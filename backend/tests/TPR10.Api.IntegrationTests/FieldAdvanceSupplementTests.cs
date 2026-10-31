using TPR10.Api.Organization;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.Scopes;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using L = TPR10.Api.IntegrationTests.FieldAdvanceRequestLifecycleTests;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Requests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSupplementTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("SupplementService")]
    [InlineData("ApprovalBasisCalculator")]
    public void Supplement_services_are_registered(string name)
        => Assert.Contains(new ServiceCollection().AddFieldAdvances(), x =>
            x.ServiceType.FullName == "TPR10.Api.FieldAdvances.Requests." + name
            && x.Lifetime == ServiceLifetime.Scoped);

    [Fact]
    public void Confirmation_exposes_server_calculated_basis()
        => Assert.NotNull(typeof(ConfirmationQuote).GetProperty("Basis"));

    [Fact]
    public async Task AggregateAuthorityIncrementOnlyPayout()
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        var supplement = await CreateSupplement(f, root, 30);
        var quote = await Quote(f, supplement);
        Assert.Equal(13000m, quote.Basis.Amount);
        Assert.Equal(2, quote.Basis.Items.Length);
        var approved = await Approve(f, await L.Submit(f, supplement));
        await using var db = f.CreateContext();
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == approved.Id);
        Assert.Equal(3000m, account.Spendable);
        Assert.Equal(2000m, (await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == root.Id)).Paid);
        var next = await CreateSupplement(f, approved, 20);
        Assert.Equal(15000m, (await Quote(f, next)).Basis.Amount);
        Assert.Empty(await db.Set<AdvancePaymentHold>().ToArrayAsync());
    }

    [Fact]
    public async Task Lower_limit_cannot_review_and_worker_uses_cumulative_basis()
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        var submitted = await L.Submit(f, await CreateSupplement(f, root, 30));
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.ApprovalLimit, 10000m));
        Assert.Equal(403, (await L.Decide(f, submitted, "Approve")).Status);
        Assert.True(await L.Reevaluate(f, submitted.Id));
        Assert.Equal("Held", (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == submitted.Id)).State);
    }

    [Fact]
    public async Task Concurrent_submissions_claim_one_slot_and_return_keeps_it()
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        var a = await CreateSupplement(f, root, 30); var b = await CreateSupplement(f, root, 20);
        var qa = await Quote(f, a); var qb = await Quote(f, b);
        var results = await Task.WhenAll(Confirm(f, a, qa), Confirm(f, b, qb));
        Assert.Single(results, x => x.Status == 200); Assert.Single(results, x => x.Status == 409);
        var winner = results.Single(x => x.Status == 200).Value!;
        var loser = winner.Id == a.Id ? b : a;
        var returned = await L.Decide(f, winner, "Return"); Assert.Equal(200, returned.Status);
        Assert.Equal(409, (await Confirm(f, loser, await Quote(f, loser))).Status);
        var revised = await L.Command(f, returned.Value!, "ReviseAsync"); Assert.Equal(200, revised.Status);
        Assert.Equal(409, (await Confirm(f, loser, await Quote(f, loser))).Status);
        var again = await L.Submit(f, revised.Value!);
        Assert.NotEqual(winner.RevisionId, again.RevisionId);
        await using var db = f.CreateContext();
        Assert.All(await db.Set<AdvanceApprovalStep>().Where(x => x.RevisionId == again.RevisionId).ToArrayAsync(), x => Assert.Null(x.Outcome));
        Assert.Equal(200, (await L.Command(f, again, "WithdrawAsync")).Status);
        Assert.Equal(200, (await Confirm(f, loser, await Quote(f, loser))).Status);
    }

    [Fact]
    public async Task Basis_change_invalidates_quote_and_copy_has_independent_family()
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        var a = await CreateSupplement(f, root, 30); var b = await CreateSupplement(f, root, 20);
        var stale = await Quote(f, b);
        await Approve(f, await L.Submit(f, a));
        Assert.Equal(409, (await Confirm(f, b, stale)).Status);
        var withdrawn = await L.Command(f, b, "WithdrawAsync"); Assert.Equal(200, withdrawn.Status);
        var copied = await L.Command(f, withdrawn.Value!, "CopyAsync"); Assert.Equal(201, copied.Status);
        await using var db = f.CreateContext();
        var source = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == b.Id);
        var copy = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == copied.Value!.Id);
        Assert.NotEqual(source.FamilyId, copy.FamilyId); Assert.Equal("Normal", copy.RelationKind);
        Assert.Null(copy.SupplementOfRequestId);
    }

    [Fact]
    public async Task Unpaid_root_cannot_create_supplement()
    {
        await using var f = await Ready(); var root = await Original(f);
        await using var db = f.CreateContext(); var service = await Supplements(f, db, AuthorId);
        Assert.Equal(409, (await service.CreateAsync(AuthorId, root.Id, Content(30), L.Meta(0), default)).Status);
    }


    [Theory]
    [InlineData("Rejected")]
    [InlineData("Withdrawn")]
    [InlineData("Closed")]
    public async Task Terminal_root_cannot_be_bypassed(string state)
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        await using var db = f.CreateContext();
        await db.Set<AdvanceRequest>().Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.State, state));
        var service = await Supplements(f, db, AuthorId);
        Assert.Equal(409, (await service.CreateAsync(AuthorId, root.Id, Content(30), L.Meta(0), default)).Status);
    }

    [Fact]
    public async Task Delegate_uses_source_identity_and_owner_must_confirm_with_current_project_grants()
    {
        await using var f = await Ready(); var actor = Guid.Parse("71000000-0000-0000-0000-000000000054");
        await Actor(f, actor, "request.delegate");
        var root = await Original(f, actor);
        await using var db = f.CreateContext();
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(); account.Paid = 2000m; account.EverPaid = true; await db.SaveChangesAsync();
        var service = await Supplements(f, db, actor); var content = Content(30); var meta = L.Meta(0);
        var created = await service.CreateAsync(actor, root.Id, content, meta, default);
        Assert.Equal(201, created.Status); var draft = created.Value!;
        Assert.Equal(AuthorPersonId, draft.OwnerPersonId); Assert.Equal(actor, draft.DelegatePersonId); Assert.Equal(root.Scope, draft.Scope);
        Assert.Equal(JsonSerializer.Serialize(created), JsonSerializer.Serialize(await service.CreateAsync(actor, root.Id, content, meta, default)));
        var submission = L.Submission(f, db, await f.SessionAsync(actor));
        var quote = await submission.QuoteAsync(actor, draft.Id, default); Assert.Equal(200, quote.Status);
        Assert.Equal(403, (await submission.ConfirmAsync(actor, draft.Id, quote.Value!.ConfirmationHash, L.Meta(draft.Version), default)).Status);
        var sent = await submission.SendToOwnerAsync(actor, draft.Id, L.Meta(draft.Version), default); Assert.Equal(200, sent.Status);
        Assert.Equal("PendingApproval", (await L.Submit(f, sent.Value!)).State);
        await db.Set<GrantProposal>().Where(x => x.RecipientId == actor && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(g => g.EndsAtUtc, f.Clock.GetUtcNow()));
        Assert.Equal(403, (await service.CreateAsync(actor, root.Id, content, meta, default)).Status);
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(g => g.EndsAtUtc, f.Clock.GetUtcNow()));
        var owner = await Supplements(f, db, AuthorId);
        Assert.Equal(403, (await owner.CreateAsync(AuthorId, root.Id, Content(20), L.Meta(0), default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Other_project_only_owner_or_delegate_cannot_create_supplement(bool delegated)
    {
        await using var f = await Ready();
        var actor = delegated ? Guid.Parse("71000000-0000-0000-0000-000000000054") : AuthorId;
        var person = delegated ? actor : AuthorPersonId;
        var capability = delegated ? "request.delegate" : "request.create";
        if (delegated) await Actor(f, actor, capability);
        var root = await Original(f, delegated ? actor : null);
        await using var db = f.CreateContext();
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync();
        account.Paid = 2000m; account.EverPaid = true;
        var otherProject = Guid.NewGuid();
        db.Add(new TPR10.Api.Organization.Data.Project
        {
            Id = otherProject,
            WorkspaceId = WorkspaceId,
            Code = "TASK10-OTHER",
            Name = "โครงการอื่น",
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person, projectId: otherProject);
        await db.Set<GrantProposal>().Where(x => x.RecipientId == actor && x.ProjectId == otherProject)
            .ExecuteUpdateAsync(x => x.SetProperty(g => g.Capabilities, new[] { capability }));
        await db.Set<GrantProposal>().Where(x => x.RecipientId == actor && x.ProjectId != otherProject)
            .ExecuteUpdateAsync(x => x.SetProperty(g => g.EndsAtUtc, f.Clock.GetUtcNow()));
        var session = await f.SessionAsync(actor); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var people = new FinancePersonResolver(db, f.Clock); var requests = new RequestAccess(db, access, people, f.Clock);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
            Assert.True((await access.CheckAsync(actor, new(WorkspaceId, otherProject), capability, default)).Allowed);
            Assert.False((await access.CheckAsync(actor, root.Scope, capability, default)).Allowed);
        }
        var drafts = L.Draft(f, db, session);
        var service = new SupplementService(db, requests, drafts, new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var read = new RequestReadService(db, access, people, requests, drafts,
            new ScopeAccess(db, session, new EffectiveRolePolicy(db), f.Clock), f.Clock, new(db, access, session, f.Clock));
        var before = await Snapshot();
        var result = await service.CreateAsync(actor, root.Id, Content(30), L.Meta(0), default);
        Assert.Equal(403, result.Status); Assert.Equal("finance-access-denied", result.Code); Assert.Null(result.Value);
        var source = await read.DetailsAsync(actor, root.Id, default);
        Assert.Equal(403, source.Status); Assert.Null(source.Value);
        Assert.Equal(before, await Snapshot());

        async Task<string> Snapshot()
        {
            await using var check = f.CreateContext();
            return JsonSerializer.Serialize(new
            {
                requestsAndAudit = await L.Snapshot(f),
                families = await check.Set<AdvanceFamily>().OrderBy(x => x.Id).ToArrayAsync(),
                accounts = await check.Set<AdvancePaymentAccount>().OrderBy(x => x.Id).ToArrayAsync(),
                revisions = await check.Set<AdvanceRequestRevision>().OrderBy(x => x.Id).ToArrayAsync(),
                receipts = await check.Set<FinanceCommandReceipt>().OrderBy(x => x.CommandId).ToArrayAsync(),
                participants = await check.Set<AdvanceRequestParticipant>().OrderBy(x => x.Id).ToArrayAsync()
            });
        }
    }

    [Theory]
    [InlineData("Checker")]
    [InlineData("Payer")]
    public async Task Whole_family_history_excludes_role_switching_on_supplements(string role)
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        var supplement = await CreateSupplement(f, root, 30);
        await using var db = f.CreateContext(); var family = (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root.Id)).FamilyId;
        db.Add(new AdvancePaymentParticipation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = family,
            PersonId = ReviewerPersonId,
            Role = role,
            CreatedBy = ReviewerId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync();
        var submitted = await L.Submit(f, supplement); Assert.Equal("Held", submitted.State);
        Assert.Equal(403, (await L.Decide(f, submitted, "Approve")).Status);
        var second = await CreateSupplement(f, root, 20);
        Assert.Equal(409, (await Confirm(f, second, await Quote(f, second))).Status);
    }

    [Fact]
    public async Task Rejected_supplement_releases_slot_without_reducing_original_basis()
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        var pending = await L.Submit(f, await CreateSupplement(f, root, 30));
        Assert.Equal(200, (await L.Decide(f, pending, "Reject")).Status);
        var next = await CreateSupplement(f, root, 20);
        Assert.Equal(12000m, (await Quote(f, next)).Basis.Amount);
        Assert.Equal(200, (await Confirm(f, next, await Quote(f, next))).Status);
        await using var db = f.CreateContext();
        Assert.Equal(10000m, (await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == root.Id)).Spendable);
    }

    [Fact]
    public async Task Assignment_basis_does_not_grant_source_access_or_expose_source_content()
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        var pending = await L.Submit(f, await CreateSupplement(f, root, 30));
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == ReviewerId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(g => g.Capabilities, new[] { "request.approve" }));
        var session = await f.SessionAsync(ReviewerId); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var people = new FinancePersonResolver(db, f.Clock); var requests = new RequestAccess(db, access, people, f.Clock);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            Assert.True((await requests.CheckAsync(ReviewerId, pending.Id, "read", default)).Allowed);
            Assert.False((await requests.CheckAsync(ReviewerId, root.Id, "read", default)).Allowed);
        }
        var read = new RequestReadService(db, access, people, requests, L.Draft(f, db, session),
            new ScopeAccess(db, session, new EffectiveRolePolicy(db), f.Clock), f.Clock, new(db, access, session, f.Clock));
        var details = await read.DetailsAsync(ReviewerId, pending.Id, default); Assert.Equal(200, details.Status);
        Assert.Equal(13000m, details.Value!.Basis!.Amount);
        var wire = await read.RevisionAsync(ReviewerId, pending.Id, pending.RevisionId, default);
        Assert.Equal(200, wire.Status); Assert.Equal("13000.00", wire.Value!.Basis!.Amount);
        Assert.Equal(403, (await read.RevisionAsync(ReviewerId, root.Id, root.RevisionId, default)).Status);
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == pending.RevisionId);
        var basis = JsonSerializer.Deserialize<ApprovalBasis>(revision.ApprovalBasisJson)!;
        Assert.Equal(new[] { "ApprovedAmount", "RequestId", "RevisionId" }, typeof(ApprovalBasisItem).GetProperties().Select(x => x.Name).Order().ToArray());
        Assert.Equal(new[] { "ApprovedAmount", "RequestId", "RevisionId" }, typeof(ApprovalBasisItemHttp).GetProperties().Select(x => x.Name).Order().ToArray());
        Assert.Contains(basis.Items, x => x.RequestId == root.Id && x.ApprovedAmount == 10000m);
        var approved = await L.Decide(f, pending, "Approve"); Assert.Equal(200, approved.Status);
        await using var tx2 = await db.Database.BeginTransactionAsync();
        Assert.False((await requests.CheckAsync(ReviewerId, pending.Id, "read", default)).Allowed);
    }

    [Fact]
    public async Task Returned_supplement_requotes_current_policy_and_keeps_old_snapshot()
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        var pending = await L.Submit(f, await CreateSupplement(f, root, 30));
        var returned = await L.Decide(f, pending, "Return"); Assert.Equal(200, returned.Status);
        var frozen = await L.Frozen(f, pending.RevisionId);
        var revised = await L.Command(f, returned.Value!, "ReviseAsync"); Assert.Equal(200, revised.Status);
        var before = await Quote(f, revised.Value!);
        PolicyView company;
        await using (var db = f.CreateContext())
        {
            var review = await (from r in db.Set<PolicyReview>()
                                join a in db.Set<ActivePolicy>() on r.PolicyRevisionId equals a.PolicyRevisionId
                                where a.ProjectId == null
                                select r).SingleAsync();
            company = JsonSerializer.Deserialize<PolicyView>(review.ReviewedContentJson!)!;
        }
        await P.Review(f, await P.Submit(f, await P.Create(f, source: company, active: company,
            content: P.Content(9) with { Bands = [new(1, "director", 100000m)] })));
        Assert.Equal(409, (await Confirm(f, revised.Value!, before)).Status);
        var current = await Quote(f, revised.Value!); Assert.NotEqual(before.Policy.ContentHash, current.Policy.ContentHash);
        Assert.Equal(200, (await Confirm(f, revised.Value!, current)).Status);
        Assert.Equal(frozen, await L.Frozen(f, pending.RevisionId));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_payout_remains_available_and_existing_family_hold_is_preserved(bool held)
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        var payer = Guid.Parse("71000000-0000-0000-0000-000000000055");
        var checker = Guid.Parse("71000000-0000-0000-0000-000000000056");
        await Actor(f, payer, "payment.record"); await Actor(f, checker, "finance.check");
        await using (var db = f.CreateContext())
        {
            if (held)
            {
                var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == root.Id);
                db.Add(new AdvancePaymentHold
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WorkspaceId,
                    ProjectId = ProjectId,
                    FamilyId = r.FamilyId,
                    Code = "PaymentMismatch",
                    Reason = "ยอดจ่ายไม่ตรง",
                    CreatedBy = payer,
                    CreatedByPersonId = payer,
                    CreatedAtUtc = f.Clock.GetUtcNow()
                });
                await db.SaveChangesAsync();
            }
        }
        var supplement = await L.Submit(f, await CreateSupplement(f, root, 30));
        var create = await Payment(f, root.Id, payer, "Create");
        Assert.Equal(held ? 409 : 200, create.Status);
        Assert.Equal(409, (await Payment(f, supplement.Id, payer, "Create")).Status);
        if (!held)
        {
            var id = create.Value!.InstallmentId!.Value;
            Assert.Equal(200, (await Payment(f, root.Id, payer, "Submit", id)).Status);
            Assert.Equal(200, (await Payment(f, root.Id, checker, "Review", id)).Status);
            Assert.Equal(200, (await Payment(f, root.Id, payer, "Start", id)).Status);
        }
        await using var check = f.CreateContext();
        var holds = await check.Set<AdvancePaymentHold>().ToArrayAsync();
        Assert.Equal(held ? 1 : 0, holds.Length); Assert.All(holds, x => Assert.Null(x.ReleasedAtUtc));
        Assert.Equal(2000m, (await check.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == root.Id)).Paid);
        Assert.Equal(0m, (await check.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == supplement.Id)).Spendable);
    }

    [Fact]
    public async Task Confirmation_slot_and_snapshot_rollback_then_replay_is_single_effect()
    {
        await using var f = await Ready(); var root = await PaidRoot(f);
        var draft = await CreateSupplement(f, root, 30); var quote = await Quote(f, draft); var meta = L.Meta(draft.Version);
        await using var db = f.CreateContext(); var session = await f.SessionAsync(AuthorId);
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var audit = new FieldAdvanceRequestRaceTests.CallbackAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock),
            () => throw new InvalidOperationException("task10-injected-audit-failure"));
        var failing = new RequestSubmissionService(db, access, people, new(db, access, people, f.Clock), L.Draft(f, db, session),
            new(db, people, f.Clock), audit, f.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ConfirmAsync(AuthorId, draft.Id, quote.ConfirmationHash, meta, default));
        Assert.Null((await db.Set<AdvanceFamily>().SingleAsync()).PendingSupplementId);
        Assert.Null((await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == draft.RevisionId)).SubmittedAtUtc);
        Assert.Empty(await db.Set<AdvanceApprovalStep>().Where(x => x.RequestId == draft.Id).ToArrayAsync());
        Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == meta.CommandId));
        var service = L.Submission(f, db, session);
        var confirmed = await service.ConfirmAsync(AuthorId, draft.Id, quote.ConfirmationHash, meta, default); Assert.Equal(200, confirmed.Status);
        var replay = await service.ConfirmAsync(AuthorId, draft.Id, quote.ConfirmationHash, meta, default);
        Assert.Equal(JsonSerializer.Serialize(confirmed), JsonSerializer.Serialize(replay));
        Assert.Equal(draft.Id, (await db.Set<AdvanceFamily>().SingleAsync()).PendingSupplementId);
        Assert.Equal(3, await db.Set<AdvanceApprovalStep>().CountAsync(x => x.RequestId == draft.Id));
        Assert.Equal(1, await db.Set<AdvanceRequestEvent>().CountAsync(x => x.CommandId == meta.CommandId));
    }

    private static async Task<FinanceResult<PaymentView>> Payment(FieldAdvanceFixture f, Guid request, Guid actor, string operation, Guid? installment = null)
    {
        await using var db = f.CreateContext(); var session = await f.SessionAsync(actor);
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var executor = new PaymentCommandExecutor(db, new(db, access, people, new(db, access, people, f.Clock), f.Clock),
            new(db, session, access, people, f.Clock), new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var requestRow = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == requestRow.FamilyId);
        var installmentRow = installment is null ? null : await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installment);
        var meta = new PaymentCommand(Guid.NewGuid(), family.Version, requestRow.Version, installmentRow?.Version, "ทดสอบส่วนเพิ่ม");
        var ledger = new ReservationLedger(db, f.Clock); var service = new InstallmentService(db, executor, ledger, f.Clock);
        return operation switch
        {
            "Create" => await service.CreateAsync(actor, request, new("3000", new("Cash", AuthorPersonId, null, null, null)), meta, default),
            "Submit" => await service.SubmitAsync(actor, installment!.Value, meta, default),
            "Review" => await new InstallmentReviewService(db, executor, people, f.Clock).ReviewAsync(actor, installment!.Value, "Approve", meta, default),
            _ => await new PaymentExecutionService(db, executor, ledger, people, null!, new(), f.Clock).StartAsync(actor, installment!.Value, meta, default)
        };
    }
    private static async Task<SupplementService> Supplements(FieldAdvanceFixture f, Tpr10DbContext db, Guid actor)
    {
        var session = await f.SessionAsync(actor); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var people = new FinancePersonResolver(db, f.Clock);
        return new(db, new(db, access, people, f.Clock), L.Draft(f, db, session), new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    }
    private static async Task<RequestView> CreateSupplement(FieldAdvanceFixture f, RequestView source, int quantity)
    {
        await using var db = f.CreateContext(); var service = await Supplements(f, db, AuthorId);
        var result = await service.CreateAsync(AuthorId, source.Id, Content(quantity), L.Meta(0), default);
        Assert.Equal(201, result.Status); return result.Value!;
    }
    private static async Task<ConfirmationQuote> Quote(FieldAdvanceFixture f, RequestView view)
    {
        await using var db = f.CreateContext(); var session = await f.SessionAsync(AuthorId);
        var result = await L.Submission(f, db, session).QuoteAsync(AuthorId, view.Id, default);
        Assert.Equal(200, result.Status); return result.Value!;
    }
    private static async Task<FinanceResult<RequestView>> Confirm(FieldAdvanceFixture f, RequestView view, ConfirmationQuote quote)
    {
        await using var db = f.CreateContext(); var session = await f.SessionAsync(AuthorId);
        return await L.Submission(f, db, session).ConfirmAsync(AuthorId, view.Id, quote.ConfirmationHash, L.Meta(view.Version), default);
    }
    private static async Task<RequestView> PaidRoot(FieldAdvanceFixture f)
    {
        var root = await Original(f);
        // Established payment-account fixture: actual-payment history is represented by EverPaid/Paid.
        await using var db = f.CreateContext(); var account = await db.Set<AdvancePaymentAccount>().SingleAsync();
        account.Paid = 2000m; account.EverPaid = true; await db.SaveChangesAsync(); return root;
    }
    private static readonly Guid Director = Guid.Parse("71000000-0000-0000-0000-000000000051");
    private async Task<FieldAdvanceFixture> Ready()
    {
        var f = await L.Ready(postgres);
        await using (var db = f.CreateContext())
        {
            await db.Set<GrantProposal>().Where(x => x.ProjectId == ProjectId && (x.RecipientId == ReviewerId || x.RecipientId == RecipientId))
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.ApprovalLimit, 100000m));
        }
        // Publish a genuine current policy covering the larger ceiling.
        PolicyView company;
        await using (var db = f.CreateContext())
        {
            var review = await (from r in db.Set<PolicyReview>()
                                join a in db.Set<ActivePolicy>() on r.PolicyRevisionId equals a.PolicyRevisionId
                                where a.ProjectId == null
                                select r).SingleAsync();
            company = JsonSerializer.Deserialize<PolicyView>(review.ReviewedContentJson!)!;
        }
        await P.Review(f, await P.Submit(f, await P.Create(f, source: company, active: company,
            content: P.Content() with { Bands = [new(1, "director", 100000m)] })));
        await Actor(f, Director, "request.approve", "director");
        return f;
    }
    private static async Task Actor(FieldAdvanceFixture f, Guid actor, string capability, string? level = null)
    {
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlAsync($"INSERT INTO users(id,username,normalized_username) VALUES ({actor},{actor.ToString()},{actor.ToString()})");
        await db.Database.ExecuteSqlAsync($"INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) SELECT gen_random_uuid(),{actor},workspace_id,department_id,valid_from_utc,created_by,'task10' FROM employee_memberships WHERE user_id={AuthorId}");
        db.Add(new FinancePerson { Id = actor, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "ทดสอบ" });
        db.Add(new FinanceAccountLink { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, UserId = actor, PersonId = actor, CreatedBy = AuthorId, CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1), Reason = "ทดสอบ" });
        await db.SaveChangesAsync();
        var grant = await FieldAdvanceGrantTests.AuthorityAsync(f, actor, actor, limit: 100000m);
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == grant);
        var proposal = await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
        proposal.Capabilities = [capability]; proposal.ApprovalLevels = level is null ? [] : [level];
        proposal.PrimaryApprovalLevels = level is null ? [] : [level]; await db.SaveChangesAsync();
    }
    private static RequestContent Content(int quantity) => new("Individual", "ปรับวงเงิน", new(2026, 10, 24), new(2026, 10, 25), new(2026, 10, 23), [],
        [new(Guid.NewGuid(), Guid.Parse("78000000-0000-0000-0000-000000000001"), "ค่าเดินทาง", "QuantityRate", quantity.ToString(), null, null)]);
    private static async Task<RequestView> Original(FieldAdvanceFixture f, Guid? actor = null)
    {
        var who = actor ?? AuthorId; var session = await f.SessionAsync(who); await using var db = f.CreateContext();
        var draft = await L.Draft(f, db, session).CreateAsync(who, new(new(WorkspaceId, ProjectId), AuthorPersonId, Content(100)), L.Meta(0), default);
        Assert.Equal(201, draft.Status);
        if (actor is not null)
        {
            var sent = await L.Submission(f, db, session).SendToOwnerAsync(who, draft.Value!.Id, L.Meta(draft.Value.Version), default);
            Assert.Equal(200, sent.Status); draft = sent;
        }
        var approved = await Approve(f, await L.Submit(f, draft.Value!));
        // Task8 fixtures explicitly establish an approved account; approvals here are all real 7B decisions.
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync();
        account.ApprovedRevisionId = approved.RevisionId; account.Approved = account.Spendable = 10000m;
        await db.SaveChangesAsync(); return approved;
    }
    private static async Task<RequestView> Approve(FieldAdvanceFixture f, RequestView view)
    {
        foreach (var actor in new[] { ReviewerId, RecipientId, Director })
        { var result = await L.Decide(f, view, "Approve", actor); Assert.Equal(200, result.Status); view = result.Value!; }
        Assert.Equal("Approved", view.State); return view;
    }
}
