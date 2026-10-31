using System.Net;
using Microsoft.AspNetCore.Http;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Identity.Data;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.FieldAdvances.Evidence;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePaymentHttpTests(PostgresFixture postgres)
{
    private const string Root = "/api/v1/field-advances/";
    private async Task<FieldAdvanceFixture> Ready()
    {
        var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await FieldAdvanceGrantTests.AuthorityAsync(f, RecipientId, RecipientPersonId);
        await FieldAdvanceGrantTests.AuthorityAsync(f, AuthorId, AuthorPersonId);
        await using var db = f.CreateContext();
        foreach (var grant in await db.Set<GrantProposal>().ToArrayAsync())
            grant.Capabilities = grant.RecipientId == RecipientId ? ["payment.record"] : grant.RecipientId == AuthorId ? ["request.create"] : [];
        await db.SaveChangesAsync(); return f;
    }
    [Fact]
    public async Task Ui_context_and_scopes_use_current_payment_rights()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var payer = await Client(factory, f, RecipientId);
        var response = await payer.GetAsync(Root + $"requests/{id}/payment-context");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var context = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("installments", context.GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()));
        var scopes = await payer.GetFromJsonAsync<JsonElement[]>(Root + "payment-scopes");
        Assert.Single(scopes!); Assert.Contains("payment.record", scopes![0].GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await payer.GetAsync(Root + "payment-scopes?page=0")).StatusCode);
        await using (var db = f.CreateContext())
            await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "report.read" }));
        context = await payer.GetFromJsonAsync<JsonElement>(Root + $"requests/{id}/payment-context");
        Assert.Empty(context.GetProperty("allowedActions").EnumerateArray());
        Assert.Empty((await payer.GetFromJsonAsync<JsonElement[]>(Root + "payment-scopes"))!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ui_prepared_actions_are_not_delivered_after_capability_loss_even_with_report_read(bool scopes)
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        var session = await f.SessionAsync(RecipientId); await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session.Entity;
        var ui = scope.ServiceProvider.GetRequiredService<PaymentUiService>();
        var result = scopes ? await ui.ScopesAsync(RecipientId, 1, 25, default) : await ui.ContextAsync(RecipientId, id, default);
        await using (var db = f.CreateContext())
            await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "report.read" }));
        Assert.Equal(200, (await scope.ServiceProvider.GetRequiredService<PaymentReadService>().GetAsync(RecipientId, id, default)).Status);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context); Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0; var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("allowedActions", body); Assert.DoesNotContain("payment.record", body);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("person")]
    [InlineData("hold")]
    [InlineData("family-version")]
    public async Task Ui_prepared_context_rechecks_expiry_person_and_family_hold(string change)
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        var session = await f.SessionAsync(RecipientId); await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session.Entity;
        var result = await scope.ServiceProvider.GetRequiredService<PaymentUiService>().ContextAsync(RecipientId, id, default);
        await using (var db = f.CreateContext())
        {
            if (change == "expired") f.Clock.Advance(TimeSpan.FromMinutes(20));
            else if (change == "person") await db.Set<FinanceAccountLink>().Where(x => x.UserId == RecipientId).ExecuteUpdateAsync(x => x.SetProperty(p => p.EndedAtUtc, f.Clock.GetUtcNow()).SetProperty(p => p.Version, p => p.Version + 1).SetProperty(p => p.EndedBy, AuthorId).SetProperty(p => p.EndReason, "test identity ended"));
            else if (change == "family-version")
            {
                var family = await db.Set<AdvanceRequest>().Where(x => x.Id == id).Select(x => x.FamilyId).SingleAsync();
                await db.Set<AdvanceFamily>().Where(x => x.Id == family).ExecuteUpdateAsync(x => x.SetProperty(p => p.Version, p => p.Version + 1));
                await db.Set<AdvanceRequest>().Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Version, p => p.Version + 1));
            }
            else
            {
                var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
                db.Add(new AdvancePaymentHold
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WorkspaceId,
                    ProjectId = ProjectId,
                    FamilyId = request.FamilyId,
                    Code = "PaymentMismatch",
                    Reason = "test family hold",
                    CreatedBy = RecipientId,
                    CreatedByPersonId = RecipientPersonId,
                    CreatedAtUtc = f.Clock.GetUtcNow()
                });
                await db.SaveChangesAsync();
            }
        }
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context); Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0; Assert.DoesNotContain("allowedActions", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    // คั่นระหว่าง prepare/delivery ด้วย commit จริงอีก connection โดยไม่ใช้ sleep หรือ production hook
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_draft_commit_between_context_prepare_and_delivery_distinguishes_stale_from_revoked(bool revoke)
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres);
        var draft = await FieldAdvanceRequestLifecycleTests.Create(f);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        var session = await f.SessionAsync(AuthorId);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session.Entity;
        var reader = scope.ServiceProvider.GetRequiredService<TPR10.Api.Data.Tpr10DbContext>();
        await reader.Database.OpenConnectionAsync();
        var readerPid = ((Npgsql.NpgsqlConnection)reader.Database.GetDbConnection()).ProcessID;
        var prepared = await scope.ServiceProvider.GetRequiredService<PaymentUiService>().ContextAsync(AuthorId, draft.Id, default);
        Assert.Null(reader.Database.CurrentTransaction);

        // ContextAsync คืน IResult หลังปล่อย transaction จึงเป็น barrier ก่อน final serialization/authorization
        await using (var writer = f.CreateContext())
        {
            await writer.Database.OpenConnectionAsync();
            Assert.NotEqual(readerPid, ((Npgsql.NpgsqlConnection)writer.Database.GetDbConnection()).ProcessID);
            var writerSession = await f.SessionAsync(AuthorId);
            var updated = await FieldAdvanceRequestLifecycleTests.Draft(f, writer, writerSession).UpdateAsync(
                AuthorId, draft.Id, draft.Content with { Purpose = "แก้รายละเอียดระหว่างเตรียมส่งข้อมูล" },
                FieldAdvanceRequestLifecycleTests.Meta(draft.Version), default);
            Assert.Equal(200, updated.Status);
            Assert.Equal(draft.Version + 1, updated.Value!.Version);
            Assert.Null(writer.Database.CurrentTransaction);
            if (revoke)
            {
                await using var tx = await writer.Database.BeginTransactionAsync();
                await writer.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
                var changed = await writer.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, Array.Empty<string>()));
                Assert.True(changed > 0);
                await tx.CommitAsync();
            }
        }
        // อ่านจาก connection เดิมหลัง commit ยืนยันทั้ง version และสิทธิ์จริง ไม่เดาจากHTTP403
        reader.ChangeTracker.Clear();
        var committed = await reader.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == draft.Id);
        Assert.Equal(draft.Version + 1, committed.Version);
        Assert.Equal(AuthorPersonId, committed.OwnerPersonId);
        Assert.Equal(revoke ? 403 : 200,
            (await scope.ServiceProvider.GetRequiredService<PaymentReadService>().GetAsync(AuthorId, draft.Id, default)).Status);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();
        await prepared.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("allowedActions", body);
        Assert.DoesNotContain("workflow", body);
        Assert.Equal("private, no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(revoke ? 403 : 409, context.Response.StatusCode);
    }

    [Fact]
    public async Task Owner_unchanged_prepared_context_delivers_current_projection()
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres);
        var draft = await FieldAdvanceRequestLifecycleTests.Create(f);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(AuthorId)).Entity;
        var prepared = await scope.ServiceProvider.GetRequiredService<PaymentUiService>().ContextAsync(AuthorId, draft.Id, default);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();
        await prepared.ExecuteAsync(context);
        Assert.Equal(200, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(draft.Version, body.RootElement.GetProperty("requestVersion").GetInt64());
        Assert.Equal(AuthorPersonId, body.RootElement.GetProperty("workflow").GetProperty("ownerPersonId").GetGuid());
    }

    [Fact]
    public async Task Final_identity_expiry_overrides_stale_projection_without_delivering_payload()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(AuthorId)).Entity;
        var validated = false;
        var prepared = PaymentHttp.ProtectProjection(AuthorId, [id], Results.Json(new { marker = "must-not-deliver" }), _ =>
        {
            validated = true;
            f.Clock.Advance(TimeSpan.FromMinutes(20));
            return Task.FromResult(PaymentHttp.ProjectionOutcome.Stale);
        });
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();
        await prepared.ExecuteAsync(context);
        Assert.True(validated);
        Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("must-not-deliver", body);
        Assert.Contains("finance-access-denied", body);
    }

    [Fact]
    public async Task Evidence_configuration_binds_GUID_roots_through_real_registration()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var storage = Guid.NewGuid(); var archive = Guid.NewGuid();
        var settings = new Dictionary<string, string?>(keys.Settings)
        {
            ["FinanceEvidence:ActiveStorageId"] = storage.ToString(),
            [$"FinanceEvidence:Roots:{storage}"] = "/private/tmp",
            [$"FinanceEvidence:Roots:{archive}"] = "/private/var/tmp"
        };
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, settings);
        var options = factory.Services.GetRequiredService<FinanceEvidenceOptions>();
        Assert.Equal(storage, options.ActiveStorageId); Assert.Equal(2, options.Roots.Count);
        Assert.Equal("/private/tmp", options.Roots[storage]); Assert.Equal("/private/var/tmp", options.Roots[archive]);
        options.Validate(); Assert.Equal(10 * 1024 * 1024, options.MaxFileBytes);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("empty-guid")]
    [InlineData("relative-root")]
    [InlineData("blank-root")]
    [InlineData("missing-active")]
    [InlineData("limit")]
    public async Task Evidence_registration_rejects_invalid_keys_roots_and_limits(string invalid)
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial(); var storage = Guid.NewGuid();
        var settings = new Dictionary<string, string?>(keys.Settings)
        {
            ["FinanceEvidence:ActiveStorageId"] = storage.ToString(),
            [$"FinanceEvidence:Roots:{storage}"] = "/private/tmp"
        };
        if (invalid == "malformed") settings["FinanceEvidence:Roots:not-a-guid"] = "/private/tmp";
        if (invalid == "duplicate") settings[$"FinanceEvidence:Roots:{storage:N}"] = "/private/tmp";
        if (invalid == "empty-guid") settings[$"FinanceEvidence:Roots:{Guid.Empty}"] = "/private/tmp";
        if (invalid == "relative-root") settings[$"FinanceEvidence:Roots:{storage}"] = "relative";
        if (invalid == "blank-root") settings[$"FinanceEvidence:Roots:{storage}"] = " ";
        if (invalid == "missing-active") settings["FinanceEvidence:ActiveStorageId"] = Guid.NewGuid().ToString();
        if (invalid == "limit") settings["FinanceEvidence:MaxFiles"] = "11";
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, settings);
        Assert.Throws<InvalidOperationException>(() => factory.Services.GetRequiredService<FinanceEvidenceOptions>());
    }

    private static PaymentCommand Meta() => new(Guid.NewGuid(), 1, 1, null, "ทดสอบ");
    private static object Input() => new { amount = "100.00", destination = new PaymentDestination("Cash", AuthorPersonId, null, null, null) };

    [Fact]
    public async Task Expiry_during_serialization_must_not_disclose_prepared_payload()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        var session = await f.SessionAsync(RecipientId); await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session.Entity;
        var result = PaymentHttp.Protect(RecipientId, id, Results.Json(new ExpiringPayload(() => f.Clock.Advance(TimeSpan.FromMinutes(20)))));
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context); Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0; Assert.DoesNotContain("secret-account", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
    private sealed class ExpiringPayload(Action expire)
    {
        public string AccountNumber { get { expire(); return "secret-account"; } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_response_and_download_recheck_rights_at_actual_execution(bool file)
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        var session = await f.SessionAsync(RecipientId);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session.Entity;
        var service = scope.ServiceProvider.GetRequiredService<PaymentReadService>();
        Assert.Equal(200, (await service.GetAsync(RecipientId, id, default)).Status);
        var protectedResult = PaymentHttp.Protect(RecipientId, id,
            file ? Results.File(new byte[] { 17, 29, 53 }, "application/pdf", "evidence.pdf") : Results.Json(new { accountNumber = "protected-account" }));
        await using (var db = f.CreateContext())
            await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, Array.Empty<string>()));
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; context.Response.Body = new MemoryStream();
        await protectedResult.ExecuteAsync(context);
        Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0; var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain("protected-account", body); Assert.DoesNotContain("evidence.pdf", body);
    }

    private static async Task<Guid> ExistingEvidence(FieldAdvanceFixture f, Guid requestId)
    {
        await using var db = f.CreateContext();
        var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId);
        var evidence = new AdvanceEvidence
        {
            Id = Guid.NewGuid(),
            WorkspaceId = request.WorkspaceId,
            ProjectId = request.ProjectId,
            FamilyId = request.FamilyId,
            RequestId = requestId,
            Sha256 = new('e', 64),
            Length = 3,
            ContentType = "application/pdf",
            State = "Ready",
            CreatedBy = RecipientId,
            CreatedByPersonId = RecipientPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        };
        db.Add(evidence); await db.SaveChangesAsync(); return evidence.Id;
    }

    private static async Task<(Guid Id, Guid ProposalId, Guid Person)> AssignedDisposition(FieldAdvanceFixture f)
    {
        var person = Guid.NewGuid();
        await using (var db = f.CreateContext())
        {
            db.Add(new FinancePerson { Id = person, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "test final approver" });
            db.Add(new FinanceAccountLink { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, UserId = AliasAccountId, PersonId = person, CreatedBy = AuthorId, Reason = "test final approver", CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1) });
            await db.SaveChangesAsync();
        }
        var assignment = await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, person);
        await using (var db = f.CreateContext())
        {
            var grant = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == assignment);
            await db.Set<GrantProposal>().Where(x => x.Id == grant.ProposalId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve" })
                .SetProperty(p => p.ApprovalLevels, new[] { "director" }).SetProperty(p => p.PrimaryApprovalLevels, new[] { "director" }));
        }
        var request = await FieldAdvanceRequestLifecycleTests.Submit(f, await FieldAdvanceRequestLifecycleTests.Create(f));
        foreach (var actor in new[] { ReviewerId, RecipientId, AliasAccountId })
        {
            var approved = await FieldAdvanceRequestLifecycleTests.Decide(f, request, "Approve", actor);
            Assert.Equal(200, approved.Status); request = approved.Value!;
        }
        Assert.Equal("Approved", request.State);
        var proposalId = Guid.NewGuid();
        await using (var db = f.CreateContext())
        {
            var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request.Id); var receipt = FieldAdvancePaymentFixture.Receipt(); receipt.ActorId = ReviewerId; db.Add(receipt);
            db.Add(new AdvancePaymentProposal
            {
                Id = proposalId,
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                FamilyId = r.FamilyId,
                RequestId = r.Id,
                CommandId = receipt.CommandId,
                Kind = "PaymentDisposition",
                FamilyVersion = 1,
                RequestVersion = r.Version,
                ContentHash = new('a', 64),
                BeforeJson = "{}",
                AfterJson = "{\"Outcome\":\"TerminateRemaining\"}",
                ProposedBy = ReviewerId,
                ProposedByPersonId = ReviewerPersonId,
                Reason = "test assigned proposal"
            });
            await db.SaveChangesAsync();
        }
        return (request.Id, proposalId, person);
    }

    [Fact]
    public async Task Assigned_disposition_confirmer_can_read_only_assigned_proposal_context_not_source_request()
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres); using var keys = new TestKeyMaterial();
        var request = await AssignedDisposition(f); var proposalId = request.ProposalId;
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings); using var final = await Client(factory, f, AliasAccountId);
        Assert.Equal(HttpStatusCode.Forbidden, (await final.GetAsync(Root + $"requests/{request.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await final.GetAsync(Root + $"payment-proposals/{proposalId}")).StatusCode);
        var catalog = await final.GetFromJsonAsync<JsonElement>(Root + $"requests/{request.Id}/installments");
        Assert.Single(catalog.GetProperty("proposals").EnumerateArray()); Assert.Empty(catalog.GetProperty("installments").EnumerateArray());
        Assert.Empty(catalog.GetProperty("evidence").EnumerateArray());
        Assert.Equal(3, catalog.EnumerateObject().Count());
        Assert.Single((await final.GetFromJsonAsync<JsonElement[]>(Root + $"payments?workspaceId={WorkspaceId}&projectId={ProjectId}"))!);
        var evidenceId = await ExistingEvidence(f, request.Id);
        var evidenceResponse = await final.GetAsync(Root + $"payment-evidence/{evidenceId}");
        Assert.Equal(HttpStatusCode.Forbidden, evidenceResponse.StatusCode);
        Assert.DoesNotContain(new string('e', 64), await evidenceResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await final.GetAsync(Root + $"requests/{request.Id}")).StatusCode);
        await using (var db = f.CreateContext())
            await db.Set<GrantProposal>().Where(x => x.RecipientId == AliasAccountId).ExecuteUpdateAsync(x => x.SetProperty(p => p.PrimaryApprovalLevels, Array.Empty<string>()));
        Assert.Equal(HttpStatusCode.Forbidden, (await final.GetAsync(Root + $"payment-proposals/{proposalId}")).StatusCode);
    }

    [Fact]
    public async Task Task14_limited_context_has_no_source_account_installment_or_evidence_fields()
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres); using var keys = new TestKeyMaterial();
        var request = await AssignedDisposition(f);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var final = await Client(factory, f, AliasAccountId);
        var response = await final.GetAsync(Root + $"requests/{request.Id}/payment-context");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("disposition", body.GetProperty("variant").GetString());
        Assert.Equal(new[] { "familyVersion", "proposals", "requestId", "requestVersion", "variant" }, body.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Single(body.GetProperty("proposals").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await final.GetAsync(Root + $"requests/{request.Id}/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await final.GetAsync(Root + $"requests/{request.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await final.GetAsync(Root + $"payment-evidence/{await ExistingEvidence(f, request.Id)}")).StatusCode);
    }

    [Theory]
    [InlineData("assignment")]
    [InlineData("person")]
    [InlineData("expiry")]
    public async Task Task14_prepared_limited_context_cannot_survive_assignment_or_identity_loss_with_report_read(string change)
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres); using var keys = new TestKeyMaterial();
        var request = await AssignedDisposition(f);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        var session = await f.SessionAsync(AliasAccountId); await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session.Entity;
        var prepared = await scope.ServiceProvider.GetRequiredService<PaymentUiService>().ContextAsync(AliasAccountId, request.Id, default);
        await using (var db = f.CreateContext())
        {
            if (change == "person") await db.Set<FinanceAccountLink>().Where(x => x.UserId == AliasAccountId).ExecuteUpdateAsync(x => x.SetProperty(p => p.EndedAtUtc, f.Clock.GetUtcNow()).SetProperty(p => p.Version, p => p.Version + 1).SetProperty(p => p.EndedBy, AuthorId).SetProperty(p => p.EndReason, "test"));
            else if (change == "expiry") await db.Set<GrantProposal>().Where(x => x.RecipientId == AliasAccountId).ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow()));
            else await db.Set<GrantProposal>().Where(x => x.RecipientId == AliasAccountId).ExecuteUpdateAsync(x => x.SetProperty(p => p.PrimaryApprovalLevels, Array.Empty<string>()));
        }
        if (change != "person")
        {
            var grant = await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, request.Person);
            await using var db = f.CreateContext(); var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == grant);
            await db.Set<GrantProposal>().Where(x => x.Id == assignment.ProposalId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "report.read" }));
            Assert.Equal(200, (await scope.ServiceProvider.GetRequiredService<PaymentReadService>().GetAsync(AliasAccountId, request.Id, default)).Status);
        }
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; context.Response.Body = new MemoryStream();
        await prepared.ExecuteAsync(context); Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0; Assert.DoesNotContain(request.ProposalId.ToString(), await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prepared_full_catalog_requires_read_even_when_final_assignment_survives(bool expire)
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres); using var keys = new TestKeyMaterial();
        var request = await AssignedDisposition(f);
        var installment = await new FieldAdvancePaymentFixture(f).SeedInstallmentAsync(request.Id);
        var readGrant = await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, request.Person);
        await using (var db = f.CreateContext())
        {
            var proposal = (await db.Set<GrantAssignment>().SingleAsync(x => x.Id == readGrant)).ProposalId;
            await db.Set<GrantProposal>().Where(x => x.Id == proposal).ExecuteUpdateAsync(x => x
                .SetProperty(p => p.Capabilities, new[] { "report.read" })
                .SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow().AddMinutes(1)));
        }
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        var session = await f.SessionAsync(AliasAccountId); await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session.Entity;
        var service = scope.ServiceProvider.GetRequiredService<PaymentReadService>();
        Assert.Single((await service.InstallmentsAsync(AliasAccountId, request.Id, default)).Value!.Installments);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; context.Response.Body = new MemoryStream();
        var prepared = await PaymentEndpoints.Installments(request.Id, service, context, default);
        if (expire) f.Clock.Advance(TimeSpan.FromMinutes(2));
        else
        {
            await using var db = f.CreateContext();
            await db.Set<GrantAssignment>().Where(x => x.Id == readGrant).ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(p => p.RevokedBy, AuthorId).SetProperty(p => p.RevocationReason, "test revoke read"));
        }
        var access = scope.ServiceProvider.GetRequiredService<PaymentAccess>();
        var scopedDb = scope.ServiceProvider.GetRequiredService<TPR10.Api.Data.Tpr10DbContext>();
        await using (var tx = await scopedDb.Database.BeginTransactionAsync())
        {
            Assert.False((await access.CheckAsync(AliasAccountId, request.Id, "read", default)).Allowed);
            Assert.True(await PaymentHttp.AssignedDisposition(scopedDb, access, AliasAccountId, request.Id, default));
        }
        await prepared.ExecuteAsync(context);
        Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain(installment.Installment.Id.ToString(), body);
        Assert.DoesNotContain("installments", body);
        var restricted = await service.InstallmentsAsync(AliasAccountId, request.Id, default);
        Assert.Equal(200, restricted.Status); Assert.Empty(restricted.Value!.Installments); Assert.Single(restricted.Value.Proposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task14_prepared_full_context_requires_read_even_when_final_assignment_survives(bool expire)
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres); using var keys = new TestKeyMaterial();
        var request = await AssignedDisposition(f);
        var installment = await new FieldAdvancePaymentFixture(f).SeedInstallmentAsync(request.Id);
        var readGrant = await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, request.Person);
        await using (var db = f.CreateContext())
        {
            var proposal = (await db.Set<GrantAssignment>().SingleAsync(x => x.Id == readGrant)).ProposalId;
            await db.Set<GrantProposal>().Where(x => x.Id == proposal).ExecuteUpdateAsync(x => x
                .SetProperty(p => p.Capabilities, new[] { "report.read" })
                .SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow().AddMinutes(1)));
        }
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        var session = await f.SessionAsync(AliasAccountId); await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session.Entity;
        var service = scope.ServiceProvider.GetRequiredService<PaymentReadService>();
        Assert.Single((await service.InstallmentsAsync(AliasAccountId, request.Id, default)).Value!.Installments);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; context.Response.Body = new MemoryStream();
        var prepared = await scope.ServiceProvider.GetRequiredService<PaymentUiService>().ContextAsync(AliasAccountId, request.Id, default);
        if (expire) f.Clock.Advance(TimeSpan.FromMinutes(2));
        else
        {
            await using var db = f.CreateContext();
            await db.Set<GrantAssignment>().Where(x => x.Id == readGrant).ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(p => p.RevokedBy, AuthorId).SetProperty(p => p.RevocationReason, "test revoke read"));
        }
        var access = scope.ServiceProvider.GetRequiredService<PaymentAccess>();
        var scopedDb = scope.ServiceProvider.GetRequiredService<TPR10.Api.Data.Tpr10DbContext>();
        await using (var tx = await scopedDb.Database.BeginTransactionAsync())
        {
            Assert.False((await access.CheckAsync(AliasAccountId, request.Id, "read", default)).Allowed);
            Assert.True(await PaymentHttp.AssignedDisposition(scopedDb, access, AliasAccountId, request.Id, default));
        }
        await prepared.ExecuteAsync(context);
        Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.DoesNotContain(installment.Installment.Id.ToString(), body);
        Assert.DoesNotContain("installments", body);
        var restricted = await service.InstallmentsAsync(AliasAccountId, request.Id, default);
        Assert.Equal(200, restricted.Status); Assert.Empty(restricted.Value!.Installments); Assert.Single(restricted.Value.Proposals);
    }

    [Fact]
    public async Task Prepared_restricted_catalog_requires_disposition_even_if_read_is_later_granted()
    {
        await using var f = await FieldAdvanceRequestLifecycleTests.Ready(postgres); using var keys = new TestKeyMaterial();
        var request = await AssignedDisposition(f);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        var session = await f.SessionAsync(AliasAccountId); await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session.Entity;
        var service = scope.ServiceProvider.GetRequiredService<PaymentReadService>();
        var restricted = await service.InstallmentsAsync(AliasAccountId, request.Id, default);
        Assert.Equal(200, restricted.Status); Assert.Single(restricted.Value!.Proposals);
        Assert.Empty(restricted.Value.Installments); Assert.Empty(restricted.Value.Evidence);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider }; context.Response.Body = new MemoryStream();
        var prepared = await PaymentEndpoints.Installments(request.Id, service, context, default);
        await using (var db = f.CreateContext())
            await db.Set<GrantProposal>().Where(x => x.RecipientId == AliasAccountId).ExecuteUpdateAsync(x => x
                .SetProperty(p => p.PrimaryApprovalLevels, Array.Empty<string>()).SetProperty(p => p.ApprovalLevels, Array.Empty<string>())
                .SetProperty(p => p.Capabilities, new[] { "report.read" }));
        Assert.Equal(200, (await service.GetAsync(AliasAccountId, request.Id, default)).Status);
        await prepared.ExecuteAsync(context); Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.DoesNotContain(request.ProposalId.ToString(), await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stalled_delivery_aborts_and_releases_authorization_lock(bool clientAbort)
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var request = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        var session = await f.SessionAsync(RecipientId); await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = session.Entity;
        using var lifetime = new ControlledLifetime(); await using var stream = new StalledStream();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature>(lifetime);
        context.Response.Body = stream;
        var delivery = PaymentHttp.Protect(RecipientId, request, Results.Json(new { value = "protected" })).ExecuteAsync(context);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        async Task<bool> TryLock()
        {
            await using var connection = new Npgsql.NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using var command = new Npgsql.NpgsqlCommand("SELECT pg_try_advisory_xact_lock(7241002)", connection, tx);
            return (bool)(await command.ExecuteScalarAsync())!;
        }
        Assert.False(await TryLock());
        if (clientAbort) lifetime.CancelRequest();
        await delivery.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(lifetime.Aborted); Assert.True(stream.Cancelled); Assert.Equal(0, stream.Length);
        Assert.Null(scope.ServiceProvider.GetRequiredService<TPR10.Api.Data.Tpr10DbContext>().Database.CurrentTransaction);
        Assert.True(await TryLock());
        Assert.Equal(200, (await scope.ServiceProvider.GetRequiredService<PaymentReadService>().GetAsync(RecipientId, request, default)).Status);
    }
    private sealed class ControlledLifetime : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature, IDisposable
    {
        private readonly CancellationTokenSource source = new();
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get => source.Token; set => throw new NotSupportedException(); }
        public void CancelRequest() => source.Cancel();
        public void Abort() { Aborted = true; source.Cancel(); }
        public void Dispose() => source.Dispose();
    }
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        private async Task Stall(CancellationToken ct)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => new(Stall(cancellationToken));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Stall(cancellationToken);
    }

    [Fact]
    public async Task Installment_commands_preserve_separation_and_lost_start_is_recoverable_without_second_attempt()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var person = Guid.NewGuid();
        await using (var db = f.CreateContext())
        {
            db.Add(new FinancePerson { Id = person, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "test checker" });
            db.Add(new FinanceAccountLink { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, UserId = AliasAccountId, PersonId = person, CreatedBy = AuthorId, Reason = "test checker", CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1) });
            await db.SaveChangesAsync();
        }
        var grant = await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, person);
        await using (var db = f.CreateContext())
        {
            var proposalId = (await db.Set<GrantAssignment>().SingleAsync(x => x.Id == grant)).ProposalId;
            await db.Set<GrantProposal>().Where(x => x.Id == proposalId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "finance.check" }));
        }
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var payer = await Client(factory, f, RecipientId); using var checker = await Client(factory, f, AliasAccountId);
        var created = await Send(payer, HttpMethod.Post, $"requests/{id}/installments", new { input = Input(), meta = Meta() });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var current = (await created.Content.ReadFromJsonAsync<PaymentHttpView>())!;
        var installmentId = current.InstallmentId!.Value;
        static PaymentCommand Command(PaymentHttpView v) => new(Guid.NewGuid(), v.FamilyVersion, v.RequestVersion, v.InstallmentVersion, "ทดสอบ");
        var submitted = await Send(payer, HttpMethod.Post, $"installments/{installmentId}/submit", Command(current)); Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        current = (await submitted.Content.ReadFromJsonAsync<PaymentHttpView>())!;
        Assert.Equal("100.00", current.Reserved);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(payer, HttpMethod.Post, $"installments/{installmentId}/review", new { outcome = "Approve", meta = Command(current) })).StatusCode);
        var payerUi = await payer.GetFromJsonAsync<PaymentUiContext>(Root + $"requests/{id}/payment-context");
        var checkerUi = await checker.GetFromJsonAsync<PaymentUiContext>(Root + $"requests/{id}/payment-context");
        Assert.DoesNotContain("review", payerUi!.Installments.Single().AllowedActions);
        Assert.DoesNotContain("installments", checkerUi!.AllowedActions);
        Assert.Contains("terminate-remaining", checkerUi.AllowedActions); Assert.Equal(new[] { "review" }, checkerUi.Installments.Single().AllowedActions);
        var reviewed = await Send(checker, HttpMethod.Post, $"installments/{installmentId}/review", new { outcome = "Approve", meta = Command(current) });
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode); current = (await reviewed.Content.ReadFromJsonAsync<PaymentHttpView>())!;
        checkerUi = await checker.GetFromJsonAsync<PaymentUiContext>(Root + $"requests/{id}/payment-context");
        Assert.Empty(checkerUi!.Installments.Single().AllowedActions);
        payerUi = await payer.GetFromJsonAsync<PaymentUiContext>(Root + $"requests/{id}/payment-context");
        Assert.Contains("start", payerUi!.Installments.Single().AllowedActions);
        var start = Command(current);
        var started = await Send(payer, HttpMethod.Post, $"installments/{installmentId}/start", start); Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(payer, HttpMethod.Post, $"installments/{installmentId}/start", start)).StatusCode);
        var recovered = await payer.GetFromJsonAsync<PaymentHttpView>(Root + $"payment-commands/{start.CommandId}");
        Assert.Equal("Paying", recovered!.State); Assert.Equal("0.00", recovered.Paid); Assert.Equal("100.00", recovered.Reserved);
        var detail = await payer.GetFromJsonAsync<JsonElement>(Root + $"installments/{installmentId}");
        Assert.Equal("InstallmentReview", detail.GetProperty("proposals")[0].GetProperty("kind").GetString());
        payerUi = await payer.GetFromJsonAsync<PaymentUiContext>(Root + $"requests/{id}/payment-context");
        Assert.Contains("record", payerUi!.Installments.Single().AllowedActions);
        Assert.Contains("outcome", payerUi.Installments.Single().AllowedActions);
        Assert.DoesNotContain("start", payerUi.Installments.Single().AllowedActions);
        Assert.DoesNotContain("takeover", payerUi.Installments.Single().AllowedActions);
        await using var verify = f.CreateContext(); Assert.Single(await verify.Set<AdvancePaymentAttempt>().ToArrayAsync());
        // A second account with its own finance person may take over, never record the original payer's result.
        await verify.Set<GrantProposal>().Where(x => x.RecipientId == AliasAccountId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "payment.record", "report.read" }));
        checkerUi = await checker.GetFromJsonAsync<PaymentUiContext>(Root + $"requests/{id}/payment-context");
        Assert.Empty(checkerUi!.Installments.Single().AllowedActions); // prior checker history forbids switching role
        await verify.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "finance.check", "report.read" }));
        payerUi = await payer.GetFromJsonAsync<PaymentUiContext>(Root + $"requests/{id}/payment-context");
        Assert.Empty(payerUi!.AllowedActions); Assert.Empty(payerUi.Installments.Single().AllowedActions);

    }

    [Fact]
    public async Task Request_read_discovers_request_scoped_proposals_without_entity_or_other_request_data()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using (var db = f.CreateContext())
        {
            var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
            var receipt = FieldAdvancePaymentFixture.Receipt(); receipt.ActorId = RecipientId; db.Add(receipt);
            db.Add(new AdvancePaymentProposal
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                FamilyId = r.FamilyId,
                RequestId = id,
                CommandId = receipt.CommandId,
                Kind = "PaymentDisposition",
                FamilyVersion = 1,
                RequestVersion = 1,
                ContentHash = new('a', 64),
                BeforeJson = "{\"Approved\":10000,\"Basis\":10000}",
                AfterJson = "{\"Outcome\":\"TerminateRemaining\"}",
                ProposedBy = RecipientId,
                ProposedByPersonId = RecipientPersonId,
                Reason = "ยุติยอดเหลือ"
            });
            await db.SaveChangesAsync();
        }
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings); using var payer = await Client(factory, f, RecipientId);
        var response = await payer.GetAsync(Root + $"requests/{id}/installments"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        Assert.Equal("PaymentDisposition", value.GetProperty("proposals")[0].GetProperty("kind").GetString());
        Assert.Equal("TerminateRemaining", value.GetProperty("proposals")[0].GetProperty("after").GetProperty("outcome").GetString());
        Assert.Equal("10000.00", value.GetProperty("proposals")[0].GetProperty("before").GetProperty("approved").GetString());
        Assert.Equal("10000.00", value.GetProperty("proposals")[0].GetProperty("before").GetProperty("basis").GetString());
        Assert.Empty(value.GetProperty("installments").EnumerateArray()); Assert.Empty(value.GetProperty("evidence").EnumerateArray());
        Assert.DoesNotContain("contentHash", value.ToString()); Assert.DoesNotContain("beforeJson", value.ToString());
    }

    [Fact]
    public async Task Routes_require_authenticated_session_and_current_mfa()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Root + $"requests/{id}/payments")).StatusCode);
        using var payer = await Client(factory, f, RecipientId);
        var csrf = await IdentityTestDriver.TokenAsync(payer);
        await using var db = f.CreateContext();
        await db.Set<IdentitySession>().Where(x => x.UserId == RecipientId).ExecuteUpdateAsync(x => x.SetProperty(p => p.MfaVerifiedAtUtc, (DateTimeOffset?)null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await payer.GetAsync(Root + $"requests/{id}/payments")).StatusCode);
        using var mutation = IdentityTestDriver.Mutation(csrf, Root + $"requests/{id}/installments");
        mutation.Content = JsonContent.Create(new { input = Input(), meta = Meta() });
        Assert.Equal(HttpStatusCode.Forbidden, (await payer.SendAsync(mutation)).StatusCode);
    }

    [Fact]
    public async Task New_routes_generate_openapi_contracts_independently_of_legacy_document()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings,
            configureServices: services => services.Configure<OpenApiOptions>("v1", options =>
                options.ShouldInclude = description => description.ActionDescriptor.EndpointMetadata.OfType<PaymentApiMetadata>().Any()));
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        Assert.Equal(27, paths.EnumerateObject().Sum(x => x.Value.EnumerateObject().Count()));
        foreach (var path in paths.EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
            {
                var responses = operation.Value.GetProperty("responses");
                foreach (var error in new[] { "400", "401", "403", "409", "422", "503" }) Assert.True(responses.TryGetProperty(error, out _));
                if (operation.Name == "post") Assert.True(operation.Value.TryGetProperty("requestBody", out _));
            }
        var uploadSchema = paths.GetProperty(Root + "requests/{id}/payment-evidence").GetProperty("post").GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/pdf").GetProperty("schema");
        Assert.True(uploadSchema.TryGetProperty("format", out var format)); Assert.Equal("binary", format.GetString());
        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty("PaymentHttpView").GetProperty("properties");
        Assert.Equal("string", schema.GetProperty("approved").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Evidence_upload_download_enforces_limits_headers_current_rights_and_csrf()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var requestId = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        var root = Path.Combine(Path.GetTempPath(), "task12-evidence-" + Guid.NewGuid().ToString("N"));
        root = OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + root : root;
        Directory.CreateDirectory(root);
        try
        {
            var storage = Guid.NewGuid();
            var settings = new Dictionary<string, string?>(keys.Settings)
            {
                ["FinanceEvidence:ActiveStorageId"] = storage.ToString(),
                [$"FinanceEvidence:Roots:{storage}"] = root,
                ["FinanceEvidence:MaxFileBytes"] = "1024"
            };
            await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, settings);
            using var payer = await Client(factory, f, RecipientId);
            using var bitmap = new SkiaSharp.SKBitmap(2, 2); bitmap.Erase(SkiaSharp.SKColors.White);
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap); using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100); var png = data.ToArray();
            async Task<HttpResponseMessage> Upload(byte[] bytes)
            {
                var token = await IdentityTestDriver.TokenAsync(payer);
                using var message = IdentityTestDriver.Mutation(token, Root + $"requests/{requestId}/payment-evidence");
                message.Content = new ByteArrayContent(bytes); message.Content.Headers.ContentType = new("image/png");
                return await payer.SendAsync(message);
            }
            var tooLarge = await Upload(new byte[1025]); Assert.Equal(HttpStatusCode.UnprocessableEntity, tooLarge.StatusCode);
            var uploaded = await Upload(png); Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            var ticket = await uploaded.Content.ReadFromJsonAsync<JsonElement>(); var id = ticket.GetProperty("id").GetGuid();
            Assert.DoesNotContain(root, ticket.ToString());
            var download = await payer.GetAsync(Root + $"payment-evidence/{id}"); Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(png, await download.Content.ReadAsByteArrayAsync());
            Assert.Contains("no-store", download.Headers.CacheControl!.ToString()); Assert.True(download.Headers.CacheControl.Private);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal(HttpStatusCode.Forbidden, (await payer.PostAsJsonAsync(Root + $"requests/{requestId}/installments", new { input = Input(), meta = Meta() })).StatusCode);
            await using var db = f.CreateContext(); await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, Array.Empty<string>()));
            Assert.Equal(HttpStatusCode.Forbidden, (await payer.GetAsync(Root + $"payment-evidence/{id}")).StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EveryRouteRechecksCurrentRights()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        var installment = await new FieldAdvancePaymentFixture(f).SeedInstallmentAsync(id);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var payer = await Client(factory, f, RecipientId);
        var command = Meta();
        Assert.Equal(HttpStatusCode.OK, (await Send(payer, HttpMethod.Post, $"requests/{id}/installments", new { input = Input(), meta = command })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await payer.GetAsync(Root + $"payment-commands/{command.CommandId}")).StatusCode);
        var evidenceId = await ExistingEvidence(f, id); var proposalId = Guid.NewGuid();
        await using (var db = f.CreateContext())
        {
            var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
            var receipt = FieldAdvancePaymentFixture.Receipt(); receipt.ActorId = RecipientId; db.Add(receipt);
            db.Add(new AdvancePaymentProposal
            {
                Id = proposalId,
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                FamilyId = request.FamilyId,
                RequestId = id,
                CommandId = receipt.CommandId,
                Kind = "PaymentDisposition",
                FamilyVersion = 1,
                RequestVersion = 1,
                ContentHash = new('a', 64),
                BeforeJson = "{}",
                AfterJson = "{}",
                ProposedBy = RecipientId,
                ProposedByPersonId = RecipientPersonId,
                Reason = "foreign family proposal"
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await payer.GetAsync(Root + $"payment-proposals/{proposalId}")).StatusCode);
        using var denied = await Client(factory, f, ReviewerId);
        var token = await IdentityTestDriver.TokenAsync(denied);
        async Task<HttpResponseMessage> SendDenied(string path, object body)
        {
            using var message = IdentityTestDriver.Mutation(token, Root + path); message.Content = JsonContent.Create(body);
            return await denied.SendAsync(message);
        }
        foreach (var path in new[] { $"requests/{id}/payments", $"requests/{id}/installments", $"installments/{installment.Installment.Id}", $"payment-proposals/{proposalId}", $"payment-commands/{command.CommandId}", $"payment-evidence/{evidenceId}", $"payments?workspaceId={WorkspaceId}&projectId={ProjectId}" })
        {
            var response = await denied.GetAsync(Root + path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("accountNumber", await response.Content.ReadAsStringAsync());
        }
        foreach (var action in new[] { "submit", "start", "cancel" })
            Assert.Equal(HttpStatusCode.Forbidden, (await SendDenied($"installments/{installment.Installment.Id}/{action}", Meta() with { InstallmentVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendDenied($"requests/{id}/installments", new { input = Input(), meta = Meta() })).StatusCode);
        foreach (var action in new[] { "review", "record", "outcome", "mismatch", "metadata", "takeover", "update" })
        {
            var actual = new ActualPaymentInput("100.00", AuthorPersonId, new(2026, 10, 27), null, []);
            object body = action switch
            {
                "review" => new { outcome = "Approve", meta = Meta() with { InstallmentVersion = 1 } },
                "record" or "mismatch" => new { input = actual, meta = Meta() with { InstallmentVersion = 1 } },
                "metadata" => new { input = new PaymentMetadataInput(actual.PaidOn, null, []), meta = Meta() with { InstallmentVersion = 1 } },
                "outcome" => new { input = new PaymentOutcomeInput("Unknown", null, []), meta = Meta() with { InstallmentVersion = 1 } },
                "takeover" => new { evidenceIds = Array.Empty<Guid>(), meta = Meta() with { InstallmentVersion = 1 } },
                _ => new { input = Input(), meta = Meta() with { InstallmentVersion = 1 } }
            };
            Assert.Equal(HttpStatusCode.Forbidden, (await SendDenied($"installments/{installment.Installment.Id}/{action}", body)).StatusCode);
        }
        foreach (var action in new[] { "confirm", "finalize" })
            Assert.Equal(HttpStatusCode.Forbidden, (await SendDenied($"payment-proposals/{proposalId}/{action}", Meta())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendDenied($"requests/{id}/ceiling-activate", Meta())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendDenied($"requests/{id}/disposition", new { outcome = "TerminateRemaining", meta = Meta() })).StatusCode);
        var content = new TPR10.Api.FieldAdvances.Requests.RequestContent("Individual", "สำรวจ", new(2026, 10, 27), new(2026, 10, 28), new(2026, 10, 27), [], []);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendDenied($"requests/{id}/ceiling-amendment", new { content, meta = Meta() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendDenied($"requests/{id}/supplements", new { content, meta = new TPR10.Api.FieldAdvances.Requests.RequestCommand(Guid.NewGuid(), 0, "ทดสอบ") })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(Root + $"payments?workspaceId={OtherWorkspaceId}&projectId={ProjectId}")).StatusCode);
    }

    [Fact]
    public async Task Summary_queue_and_installment_use_allowlist_and_decimal_strings()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var payer = await Client(factory, f, RecipientId);
        var response = await payer.GetAsync(Root + $"requests/{id}/payments"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("10000.00", value.GetProperty("approved").GetString());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("installmentId").ValueKind);
        Assert.Equal(12, value.EnumerateObject().Count());
        var queue = await payer.GetFromJsonAsync<JsonElement[]>(Root + $"payments?workspaceId={WorkspaceId}&projectId={ProjectId}&take=1"); Assert.Single(queue!);
        Assert.Equal(HttpStatusCode.BadRequest, (await payer.GetAsync(Root + $"payments?workspaceId={WorkspaceId}&projectId={ProjectId}&take=101")).StatusCode);
        var created = await Send(payer, HttpMethod.Post, $"requests/{id}/installments", new { input = Input(), meta = Meta() });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var result = await created.Content.ReadFromJsonAsync<JsonElement>();
        var detail = await payer.GetFromJsonAsync<JsonElement>(Root + $"installments/{result.GetProperty("installmentId").GetGuid()}");
        Assert.Equal("100.00", detail.GetProperty("revisions")[0].GetProperty("amount").GetString());
        Assert.DoesNotContain("contentHash", detail.ToString());
    }

    [Fact]
    public async Task Commands_reject_unknown_fields_empty_reason_stale_versions_and_replay_after_revoke()
    {
        await using var f = await Ready(); using var keys = new TestKeyMaterial();
        var id = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000, default);
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var payer = await Client(factory, f, RecipientId); using var owner = await Client(factory, f, AuthorId);
        var meta = Meta(); var path = $"requests/{id}/installments";
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(payer, HttpMethod.Post, path, new { input = Input(), meta, unexpected = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(payer, HttpMethod.Post, path, new { input = Input(), meta = meta with { Reason = "" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(payer, HttpMethod.Post, path, new { input = Input(), meta = meta with { RequestVersion = 99 } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(payer, HttpMethod.Post, path, new { input = Input(), meta })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await payer.GetAsync(Root + $"payment-commands/{meta.CommandId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Root + $"requests/{id}/payments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(Root + $"payment-commands/{meta.CommandId}")).StatusCode);
        await using var db = f.CreateContext(); await db.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, Array.Empty<string>()));
        Assert.Equal(HttpStatusCode.Forbidden, (await payer.GetAsync(Root + $"payment-commands/{meta.CommandId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(payer, HttpMethod.Post, path, new { input = Input(), meta })).StatusCode);
    }
}
