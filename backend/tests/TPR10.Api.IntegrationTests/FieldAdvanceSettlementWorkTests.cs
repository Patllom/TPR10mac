using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementReviewTests;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementWorkTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("corrections")]
    [InlineData("case-payments")]
    [InlineData("excess-payments")]
    public void Review_receipt_operation_matches_command_history_for_each_family(string family)
    {
        var commandId = Guid.NewGuid();
        var otherCommandId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var date = new DateOnly(2026, 10, 20);
        var evidence = new[] { new RefundEvidenceSnapshot(evidenceId, "sha", 1, "image/png") };
        var history = new[] { new CorrectionHistory(commandId, "Return", ReviewerId,
            ReviewerPersonId, DateTimeOffset.UtcNow, "ส่งกลับ") };
        object entity = family switch
        {
            "corrections" => new RefundCorrection
            {
                RefundId = sourceId,
                EvidenceId = evidenceId,
                BaseVersion = 1,
                BeforeAmount = 12m,
                AfterAmount = 13m,
                SnapshotJson = JsonSerializer.Serialize(new CorrectionSnapshot(1,
                    new RefundCorrectionInput(sourceId, 13m, [evidenceId]), 1, 12m, evidence, history))
            },
            "case-payments" => new SettlementCasePayment
            {
                CaseId = sourceId,
                EvidenceId = evidenceId,
                Amount = 12m,
                PaidOn = date,
                SnapshotJson = JsonSerializer.Serialize(new CasePaymentSnapshot(1,
                    new CasePaymentInput(sourceId, 12m, date, [evidenceId]), evidence, history))
            },
            _ => new SettlementExcessPayment
            {
                ExcessId = sourceId,
                RecipientPersonId = recipient,
                EvidenceId = evidenceId,
                Amount = 12m,
                PaidOn = date,
                SnapshotJson = JsonSerializer.Serialize(new ExcessPaymentSnapshot(1,
                    new ExcessPaymentInput(sourceId, recipient, 12m, date, [evidenceId]), evidence, history))
            }
        };
        Assert.True(SettlementUiService.ReviewOperationMatches($"{family}/return", commandId, entity));
        Assert.False(SettlementUiService.ReviewOperationMatches($"{family}/confirm", commandId, entity));
        Assert.False(SettlementUiService.ReviewOperationMatches($"{family}/reject", commandId, entity));
        Assert.False(SettlementUiService.ReviewOperationMatches($"{family}/return", otherCommandId, entity));
    }
    [Fact]
    public async Task Queue_cursor_delivers_second_scoped_item_and_denies_revoked_assignment()
    {
        await using var f = await Ready(postgres);
        var (firstRequest, firstProof) = await FieldAdvanceRefundTests.RefundReady(f);
        var first = await FieldAdvanceRefundTests.RefundCall(f, firstRequest, "Propose",
            input: FieldAdvanceRefundTests.RefundInputFor(12m, firstProof));
        Assert.Equal(201, first.Status);
        Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, firstRequest, "Confirm",
            first.Value!.EntityId)).Status);
        var (secondRequest, secondProof) = await FieldAdvanceRefundTests.RefundReady(f);
        var second = await FieldAdvanceRefundTests.RefundCall(f, secondRequest, "Propose",
            input: FieldAdvanceRefundTests.RefundInputFor(13m, secondProof));
        Assert.Equal(201, second.Status);
        Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, secondRequest, "Confirm",
            second.Value!.EntityId)).Status);

        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var checker = await Client(factory, f, ReviewerId);
        var path = $"/api/v1/field-advances/settlement-work?workspaceId={WorkspaceId}&projectId={ProjectId}&lane=refunds&take=1";
        var response = await checker.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(page.GetProperty("items").EnumerateArray());
        var cursor = page.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var next = await checker.GetAsync(path + "&cursor=" + Uri.EscapeDataString(cursor!));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var secondPage = await next.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(secondPage.GetProperty("items").EnumerateArray());
        Assert.NotEqual(page.GetProperty("items")[0].GetProperty("entityId").GetGuid(),
            secondPage.GetProperty("items")[0].GetProperty("entityId").GetGuid());
        await using (var db = f.CreateContext())
        {
            await db.Set<GrantAssignment>().Where(assignment =>
                db.Set<GrantProposal>().Any(proposal => proposal.Id == assignment.ProposalId
                    && proposal.RecipientId == ReviewerId && proposal.Capabilities.Contains("finance.check")))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(value => value.RevokedAtUtc, f.Clock.GetUtcNow())
                    .SetProperty(value => value.RevokedBy, AuthorId)
                    .SetProperty(value => value.RevocationReason, "ถอนสิทธิ์ทดสอบคิว"));
        }
        var revoked = await checker.GetAsync(path + "&cursor=" + Uri.EscapeDataString(cursor!));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Empty((await revoked.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray());
    }
    [Fact]
    public async Task Recovery_receipt_binds_immutable_command_to_source_incident()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new FieldAdvancePaymentRecoveryTests.Files();
        var incident = await FieldAdvancePaymentIncidentTests.Incident(f, files, true, 8000m, 2000m);
        var proof = (await FieldAdvancePaymentRecoveryTests.Actual(f, files, incident.Request,
            Guid.NewGuid().ToByteArray())).EvidenceIds;
        var command = await Meta(f, incident.Request);
        var proposed = await FieldAdvancePaymentIncidentTests.Invoke(f, files, incident.Request,
            "Recovery", incident.Id, amount: 2000m, source: "WrongRecipient", evidence: proof,
            command: command);
        Assert.Equal(201, proposed.Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var payer = await Client(factory, f, RecipientId);
        var path = $"/api/v1/field-advances/requests/{incident.Request}/settlement/commands/{command.CommandId}/receipt?operation=incidents%2Frecover&entityId={incident.Id}";
        var response = await payer.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await payer.GetAsync(path.Replace(incident.Id.ToString(),
            Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await payer.GetAsync(path.Replace("incidents%2Frecover",
            "incidents%2Fshortfall"))).StatusCode);
    }
    [Fact]
    public async Task Shortfall_receipt_binds_created_installment_to_exact_incident()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new FieldAdvancePaymentRecoveryTests.Files();
        var incident = await FieldAdvancePaymentIncidentTests.Incident(f, files, true, 8000m, 2000m);
        await FieldAdvanceIncidentPaymentBridgeTests.Recovery(f, files, incident, 2000m);
        Assert.Equal(200, (await FieldAdvanceIncidentPaymentBridgeTests.Bridge(f, files, incident, "Release")).Status);
        var command = await Meta(f, incident.Request);
        var prepared = await FieldAdvanceIncidentPaymentBridgeTests.Bridge(f, files, incident,
            "Prepare", 2000m, command);
        Assert.Equal(201, prepared.Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var payer = await Client(factory, f, RecipientId);
        using var checker = await Client(factory, f, FieldAdvancePaymentRecoveryTests.Checker);
        var path = $"/api/v1/field-advances/requests/{incident.Request}/settlement/commands/{command.CommandId}/receipt?operation=incidents%2Fshortfall&entityId={incident.Id}";
        Assert.Equal(HttpStatusCode.OK, (await payer.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await checker.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await payer.GetAsync(path.Replace(incident.Id.ToString(),
            Guid.NewGuid().ToString()))).StatusCode);
    }
    [Fact]
    public async Task Incident_recovery_evidence_upload_requires_current_payer_and_accepted_incident()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new FieldAdvancePaymentRecoveryTests.Files();
        var source = await FieldAdvancePaymentIncidentTests.Source(f, files, true, 8000m, 2000m);
        var proposed = await FieldAdvancePaymentIncidentTests.Invoke(f, files, source.Request, "Propose",
            input: new(source.Attempt, 8000m, 2000m, source.Evidence));
        Assert.Equal(201, proposed.Status);
        var incidentId = proposed.Value!.EntityId!.Value;
        await using (var db = f.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var payerAccess = await Access(f, db, RecipientId);
            var payerEvidence = new SettlementEvidenceService(db, payerAccess, null!, null!, null!, f.Clock);
            Assert.False((await payerEvidence.UploadAccessAsync(RecipientId, source.Request,
                new IncidentRecoveryEvidenceTarget(incidentId), default)).Allowed);
        }
        Assert.Equal(200, (await FieldAdvancePaymentIncidentTests.Invoke(f, files, source.Request,
            "Confirm", incidentId)).Status);
        await using (var db = f.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var payerAccess = await Access(f, db, RecipientId);
            var payerEvidence = new SettlementEvidenceService(db, payerAccess, null!, null!, null!, f.Clock);
            Assert.True((await payerEvidence.UploadAccessAsync(RecipientId, source.Request,
                new IncidentRecoveryEvidenceTarget(incidentId), default)).Allowed);
            Assert.False((await payerEvidence.UploadAccessAsync(RecipientId, source.Request,
                new IncidentRecoveryEvidenceTarget(Guid.NewGuid()), default)).Allowed);
            var checkerAccess = await Access(f, db, FieldAdvancePaymentRecoveryTests.Checker);
            var checkerEvidence = new SettlementEvidenceService(db, checkerAccess, null!, null!, null!, f.Clock);
            Assert.False((await checkerEvidence.UploadAccessAsync(FieldAdvancePaymentRecoveryTests.Checker,
                source.Request, new IncidentRecoveryEvidenceTarget(incidentId), default)).Allowed);
        }
    }

    [Fact]
    public async Task Duplicate_comparison_discloses_other_document_only_with_both_current_scopes()
    {
        await using var f = await Ready(postgres);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        var otherProject = Guid.Parse("71000000-0000-0000-0000-000000000012");
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await Grant(f, AuthorId, AuthorPersonId, "request.create", otherProject);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var first = await Request(f);
        Guid second;
        await using (var seed = f.CreateContext())
        {
            var request = new AdvanceRequest
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = otherProject,
                OwnerPersonId = AuthorPersonId,
                CreatedBy = AuthorId,
                State = "Approved"
            };
            seed.Add(request);
            await seed.SaveChangesAsync();
            second = request.Id;
        }
        await using (var db = f.CreateContext())
        {
            var documents = await FieldAdvanceExpenseDocumentTests.Documents(f, db, files);
            Assert.Equal(201, (await documents.RegisterAsync(AuthorId, second,
                await FieldAdvanceExpenseDocumentTests.Input(f, files, second), await Meta(f, second), default)).Status);
            Assert.Equal(201, (await documents.RegisterAsync(AuthorId, first,
                await FieldAdvanceExpenseDocumentTests.Input(f, files, first), await Meta(f, first), default)).Status);
        }
        Guid duplicate;
        await using (var db = f.CreateContext())
            duplicate = (await db.Set<ExpenseDuplicateCase>().SingleAsync()).Id;

        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var checker = await Client(factory, f, RecipientId);
        var path = $"/api/v1/field-advances/settlement-work/duplicates/{first}/{duplicate}/comparison";
        var hidden = await checker.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        Assert.DoesNotContain(second.ToString(), await hidden.Content.ReadAsStringAsync());
        var otherGrant = await Grant(f, RecipientId, RecipientPersonId, "finance.check", otherProject);
        var allowed = await checker.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains(second.ToString(), await allowed.Content.ReadAsStringAsync());
        await using (var db = f.CreateContext())
            await db.Set<GrantAssignment>().Where(x => x.Id == otherGrant).ExecuteUpdateAsync(update => update
                .SetProperty(x => x.RevokedAtUtc, f.Clock.GetUtcNow())
                .SetProperty(x => x.RevokedBy, AuthorId)
                .SetProperty(x => x.RevocationReason, "ถอนสิทธิ์อีกโครงการ"));
        Assert.Equal(HttpStatusCode.Forbidden, (await checker.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Incident_receipt_requires_exact_checker_action_and_entity()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new FieldAdvancePaymentRecoveryTests.Files();
        var source = await FieldAdvancePaymentIncidentTests.Source(f, files, true, 8000m, 2000m);
        var proposal = await FieldAdvancePaymentIncidentTests.Invoke(f, files, source.Request, "Propose",
            input: new(source.Attempt, 8000m, 2000m, source.Evidence));
        Assert.Equal(201, proposal.Status);
        var incident = proposal.Value!.EntityId!.Value;
        var command = await FieldAdvancePaymentIncidentTests.Command(f, source.Request, incident);
        var accepted = await FieldAdvancePaymentIncidentTests.Invoke(f, files, source.Request, "Confirm", incident,
            command: command);
        Assert.Equal(200, accepted.Status);

        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var checker = await Client(factory, f, FieldAdvancePaymentRecoveryTests.Checker);
        using var payer = await Client(factory, f, RecipientId);
        var path = $"/api/v1/field-advances/requests/{source.Request}/settlement/commands/{command.CommandId}/receipt?operation=incidents%2Fconfirm&entityId={incident}";
        Assert.Equal(HttpStatusCode.OK, (await checker.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await payer.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await checker.GetAsync(path.Replace(incident.ToString(), Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await checker.GetAsync(path.Replace("incidents%2Fconfirm", "recoveries%2Fconfirm"))).StatusCode);
    }

    [Fact]
    public async Task Review_receipt_lookup_is_exact_actor_request_action_and_round()
    {
        await using var f = await Ready(postgres);
        var (request, round) = await Draft(f);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        var command = await ReviewCommand(f, request, round);
        var reviewed = await Review(f, ReviewerId, request, round, command: command);
        Assert.Equal(200, reviewed.Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var reviewer = await Client(factory, f, ReviewerId);
        using var other = await Client(factory, f, RecipientId);
        var path = $"/api/v1/field-advances/requests/{request}/settlement/commands/{command.CommandId}/receipt?operation=rounds%2Freview&entityId={round}";
        var response = await reviewer.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reviewed.Value!.EntityId, receipt.GetProperty("result").GetProperty("entityId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync(path.Replace(round.ToString(), Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync(path.Replace("rounds%2Freview", "refunds%2Freview"))).StatusCode);
    }
    [Fact]
    public async Task Finance_checker_sees_pending_refund_projection_without_report_read_or_admin_fallback()
    {
        await using var f = await Ready(postgres);
        var (request, proof) = await FieldAdvanceRefundTests.RefundReady(f);
        var created = await FieldAdvanceRefundTests.RefundCall(f, request, "Propose", input: FieldAdvanceRefundTests.RefundInputFor(12m, proof));
        Assert.Equal(201, created.Status);
        Assert.Equal(200, (await FieldAdvanceRefundTests.RefundCall(f, request, "Confirm", created.Value!.EntityId)).Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var checker = await Client(factory, f, ReviewerId);
        using var admin = await Client(factory, f, AuthorId);
        var path = $"/api/v1/field-advances/settlement-work?workspaceId={WorkspaceId}&projectId={ProjectId}&lane=refunds";
        var response = await checker.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        Assert.Equal(created.Value.EntityId, item.GetProperty("entityId").GetGuid());
        Assert.Equal("refund-review", item.GetProperty("allowedActions")[0].GetString());
        Assert.Equal("12.00", item.GetProperty("detail").GetProperty("refund").GetProperty("amount").GetString());
        Assert.Empty((await (await admin.GetAsync(path)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        var proofPath = $"/api/v1/field-advances/settlement-work/refunds/{request}/{created.Value.EntityId}/evidence/{proof}";
        Assert.Equal(HttpStatusCode.Conflict, (await checker.GetAsync(proofPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(proofPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await checker.GetAsync(proofPath.Replace(proof.ToString(), Guid.NewGuid().ToString()))).StatusCode);
    }

    [Fact]
    public async Task Finance_incident_queue_exposes_only_scoped_pending_facts()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new FieldAdvancePaymentRecoveryTests.Files();
        var source = await FieldAdvancePaymentIncidentTests.Source(f, files, true, 8000m, 2000m);
        var proposed = await FieldAdvancePaymentIncidentTests.Invoke(f, files, source.Request, "Propose",
            input: new(source.Attempt, 8000m, 2000m, source.Evidence));
        Assert.Equal(201, proposed.Status);
        var request = source.Request;
        var incidentId = proposed.Value!.EntityId!.Value;
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var checker = await Client(factory, f, FieldAdvancePaymentRecoveryTests.Checker);
        var path = $"/api/v1/field-advances/settlement-work?workspaceId={WorkspaceId}&projectId={ProjectId}&lane=incidents";
        var response = await checker.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        Assert.Equal(incidentId, item.GetProperty("entityId").GetGuid());
        Assert.Equal("incident-review", item.GetProperty("allowedActions")[0].GetString());
        Assert.Equal("8000.00", item.GetProperty("detail").GetProperty("incident").GetProperty("ownerReceived").GetString());
        Assert.Equal("2000.00", item.GetProperty("detail").GetProperty("incident").GetProperty("wrongRecipientReceived").GetString());
        Assert.False(item.TryGetProperty("otherRequestId", out _));
    }
    [Fact]
    public async Task Accepted_incident_offers_separate_payer_recovery_before_checker_release()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new FieldAdvancePaymentRecoveryTests.Files();
        var source = await FieldAdvancePaymentIncidentTests.Source(f, files, true, 8000m, 2000m);
        var proposed = await FieldAdvancePaymentIncidentTests.Invoke(f, files, source.Request, "Propose",
            input: new(source.Attempt, 8000m, 2000m, source.Evidence));
        Assert.Equal(201, proposed.Status);
        var incidentId = proposed.Value!.EntityId!.Value;
        Assert.Equal(200, (await FieldAdvancePaymentIncidentTests.Invoke(f, files, source.Request,
            "Confirm", incidentId)).Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var payer = await Client(factory, f, RecipientId);
        using var checker = await Client(factory, f, FieldAdvancePaymentRecoveryTests.Checker);
        var path = $"/api/v1/field-advances/settlement-work?workspaceId={WorkspaceId}&projectId={ProjectId}&lane=incidents";
        var page = await (await payer.GetAsync(path)).Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(incidentId, item.GetProperty("entityId").GetGuid());
        Assert.Equal("Accepted", item.GetProperty("state").GetString());
        Assert.Contains(item.GetProperty("allowedActions").EnumerateArray(),
            action => action.GetString() == "recovery-propose");
        var checkerPage = await (await checker.GetAsync(path)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(checkerPage.GetProperty("items").EnumerateArray());
    }
    [Fact]
    public async Task Resolved_incident_offers_explicit_release_then_payer_shortfall()
    {
        await using var f = await new FieldAdvancePaymentRecoveryTests(postgres).Ready();
        using var files = new FieldAdvancePaymentRecoveryTests.Files();
        var incident = await FieldAdvancePaymentIncidentTests.Incident(f, files, true, 8000m, 2000m);
        await FieldAdvanceIncidentPaymentBridgeTests.Recovery(f, files, incident, 2000m);
        await using (var db = f.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var access = await Access(f, db, FieldAdvancePaymentRecoveryTests.Checker);
            Assert.True((await access.CheckAsync(FieldAdvancePaymentRecoveryTests.Checker, incident.Request,
                SettlementActions.IncidentRelease, incident.Id, default)).Allowed);
            var bridge = new IncidentPaymentBridge(db, access,
                await Executor(f, db, FieldAdvancePaymentRecoveryTests.Checker), f.Clock, files.Options);
            Assert.True(await bridge.CanReleaseAsync(incident.Request, incident.Id, default));
            Assert.True((await access.CheckAsync(FieldAdvancePaymentRecoveryTests.Checker, incident.Request,
                SettlementActions.IncidentRelease, incident.Id, default)).Allowed);
            var resolution = await db.Set<PaymentIncidentResolution>().AsNoTracking().SingleAsync(x => x.Id == incident.Id);
            Assert.True(await db.Set<PaymentIncidentResolution>().AnyAsync(x => x.Id == incident.Id
                && x.WorkspaceId == WorkspaceId && x.ProjectId == ProjectId
                && (x.State == "Pending" || x.State == "Accepted")
                && db.Set<AdvanceRequest>().Any(r => r.Id == x.RequestId && r.WorkspaceId == WorkspaceId
                    && r.ProjectId == ProjectId && r.FamilyId == x.FamilyId)));
            var ui = new SettlementUiService(db, access, null!, null!);
            Assert.Single((await ui.DetailsAsync<PaymentIncidentResolution>(FieldAdvancePaymentRecoveryTests.Checker,
                [new(resolution.Id, resolution.RequestId, resolution.Version, resolution.State, resolution.Reason)],
                incident.Request, default))!);
        }
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock,
            keys.Settings, configureServices: services => services.AddSingleton(files.Options));
        using var checker = await Client(factory, f, FieldAdvancePaymentRecoveryTests.Checker);
        using var payer = await Client(factory, f, RecipientId);
        var path = $"/api/v1/field-advances/settlement-work?workspaceId={WorkspaceId}&projectId={ProjectId}&lane=incidents";
        var before = await (await checker.GetAsync(path)).Content.ReadFromJsonAsync<JsonElement>();
        var release = Assert.Single(before.GetProperty("items").EnumerateArray());
        Assert.Equal("incident-release", release.GetProperty("allowedActions")[0].GetString());
        Assert.Equal(200, (await FieldAdvanceIncidentPaymentBridgeTests.Bridge(f, files, incident, "Release")).Status);
        var after = await (await payer.GetAsync(path)).Content.ReadFromJsonAsync<JsonElement>();
        var shortfall = Assert.Single(after.GetProperty("items").EnumerateArray());
        Assert.Contains(shortfall.GetProperty("allowedActions").EnumerateArray(),
            action => action.GetString() == "shortfall-prepare");
    }
    [Fact]
    public async Task Assigned_supervisor_sees_only_current_step_without_report_read()
    {
        await using var f = await Ready(postgres);
        var (request, round) = await Draft(f);
        Assert.Equal(200, (await Submit(f, AuthorId, request, round, await ConfirmCommand(f, request, round))).Status);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var reviewer = await Client(factory, f, ReviewerId);
        using var unassigned = await Client(factory, f, RecipientId);
        var path = $"/api/v1/field-advances/settlement-work?workspaceId={WorkspaceId}&projectId={ProjectId}&lane=rounds";
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.GetAsync($"/api/v1/field-advances/requests/{request}/settlement")).StatusCode);
        var response = await reviewer.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(request, item.GetProperty("requestId").GetGuid());
        Assert.Equal(round, item.GetProperty("roundId").GetGuid());
        Assert.Equal("round-approve", item.GetProperty("allowedActions")[0].GetString());
        Assert.Empty((await (await unassigned.GetAsync(path)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
    }
}
