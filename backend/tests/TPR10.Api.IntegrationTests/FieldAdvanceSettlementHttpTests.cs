using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using TPR10.Api.Identity.Sessions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementDraftTests;
namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementHttpTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Updated_document_receipt_recovers_exact_command_after_kind_repair()
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var input = await FieldAdvanceExpenseDocumentTests.Input(f, files, request);
        await using var db = f.CreateContext();
        var documents = await FieldAdvanceExpenseDocumentTests.Documents(f, db, files);
        var created = await documents.RegisterAsync(AuthorId, request, input with { EvidenceType = null }, await Meta(f, request), default);
        Assert.Equal(201, created.Status);
        var id = created.Value!.EntityId!.Value;
        var version = (await db.Set<ExpenseDocument>().AsNoTracking().SingleAsync(x => x.Id == id)).Version;
        var command = await Meta(f, request, version);
        Assert.Equal(200, (await documents.UpdateAsync(AuthorId, request, id, input with { EvidenceType = "receipt" }, command, default)).Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var owner = await Client(factory, f, AuthorId);
        var response = await owner.GetAsync(PathFor(request) + $"/commands/{command.CommandId}/receipt?operation=documents/update&entityId={id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(command.CommandId, receipt.GetProperty("commandId").GetGuid());
        Assert.Equal(id, receipt.GetProperty("result").GetProperty("entityId").GetGuid());
    }

    [Theory]
    [InlineData("existing-registry-guard")]
    [InlineData("queue")]
    [InlineData("comparison")]
    [InlineData("receipt")]
    public async Task Duplicate_capable_delivery_waits_for_registry_before_identity(string lane)
    {
        await using var f = await Ready(postgres); var request = await Approved(f);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(AuthorId)).Entity;
        var deliveryDb = scope.ServiceProvider.GetRequiredService<TPR10.Api.Data.Tpr10DbContext>();
        async Task<SettlementHttp.ProjectionOutcome> Validate(CancellationToken ct)
        {
            await DuplicateDocumentService.LockRegistryAsync(deliveryDb, request, ct);
            return SettlementHttp.ProjectionOutcome.Current;
        }
        var receipt = new SettlementReceiptHttp(Guid.NewGuid(), "duplicates/review", Guid.NewGuid(),
            new SettlementCommandResult(request, null, "Pending", 1, 1, 1));
        var result = lane == "existing-registry-guard"
            ? SettlementHttp.ProtectReceipt(AuthorId, request, receipt.Result, 200, Validate, registry: true)
            : lane == "receipt"
            ? SettlementHttp.ProtectReceiptLookup(AuthorId, request, receipt, Validate)
            : SettlementHttp.ProtectWork(AuthorId, Results.Ok(new { lane }), Validate);
        await using var writer = f.CreateContext();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241005)");
        var delivery = Deliver(result, scope.ServiceProvider);
        await using var observer = f.CreateContext();
        var waiting = false;
        for (var i = 0; i < 100; i++)
        {
            waiting = await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted) AS \"Value\"").SingleAsync();
            if (waiting) break;
            await Task.Delay(10);
        }
        Assert.True(waiting, "delivery must actually wait on the competing writer");
        var identityAvailable = await writer.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(7241002) AS \"Value\"").SingleAsync();
        await transaction.RollbackAsync();
        var response = await delivery;
        Assert.True(identityAvailable, "delivery must not own identity while waiting for registry");
        Assert.Equal(200, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receipt_lookup_rechecks_delivery_rights_and_entity_version(bool revoke)
    {
        await using var f = await Ready(postgres); var id = await Approved(f); var meta = await Meta(f, id);
        Assert.Equal(201, (await Save(f, id, null, Input(12m), meta)).Status);
        using var keys = new TestKeyMaterial(); await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(AuthorId)).Entity;
        var prepared = await scope.ServiceProvider.GetRequiredService<SettlementUiService>().ReceiptAsync(AuthorId, id, meta.CommandId, "rounds/create", null, default);
        await using var db = f.CreateContext();
        if (revoke) await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Capabilities, Array.Empty<string>()));
        else await db.Set<SettlementRound>().Where(x => x.RequestId == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Version, y => y.Version + 1));
        var response = await Deliver(prepared, scope.ServiceProvider);
        Assert.Equal(revoke ? 403 : 409, response.StatusCode); Assert.DoesNotContain("entityId", response.Body); Assert.Equal("private, no-store", response.CacheControl);
    }
    [Fact]
    public async Task Valid_pdf_actual_worker_upload_document_refund_attachment_and_download()
    {
        await using var f = await Ready(postgres); var id = await Approved(f); await FieldAdvanceRefundTests.SeedPaid(f, id, 1000m);
        var pdf = await FinancePdfValidationTests.Options();
        using var keys = new TestKeyMaterial(); var storage = Guid.NewGuid(); var root = "/private/tmp/t16-pdf-" + Guid.NewGuid(); Directory.CreateDirectory(root);
        try
        {
            var settings = new Dictionary<string, string?>(keys.Settings)
            {
                ["FinanceEvidence:ActiveStorageId"] = storage.ToString(),
                [$"FinanceEvidence:Roots:{storage}"] = root,
                ["FinanceEvidence:Pdf:ImageId"] = pdf.ImageId,
                ["FinanceEvidence:Pdf:DockerExecutable"] = pdf.DockerExecutable
            };
            await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, settings);
            using var client = await Client(factory, f, AuthorId);
            var bytes = FinancePdfValidationTests.Pdf();
            var csrf = await IdentityTestDriver.TokenAsync(client);
            using var message = IdentityTestDriver.Mutation(csrf, PathFor(id) + "/evidence");
            message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("application/pdf");
            var uploaded = await client.SendAsync(message); Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var proof = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var document = await SendAbsolute(client, HttpMethod.Post, PathFor(id) + "/documents/create", new
            {
                input = new { issuer = "PDF fixture", number = (string?)null, hasNoNumber = true, issuedOn = "2026-10-30", amount = "100.00", evidenceIds = new[] { proof } },
                meta = await Meta(f, id)
            });
            Assert.Equal(HttpStatusCode.Created, document.StatusCode);
            var refund = await SendAbsolute(client, HttpMethod.Post, PathFor(id) + "/refunds/create", new
            {
                input = new { amount = "100.00", returnedOn = "2026-10-30", channel = "Transfer", companyRecipient = "company", evidenceIds = new[] { proof }, adjustmentCaseId = (Guid?)null },
                meta = await Meta(f, id)
            });
            Assert.Equal(HttpStatusCode.Created, refund.StatusCode);
            var documents = await client.GetFromJsonAsync<JsonElement>(PathFor(id) + "/documents");
            Assert.Equal(proof, documents.GetProperty("items")[0].GetProperty("document").GetProperty("evidenceIds")[0].GetGuid());
            var refunds = await client.GetFromJsonAsync<JsonElement>(PathFor(id) + "/refunds");
            Assert.Equal(proof, refunds.GetProperty("items")[0].GetProperty("refund").GetProperty("evidenceIds")[0].GetGuid());
            var downloaded = await client.GetAsync(PathFor(id) + $"/evidence/{proof}");
            Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode); Assert.Equal("application/pdf", downloaded.Content.Headers.ContentType!.MediaType);
            Assert.Equal(bytes, await downloaded.Content.ReadAsByteArrayAsync());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Receipt_lookup_binds_actor_request_operation_entity_and_current_rights()
    {
        await using var f = await Ready(postgres); var id = await Approved(f); var other = await Approved(f);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId); var meta = await Meta(f, id);
        var line = new { sourceLineId = (Guid?)null, categoryId = Category, description = "receipt", incurredOn = "2026-10-30", quantity = (string?)null, rate = (string?)null, lumpSum = "12.34", allocations = Array.Empty<object>() };
        var sent = await SendAbsolute(client, HttpMethod.Post, PathFor(id) + "/rounds/create", new { input = new { lines = new[] { line } }, meta });
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var original = await sent.Content.ReadFromJsonAsync<JsonElement>();
        var commandId = meta.CommandId;
        var path = $"/commands/{commandId}/receipt?operation=rounds%2Fcreate";
        var response = await client.GetAsync(PathFor(id) + path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(commandId, receipt.GetProperty("commandId").GetGuid());
        Assert.Equal(original.ToString(), receipt.GetProperty("result").ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(PathFor(id) + $"/commands/{Guid.NewGuid()}/receipt?operation=rounds%2Fcreate")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(other) + path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(id) + path.Replace("rounds%2Fcreate", "refunds%2Fcreate"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(id) + path.Replace("rounds%2Fcreate", "rounds%2Fupdate") + "&entityId=" + original.GetProperty("entityId").GetGuid())).StatusCode);
        using var stranger = await Client(factory, f, ReviewerId);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(PathFor(id) + path)).StatusCode);
        await using var db = f.CreateContext();
        await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == AuthorId)).ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(y => y.RevokedBy, AuthorId).SetProperty(y => y.RevocationReason, "receipt test"));
        var denied = await client.GetAsync(PathFor(id) + path);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.DoesNotContain("entityId", await denied.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task Employee_round_actions_return_review_and_timing_are_scoped()
    {
        await using var f = await Ready(postgres); var (id, round) = await FieldAdvanceSettlementReviewTests.Draft(f);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var page = await client.GetFromJsonAsync<JsonElement>(PathFor(id) + "/rounds");
        var row = page.GetProperty("items")[0];
        Assert.Contains("round-draft", row.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("round-confirm", row.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
        var revision = row.GetProperty("round").GetProperty("revisionId").GetGuid();
        var path = PathFor(id) + $"/rounds/{round}/revisions/{revision}";
        var detail = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("submittedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("submissionOnTime").ValueKind);
        await FieldAdvanceSettlementReviewTests.ActivateCorrectionPolicy(f, 3);
        Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Submit(f, AuthorId, id, round,
            await FieldAdvanceSettlementReviewTests.ConfirmCommand(f, id, round))).Status);
        detail = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Empty(detail.GetProperty("allowedActions").EnumerateArray());
        Assert.NotNull(detail.GetProperty("submittedAtUtc").GetString());
        Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Review(f, ReviewerId, id, round, "Return")).Status);
        detail = await client.GetFromJsonAsync<JsonElement>(path);
        var review = detail.GetProperty("returnReview");
        Assert.Equal(revision, review.GetProperty("revisionId").GetGuid());
        Assert.NotEmpty(review.GetProperty("reason").GetString()!);
        Assert.Equal(detail.GetProperty("lines")[0].GetProperty("id").GetGuid(), review.GetProperty("affectedLineIds")[0].GetGuid());
        Assert.NotNull(review.GetProperty("correctionDueOn").GetString());
        Assert.Equal(new[] { "round-draft" }, detail.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
    }
    [Fact]
    public async Task Employee_current_declaration_projects_persisted_time_and_withdrawal()
    {
        await using var f = await Ready(postgres); var (id, _) = await FieldAdvanceSettlementReviewTests.Draft(f);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var owner = await Client(factory, f, AuthorId); using var manager = await Client(factory, f, RecipientId);
        Assert.Equal(HttpStatusCode.OK, (await SendAbsolute(manager, HttpMethod.Post, PathFor(id) + "/completion/confirm",
            new { input = new { actualEnd = "2026-10-30" }, meta = await Meta(f, id) })).StatusCode);
        foreach (var complete in new[] { true, false })
        {
            Assert.Equal(HttpStatusCode.OK, (await SendAbsolute(owner, HttpMethod.Post, PathFor(id) + "/declaration",
                new { input = new { complete }, meta = await Meta(f, id) })).StatusCode);
            var summary = await owner.GetFromJsonAsync<JsonElement>(PathFor(id)); var declaration = summary.GetProperty("declaration");
            await using var db = f.CreateContext();
            var persisted = await db.Set<SubmissionDeclaration>().SingleAsync(x => x.Id == declaration.GetProperty("id").GetGuid());
            Assert.Equal(!complete, declaration.GetProperty("isWithdrawal").GetBoolean());
            Assert.Equal(persisted.Version, declaration.GetProperty("version").GetInt64());
            Assert.Equal(persisted.CreatedAtUtc, declaration.GetProperty("declaredAtUtc").GetDateTimeOffset());
            using var snapshot = JsonDocument.Parse(persisted.SnapshotJson);
            Assert.Equal(snapshot.RootElement.GetProperty("CompletionOnTime").ToString(), declaration.GetProperty("completionOnTime").ToString());
            Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(PathFor(id))).StatusCode);
        }
    }
    [Fact]
    public async Task Employee_refund_actions_and_unknown_timing_are_server_projected()
    {
        await using var f = await Ready(postgres); var (id, proof) = await FieldAdvanceRefundTests.RefundReady(f);
        var result = await FieldAdvanceRefundTests.RefundCall(f, id, "Propose", input: FieldAdvanceRefundTests.RefundInputFor(12m, proof));
        Assert.Equal(201, result.Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var page = await client.GetFromJsonAsync<JsonElement>(PathFor(id) + "/refunds");
        var row = page.GetProperty("items")[0];
        Assert.Equal(new[] { "refund-update", "refund-confirm", "refund-cancel" }, row.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("submittedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("submissionOnTime").ValueKind);
        var summary = await client.GetFromJsonAsync<JsonElement>(PathFor(id));
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("declaration").ValueKind);
    }
    [Fact]
    public async Task Family_read_is_scoped_and_wire_money()
    {
        await using var f = await Ready(postgres); var id = await Approved(f);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var response = await client.GetAsync(PathFor(id) + "/family");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(value.GetProperty("requests").EnumerateArray());
        Assert.Equal(id, value.GetProperty("requests")[0].GetProperty("requestId").GetGuid());
        Assert.Equal(JsonValueKind.String, value.GetProperty("pageTotals").GetProperty("ownerReceived").ValueKind);
    }
    [Fact]
    public async Task Duplicate_catalog_is_available_to_scoped_reader()
    {
        await using var f = await Ready(postgres); var id = await Approved(f);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var response = await client.GetAsync(PathFor(id) + "/duplicates");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(value.GetProperty("items").EnumerateArray());
    }
    [Fact]
    public async Task Preview_returns_typed_lines_and_decimal_strings_without_raw_snapshot()
    {
        await using var f = await Ready(postgres); var id = await Approved(f);
        var saved = await Save(f, id, null, Input(12.34m)); Assert.Equal(201, saved.Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var response = await client.GetAsync(PathFor(id) + $"/rounds/{saved.Value!.EntityId}/preview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(value.TryGetProperty("snapshotJson", out _));
        Assert.Equal("12.34", value.GetProperty("total").GetString());
        Assert.Equal("12.34", value.GetProperty("lines")[0].GetProperty("amount").GetString());
        Assert.NotEmpty(value.GetProperty("confirmationHash").GetString()!);
    }
    [Fact]
    public async Task Assigned_reviewer_reads_only_current_round_without_summary_grant()
    {
        await using var f = await Ready(postgres); var (id, round) = await FieldAdvanceSettlementReviewTests.Draft(f);
        var command = await FieldAdvanceSettlementReviewTests.ConfirmCommand(f, id, round);
        Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Submit(f, AuthorId, id, round, command)).Status);
        await using var db = f.CreateContext(); var revision = await db.Set<SettlementRevision>().SingleAsync(x => x.RoundId == round);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, ReviewerId);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(id))).StatusCode);
        var path = PathFor(id) + $"/rounds/{round}/revisions/{revision.Id}";
        var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(revision.Id, value.GetProperty("revisionId").GetGuid());
        Assert.Empty(value.GetProperty("allowedActions").EnumerateArray());
        Assert.False(value.TryGetProperty("declaration", out _));
        Assert.Equal("100.00", value.GetProperty("lines")[0].GetProperty("amount").GetString());
        Assert.False(value.TryGetProperty("snapshotJson", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(id) + "/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(id) + $"/rounds/{Guid.NewGuid()}/revisions/{revision.Id}")).StatusCode);
        Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Review(f, ReviewerId, id, round)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task Document_catalog_has_typed_amount_and_only_owned_proofs()
    {
        await using var f = await Ready(postgres); var id = await Approved(f); var document = await Document(f, id, 123.45m);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var value = await client.GetFromJsonAsync<JsonElement>(PathFor(id) + "/documents");
        var item = value.GetProperty("items")[0]; Assert.Equal(document, item.GetProperty("id").GetGuid());
        Assert.Empty(item.GetProperty("allowedActions").EnumerateArray());
        Assert.Equal("123.45", item.GetProperty("document").GetProperty("amount").GetString());
        Assert.Single(item.GetProperty("document").GetProperty("evidenceIds").EnumerateArray());
        Assert.False(item.TryGetProperty("snapshotJson", out _));
    }
    [Fact]
    public async Task Assigned_round_download_only_delivers_bound_proof()
    {
        await using var f = await Ready(postgres); var (id, round) = await FieldAdvanceSettlementReviewTests.Draft(f);
        using var keys = new TestKeyMaterial(); var storage = Guid.NewGuid(); var root = "/private/tmp/t15-proof-" + Guid.NewGuid(); Directory.CreateDirectory(root);
        try
        {
            var settings = new Dictionary<string, string?>(keys.Settings) { ["FinanceEvidence:ActiveStorageId"] = storage.ToString(), [$"FinanceEvidence:Roots:{storage}"] = root, ["FinanceEvidence:MaxFileBytes"] = "1024" };
            await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, settings);
            using var owner = await Client(factory, f, AuthorId);
            var bytes = FieldAdvanceSettlementEvidenceTests.Image();
            var csrf = await IdentityTestDriver.TokenAsync(owner);
            using var message = IdentityTestDriver.Mutation(csrf, PathFor(id) + "/evidence");
            message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("image/png");
            var uploaded = await owner.SendAsync(message); Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var ticket = await uploaded.Content.ReadFromJsonAsync<JsonElement>(); var proof = ticket.GetProperty("id").GetGuid();
            Guid revision; Guid unbound;
            await using (var db = f.CreateContext())
            {
                var document = await db.Set<ExpenseDocument>().SingleAsync(x => x.RequestId == id);
                unbound = document.EvidenceId; document.EvidenceId = proof;
                document.SnapshotJson = JsonSerializer.Serialize(new ExpenseDocumentService.DocumentEvidence([proof], [ticket.GetProperty("sha256").GetString()!]) { EvidenceType = "receipt" });
                await db.SaveChangesAsync(); revision = await db.Set<SettlementRevision>().Where(x => x.RoundId == round).Select(x => x.Id).SingleAsync();
            }
            Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Submit(f, AuthorId, id, round, await FieldAdvanceSettlementReviewTests.ConfirmCommand(f, id, round))).Status);
            using var reviewer = await Client(factory, f, ReviewerId);
            var prefix = PathFor(id) + $"/rounds/{round}/revisions/{revision}/evidence/";
            var response = await reviewer.GetAsync(prefix + proof); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore);
            Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync(prefix + unbound)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync(PathFor(id) + $"/evidence/{proof}")).StatusCode);
            await using var deliveryScope = factory.Services.CreateAsyncScope();
            deliveryScope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(ReviewerId)).Entity;
            var prepared = await SettlementEndpoints.DownloadRound(id, round, revision, proof, new DefaultHttpContext { RequestServices = deliveryScope.ServiceProvider }, default);
            await using var changed = f.CreateContext();
            var boundDocument = await changed.Set<ExpenseDocument>().SingleAsync(x => x.RequestId == id);
            var originalSnapshot = boundDocument.SnapshotJson;
            boundDocument.EvidenceId = unbound; boundDocument.SnapshotJson = JsonSerializer.Serialize(new ExpenseDocumentService.DocumentEvidence([unbound], [new string('a', 64)]));
            await changed.SaveChangesAsync();
            var deniedDelivery = await Deliver(prepared, deliveryScope.ServiceProvider);
            Assert.Equal(403, deniedDelivery.StatusCode); Assert.Equal("private, no-store", deniedDelivery.CacheControl);
            Assert.DoesNotContain("PNG", deniedDelivery.Body);
            boundDocument.EvidenceId = proof; boundDocument.SnapshotJson = originalSnapshot; await changed.SaveChangesAsync();
            await changed.Set<SettlementApprovalStep>().Where(x => x.RevisionId == revision && x.StepOrder == 1).ExecuteUpdateAsync(x => x.SetProperty(y => y.AssigneePersonId, RecipientPersonId));
            Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync(prefix + proof)).StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Refund_catalog_returns_company_destination_and_scoped_proofs_as_typed_data()
    {
        await using var f = await Ready(postgres); var (id, proof) = await FieldAdvanceRefundTests.RefundReady(f);
        var result = await FieldAdvanceRefundTests.RefundCall(f, id, "Propose", input: FieldAdvanceRefundTests.RefundInputFor(123.45m, proof));
        Assert.Equal(201, result.Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var value = await client.GetFromJsonAsync<JsonElement>(PathFor(id) + "/refunds");
        var item = value.GetProperty("items")[0].GetProperty("refund");
        Assert.Equal("123.45", item.GetProperty("amount").GetString());
        Assert.Equal("บัญชีบริษัทจำลอง", item.GetProperty("companyRecipient").GetString());
        Assert.Equal(proof, item.GetProperty("evidenceIds")[0].GetGuid());
        Assert.False(item.TryGetProperty("snapshotJson", out _));
    }
    [Fact]
    public async Task Settlement_decimal_schema_matches_strict_string_wire()
    {
        var transformer = Assert.IsAssignableFrom<Microsoft.AspNetCore.OpenApi.IOpenApiSchemaTransformer>(new TPR10.Api.FieldAdvances.FieldAdvanceOpenApiTransformer());
        var options = new JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        using var services = new ServiceCollection().BuildServiceProvider();
        var property = options.GetTypeInfo(typeof(RefundInput)).Properties.Single(x => x.Name == "Amount");
        var schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.Number, Format = "double" };
        var context = new Microsoft.AspNetCore.OpenApi.OpenApiSchemaTransformerContext
        {
            DocumentName = "v1",
            ParameterDescription = null,
            JsonTypeInfo = options.GetTypeInfo(typeof(decimal)),
            JsonPropertyInfo = property,
            ApplicationServices = services
        };
        await transformer.TransformAsync(schema, context, default);
        Assert.Equal(Microsoft.OpenApi.JsonSchemaType.String, schema.Type);
        Assert.Null(schema.Format); Assert.NotNull(schema.Pattern);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assigned_round_rechecks_assignment_and_step_version_before_delivery(bool revoke)
    {
        await using var f = await Ready(postgres); var (id, round) = await FieldAdvanceSettlementReviewTests.Draft(f);
        Assert.Equal(200, (await FieldAdvanceSettlementReviewTests.Submit(f, AuthorId, id, round, await FieldAdvanceSettlementReviewTests.ConfirmCommand(f, id, round))).Status);
        await using var db = f.CreateContext(); var revision = await db.Set<SettlementRevision>().SingleAsync(x => x.RoundId == round);
        using var keys = new TestKeyMaterial(); await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(ReviewerId)).Entity;
        var prepared = await scope.ServiceProvider.GetRequiredService<SettlementUiService>().RoundAsync(ReviewerId, id, round, revision.Id, default);
        var steps = db.Set<SettlementApprovalStep>().Where(x => x.RevisionId == revision.Id && x.StepOrder == 1);
        if (revoke) await steps.ExecuteUpdateAsync(x => x.SetProperty(y => y.AssigneePersonId, RecipientPersonId));
        else await steps.ExecuteUpdateAsync(x => x.SetProperty(y => y.Version, y => y.Version + 1));
        var response = await Deliver(prepared, scope.ServiceProvider);
        Assert.Equal(revoke ? 403 : 409, response.StatusCode); Assert.DoesNotContain("lines", response.Body); Assert.Equal("private, no-store", response.CacheControl);
    }
    [Fact]
    public async Task Family_http_excludes_hidden_balances_and_reauthorizes_visible_requests()
    {
        await using var f = await Ready(postgres); await Grant(f, AuthorId, AuthorPersonId, "request.create");
        var grant = await Grant(f, RecipientId, RecipientPersonId, "request.delegate");
        var root = await Request(f, delegatePerson: RecipientPersonId);
        await using var db = f.CreateContext(); var family = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root)).FamilyId;
        var hidden = await Request(f, family, root);
        await FieldAdvanceRefundTests.SeedPaid(f, root, 100m); await FieldAdvanceRefundTests.SeedPaid(f, hidden, 9876m);
        using var keys = new TestKeyMaterial(); await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, RecipientId);
        var response = await client.GetAsync(PathFor(root) + "/family"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(hidden.ToString(), body); Assert.DoesNotContain("9876", body);
        var value = JsonSerializer.Deserialize<JsonElement>(body); Assert.False(value.GetProperty("pageComplete").GetBoolean());
        Assert.False(value.GetProperty("pageSettled").GetBoolean()); Assert.Equal("100.00", value.GetProperty("pageTotals").GetProperty("ownerReceived").GetString());
        await using var scope = factory.Services.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(RecipientId)).Entity;
        var prepared = await scope.ServiceProvider.GetRequiredService<SettlementUiService>().FamilyAsync(RecipientId, root, default);
        await db.Set<GrantAssignment>().Where(x => x.Id == grant).ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(y => y.RevokedBy, AuthorId).SetProperty(y => y.RevocationReason, "ถอนสิทธิ์จำลอง"));
        var denied = await Deliver(prepared, scope.ServiceProvider); Assert.Equal(403, denied.StatusCode);
        Assert.DoesNotContain("ownerReceived", denied.Body); Assert.Equal("private, no-store", denied.CacheControl);
    }
    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    public async Task Family_continuation_is_bounded_stable_and_never_returns_hidden_totals(int visibleCount)
    {
        await using var f = await Ready(postgres); await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await Grant(f, RecipientId, RecipientPersonId, "request.delegate");
        var root = await Request(f, delegatePerson: RecipientPersonId);
        await using var db = f.CreateContext(); var family = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root)).FamilyId;
        var expected = new HashSet<Guid> { root };
        for (var i = 1; i < visibleCount; i++) expected.Add(await Request(f, family, root, RecipientPersonId));
        var hidden = await Request(f, family, root);
        await FieldAdvanceRefundTests.SeedPaid(f, hidden, 9876m);
        await db.Set<AdvanceRequest>().Where(x => x.FamilyId == family).ExecuteUpdateAsync(x => x.SetProperty(y => y.State, "Rejected"));
        using var keys = new TestKeyMaterial(); await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, RecipientId);
        var orderedVisible = await db.Set<AdvanceRequest>().Where(x => expected.Contains(x.Id)).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        await FieldAdvanceRefundTests.SeedPaid(f, orderedVisible[0], 7m); await FieldAdvanceRefundTests.SeedPaid(f, orderedVisible[^1], 11m);
        var seen = new HashSet<Guid>(); string? cursor = null; string? firstCursor = null; var pages = 0; decimal totalAcrossPages = 0;
        do
        {
            var url = PathFor(root) + "/family?take=25" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            var response = await client.GetAsync(url); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(hidden.ToString(), body); Assert.DoesNotContain("9876", body);
            var page = JsonSerializer.Deserialize<JsonElement>(body);
            Assert.Equal("visible-page", page.GetProperty("totalsScope").GetString());
            var expectedPageTotal = page.GetProperty("requests").EnumerateArray().Sum(x => decimal.Parse(x.GetProperty("totals").GetProperty("ownerReceived").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            var pageTotal = decimal.Parse(page.GetProperty("pageTotals").GetProperty("ownerReceived").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(expectedPageTotal, pageTotal); totalAcrossPages += pageTotal;
            Assert.InRange(page.GetProperty("requests").GetArrayLength(), 0, 25);
            foreach (var row in page.GetProperty("requests").EnumerateArray()) Assert.True(seen.Add(row.GetProperty("requestId").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString(); firstCursor ??= cursor;
            Assert.True(++pages <= 6);
        } while (cursor is not null);
        Assert.True(expected.SetEquals(seen)); Assert.Equal(5, pages); Assert.NotNull(firstCursor); Assert.Equal(18m, totalAcrossPages);
        var again = await client.GetFromJsonAsync<JsonElement>(PathFor(root) + "/family?take=25&cursor=" + Uri.EscapeDataString(firstCursor));
        Assert.NotEmpty(again.GetProperty("requests").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(PathFor(root) + "/family?take=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(PathFor(root) + "/family?cursor=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(PathFor(expected.First(x => x != root)) + "/family?cursor=" + Uri.EscapeDataString(firstCursor))).StatusCode);
        await db.Set<AdvanceFamily>().Where(x => x.Id == family).ExecuteUpdateAsync(x => x.SetProperty(y => y.Version, y => y.Version + 1));
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(PathFor(root) + "/family?cursor=" + Uri.EscapeDataString(firstCursor))).StatusCode);
    }
    [Fact]
    public async Task Empty_family_scan_continues_without_disclosing_hidden_cursor_key_and_rechecks_sibling_rights()
    {
        await using var f = await Ready(postgres); await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await Grant(f, RecipientId, RecipientPersonId, "request.delegate");
        var root = await Request(f, delegatePerson: RecipientPersonId);
        await using var db = f.CreateContext(); var family = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root)).FamilyId;
        var hidden = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await using (var creation = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            var familyRow = await db.Set<AdvanceFamily>().FromSqlInterpolated($"SELECT * FROM field_advance_families WHERE id={family} FOR UPDATE").SingleAsync();
            db.Add(new AdvanceRequest { Id = hidden, WorkspaceId = WorkspaceId, ProjectId = ProjectId, FamilyId = family, RelationKind = "Supplement", SupplementOfRequestId = root, OwnerPersonId = AuthorPersonId, CreatedBy = AuthorId, State = "Rejected", FamilyVersionAtCreation = ++familyRow.Version });
            await db.SaveChangesAsync(); await creation.CommitAsync();
        }
        using var keys = new TestKeyMaterial(); await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, RecipientId);
        var first = await client.GetFromJsonAsync<JsonElement>(PathFor(root) + "/family?take=1");
        Assert.Empty(first.GetProperty("requests").EnumerateArray()); Assert.False(first.GetProperty("pageComplete").GetBoolean());
        Assert.False(first.GetProperty("pageSettled").GetBoolean());
        var cursor = first.GetProperty("nextCursor").GetString(); Assert.NotNull(cursor);
        Assert.DoesNotContain(hidden.ToString(), first.ToString()); Assert.DoesNotContain(hidden.ToString("N"), cursor);
        var second = await client.GetFromJsonAsync<JsonElement>(PathFor(root) + "/family?take=1&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Single(second.GetProperty("requests").EnumerateArray());
        using var owner = await Client(factory, f, AuthorId);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(PathFor(root) + "/family?cursor=" + Uri.EscapeDataString(cursor))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(PathFor(root) + "/family?cursor=changed" + Uri.EscapeDataString(cursor))).StatusCode);
        var sibling = await Request(f, family, root);
        var reportGrant = await Grant(f, RecipientId, RecipientPersonId, "report.read");
        await using var scope = factory.Services.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(RecipientId)).Entity;
        var prepared = await scope.ServiceProvider.GetRequiredService<SettlementUiService>().FamilyAsync(RecipientId, root, default);
        await db.Set<GrantAssignment>().Where(x => x.Id == reportGrant).ExecuteUpdateAsync(x => x.SetProperty(y => y.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(y => y.RevokedBy, AuthorId).SetProperty(y => y.RevocationReason, "ถอนสิทธิ์อ่านคำขอร่วม"));
        var denied = await Deliver(prepared, scope.ServiceProvider); Assert.Equal(403, denied.StatusCode);
        Assert.DoesNotContain(sibling.ToString(), denied.Body); Assert.DoesNotContain("pageTotals", denied.Body); Assert.Equal("private, no-store", denied.CacheControl);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(PathFor(root))).StatusCode);
    }
    private static string PathFor(Guid id) => $"/api/v1/field-advances/requests/{id}/settlement";
    [Fact]
    public async Task Owner_summary_uses_settlement_scope_and_wire_money()
    {
        await using var f = await Ready(postgres); var id = await Approved(f);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var response = await client.GetAsync(PathFor(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, value.GetProperty("requestId").GetGuid());
        Assert.Equal(JsonValueKind.String, value.GetProperty("totals").GetProperty("ownerReceived").ValueKind);
        Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore);
    }

    [Fact]
    public async Task Revocation_before_delivery_does_not_send_prepared_payload()
    {
        await using var f = await Ready(postgres); var id = await Approved(f);
        const string secretExpense = "confidential-expense-15";
        var input = Input(123); input = new([input.Lines[0] with { Description = secretExpense }]);
        var saved = await Save(f, id, null, input); Assert.Equal(201, saved.Status);
        await using (var db = f.CreateContext()) await db.Set<SettlementRound>().Where(x => x.RequestId == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Reason, secretExpense));
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(AuthorId)).Entity;
        var prepared = await scope.ServiceProvider.GetRequiredService<SettlementUiService>().PageAsync<SettlementRound>(AuthorId, id, 25, null, default);
        await using (var db = f.CreateContext()) await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Capabilities, Array.Empty<string>()));
        var response = await Deliver(prepared, scope.ServiceProvider);
        Assert.Equal(403, response.StatusCode);
        Assert.DoesNotContain(secretExpense, response.Body);
        Assert.Equal("private, no-store", response.CacheControl);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_summary_checks_expiry_and_versions(bool expired)
    {
        await using var f = await Ready(postgres); var id = await Approved(f); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(AuthorId)).Entity;
        var prepared = await scope.ServiceProvider.GetRequiredService<SettlementUiService>().SummaryAsync(AuthorId, id, default);
        if (expired) f.Clock.Advance(TimeSpan.FromMinutes(20));
        else { await using var db = f.CreateContext(); await db.Set<AdvanceRequest>().Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Version, y => y.Version + 1)); }
        var response = await Deliver(prepared, scope.ServiceProvider);
        Assert.Equal(expired ? 403 : 409, response.StatusCode); Assert.DoesNotContain("ownerReceived", response.Body);
        Assert.Equal("private, no-store", response.CacheControl);
    }
    [Fact]
    public async Task Draft_http_is_strict_scoped_and_replay_bound()
    {
        await using var f = await Ready(postgres); var id = await Approved(f); var other = await Approved(f); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId); var meta = await Meta(f, id);
        var line = new { sourceLineId = (Guid?)null, categoryId = Category, description = "ทดสอบ", incurredOn = "2026-10-30", quantity = (string?)null, rate = (string?)null, lumpSum = "12.34", allocations = Array.Empty<object>() };
        var body = new { input = new { lines = new[] { line } }, meta };
        var response = await SendAbsolute(client, HttpMethod.Post, PathFor(id) + "/rounds/create", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>(); var round = value.GetProperty("entityId").GetGuid();
        var replay = await SendAbsolute(client, HttpMethod.Post, PathFor(id) + "/rounds/create", body);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode); Assert.Equal(value.ToString(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAbsolute(client, HttpMethod.Post, PathFor(other) + $"/rounds/{round}/update", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAbsolute(client, HttpMethod.Post, PathFor(id) + "/rounds/create", new { body.input, meta, actor = AuthorId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAbsolute(client, HttpMethod.Post, PathFor(id) + "/close", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(PathFor(id) + "/rounds/create", body)).StatusCode);
    }
    [Fact]
    public async Task Pagination_is_bounded_stable_and_cursor_bound_to_request()
    {
        await using var f = await Ready(postgres); var id = await Approved(f); var other = await Approved(f);
        for (var i = 0; i < 3; i++) Assert.Equal(201, (await Save(f, id, null, Input(10))).Status);
        using var keys = new TestKeyMaterial(); await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var first = await client.GetFromJsonAsync<JsonElement>(PathFor(id) + "/rounds?take=2");
        Assert.Equal(2, first.GetProperty("items").GetArrayLength()); var cursor = Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!);
        var second = await client.GetFromJsonAsync<JsonElement>(PathFor(id) + "/rounds?take=2&cursor=" + cursor);
        Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(second.GetProperty("items")[0].GetProperty("id").GetGuid(), first.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(PathFor(other) + "/rounds?cursor=" + cursor)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(PathFor(id) + "/rounds?take=101")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_and_delegate_evidence_roundtrip_is_scoped_without_Payer(bool delegated)
    {
        await using var f = await Ready(postgres);
        var id = await Approved(f, delegated ? RecipientPersonId : null); var other = await Approved(f);
        if (delegated) await Grant(f, RecipientId, RecipientPersonId, "request.delegate");
        var actor = delegated ? RecipientId : AuthorId;
        using var keys = new TestKeyMaterial(); var storage = Guid.NewGuid(); var root = "/private/tmp/t15-" + Guid.NewGuid(); Directory.CreateDirectory(root);
        try
        {
            var settings = new Dictionary<string, string?>(keys.Settings) { ["FinanceEvidence:ActiveStorageId"] = storage.ToString(), [$"FinanceEvidence:Roots:{storage}"] = root, ["FinanceEvidence:MaxFileBytes"] = "1024" };
            await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, settings);
            using var client = await Client(factory, f, actor);
            async Task<HttpResponseMessage> Upload(byte[] bytes, string type = "image/png")
            {
                var csrf = await IdentityTestDriver.TokenAsync(client);
                using var message = IdentityTestDriver.Mutation(csrf, PathFor(id) + "/evidence");
                message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new(type);
                return await client.SendAsync(message);
            }
            var bytes = FieldAdvanceSettlementEvidenceTests.Image();
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Upload(new byte[1025])).StatusCode);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Upload(bytes, "text/plain")).StatusCode);
            var uploaded = await Upload(bytes); Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var ticket = await uploaded.Content.ReadFromJsonAsync<JsonElement>(); var evidenceId = ticket.GetProperty("id").GetGuid();
            var downloaded = await client.GetAsync(PathFor(id) + $"/evidence/{evidenceId}");
            Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode); Assert.Equal(bytes, await downloaded.Content.ReadAsByteArrayAsync());
            Assert.True(downloaded.Headers.CacheControl!.Private); Assert.True(downloaded.Headers.CacheControl.NoStore);
            Assert.Equal("attachment", downloaded.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("nosniff", Assert.Single(downloaded.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(other) + $"/evidence/{evidenceId}")).StatusCode);
            await using var db = f.CreateContext(); Assert.DoesNotContain(await db.Set<AdvancePaymentParticipation>().ToArrayAsync(), x => x.PersonId == (delegated ? RecipientPersonId : AuthorPersonId) && x.Role == "Payer");
            await db.Set<GrantProposal>().Where(x => x.RecipientId == actor).ExecuteUpdateAsync(x => x.SetProperty(y => y.Capabilities, Array.Empty<string>()));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(id) + $"/evidence/{evidenceId}")).StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Changed_entity_version_cannot_deliver_prepared_page()
    {
        await using var f = await Ready(postgres); var id = await Approved(f); await Save(f, id, null, Input(12));
        using var keys = new TestKeyMaterial(); await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(AuthorId)).Entity;
        var prepared = await scope.ServiceProvider.GetRequiredService<SettlementUiService>().PageAsync<SettlementRound>(AuthorId, id, 25, null, default);
        await using (var db = f.CreateContext()) await db.Set<SettlementRound>().Where(x => x.RequestId == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Version, y => y.Version + 1));
        var response = await Deliver(prepared, scope.ServiceProvider); Assert.Equal(409, response.StatusCode); Assert.DoesNotContain("items", response.Body);
    }

    [Fact]
    public async Task Expiry_during_serialization_does_not_send_buffered_payload()
    {
        await using var f = await Ready(postgres); var id = await Approved(f); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(AuthorId)).Entity;
        var prepared = SettlementHttp.Protect(AuthorId, id, new ExpiringResult(() => f.Clock.Advance(TimeSpan.FromMinutes(20))), _ => Task.FromResult(SettlementHttp.ProjectionOutcome.Current));
        var response = await Deliver(prepared, scope.ServiceProvider);
        Assert.Equal(403, response.StatusCode); Assert.DoesNotContain("prepared-secret", response.Body); Assert.Equal("private, no-store", response.CacheControl);
    }
    private sealed class ExpiringResult(Action expire) : IResult
    {
        public async Task ExecuteAsync(HttpContext c) { await Results.Json(new { secret = "prepared-secret" }).ExecuteAsync(c); expire(); }
    }
    [Fact]
    public async Task Changed_session_account_denies_prepared_summary()
    {
        await using var f = await Ready(postgres); var id = await Approved(f); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope(); var session = scope.ServiceProvider.GetRequiredService<RequestSession>();
        session.Entity = (await f.SessionAsync(AuthorId)).Entity;
        var prepared = await scope.ServiceProvider.GetRequiredService<SettlementUiService>().SummaryAsync(AuthorId, id, default);
        session.Entity = (await f.SessionAsync(RecipientId)).Entity;
        var response = await Deliver(prepared, scope.ServiceProvider); Assert.Equal(403, response.StatusCode); Assert.DoesNotContain("ownerReceived", response.Body);
    }
    [Fact]
    public async Task Expired_http_session_is_401_and_numeric_money_is_400()
    {
        await using var f = await Ready(postgres); var id = await Approved(f); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, AuthorId);
        var payload = new { input = new { amount = 1.23m }, meta = await Meta(f, id) };
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAbsolute(client, HttpMethod.Post, PathFor(id) + $"/incidents/{Guid.NewGuid()}/shortfall", payload)).StatusCode);
        f.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(PathFor(id))).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_command_guard_can_deny_receipt_without_rolling_back(bool revoke)
    {
        await using var f = await Ready(postgres); var id = await Approved(f); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(AuthorId)).Entity;
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.ContentType = "application/json";
        var command = await Meta(f, id);
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new SettlementBody<RoundInput>(Input(12), command), SettlementHttp.Json));
        var prepared = await SettlementEndpoints.Command<RoundInput, SettlementRound>(id, context, SettlementActions.Draft,
            (sp, actor, request, entity, body, ct) => sp.GetRequiredService<SettlementDraftService>().SaveAsync(actor, request, null, body.Input, body.Meta, ct),
            (_, _) => null, null, default);
        await using var db = f.CreateContext();
        Assert.True(await db.Set<SettlementRound>().AnyAsync(x => x.RequestId == id && x.CommandId == command.CommandId));
        if (revoke) await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Capabilities, Array.Empty<string>()));
        else await db.Set<SettlementRound>().Where(x => x.RequestId == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Version, y => y.Version + 1));
        var response = await Deliver(prepared, scope.ServiceProvider); Assert.Equal(revoke ? 403 : 409, response.StatusCode); Assert.DoesNotContain("entityId", response.Body);
        Assert.Equal("private, no-store", response.CacheControl);
        Assert.True(await db.Set<SettlementRound>().AnyAsync(x => x.RequestId == id && x.CommandId == command.CommandId));
    }
    [Fact]
    public async Task OpenApi_endpoint_metadata_has_actual_statuses_and_no_worker_close()
    {
        await using var f = await Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().Where(x => x.Metadata.GetMetadata<SettlementApiMetadata>() is not null).ToArray();
        Assert.NotEmpty(endpoints);
        int[] Statuses(string suffix) => endpoints.Single(x => x.RoutePattern.RawText == "/api/v1/field-advances/requests/{requestId}/settlement" + suffix)
            .Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>().Select(x => x.StatusCode).Distinct().Order().ToArray();
        Assert.Equal(new[] { 201, 400, 401, 403, 409, 413, 415, 422, 503 }, Statuses("/rounds/create"));
        Assert.Equal(new[] { 200, 400, 401, 403, 409, 503 }, Statuses("/rounds"));
        var download = endpoints.Single(x => x.RoutePattern.RawText == "/api/v1/field-advances/requests/{requestId}/settlement/evidence/{entityId}");
        Assert.Equal(new[] { "application/pdf", "image/jpeg", "image/png" }, download.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>()
            .Where(x => x.StatusCode == 200).SelectMany(x => x.ContentTypes).Distinct().Order());
        Assert.DoesNotContain(endpoints, x => x.RoutePattern.RawText == "/api/v1/field-advances/requests/{requestId}/settlement/close");
    }
    [Fact]
    public async Task Operation_authority_delivers_receipt_without_granting_report_read()
    {
        await using var f = await Ready(postgres); var id = await Approved(f);
        await Grant(f, RecipientId, RecipientPersonId, "request.approve");
        await using (var db = f.CreateContext())
        {
            var proposal = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == RecipientId);
            proposal.PrimaryApprovalLevels = ["project-manager"]; await db.SaveChangesAsync();
        }
        using var keys = new TestKeyMaterial(); await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var client = await Client(factory, f, RecipientId);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(id))).StatusCode);
        var response = await SendAbsolute(client, HttpMethod.Post, PathFor(id) + "/completion/confirm", new { input = new { actualEnd = "2026-10-30" }, meta = await Meta(f, id) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(id, receipt.GetProperty("requestId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(PathFor(id))).StatusCode);
    }
    private static Task<HttpResponseMessage> SendAbsolute(HttpClient client, HttpMethod method, string path, object body)
        => Send(client, method, path.Replace("/api/v1/field-advances/", ""), body);
    private static async Task<(int StatusCode, string Body, string CacheControl)> Deliver(IResult result, IServiceProvider services)
    {
        var c = new DefaultHttpContext { RequestServices = services }; c.Response.Body = new MemoryStream();
        await result.ExecuteAsync(c); c.Response.Body.Position = 0;
        return (c.Response.StatusCode, await new StreamReader(c.Response.Body).ReadToEndAsync(), c.Response.Headers.CacheControl.ToString());
    }
}
