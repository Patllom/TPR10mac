using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.FieldAdvances.Evidence;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using L = TPR10.Api.IntegrationTests.FieldAdvanceRequestLifecycleTests;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceCeilingAmendmentTests(PostgresFixture postgres)
{
    private static readonly Guid Director = Guid.Parse("71000000-0000-0000-0000-000000000051");
    private static readonly Guid Payer = Guid.Parse("71000000-0000-0000-0000-000000000052");
    private static readonly Guid Checker = Guid.Parse("71000000-0000-0000-0000-000000000053");

    [Theory]
    [InlineData("CeilingAmendmentService")]
    [InlineData("PaymentDispositionService")]
    public void Amendment_commands_are_registered(string name)
        => Assert.Contains(new ServiceCollection().AddFieldAdvances(), x =>
            x.ServiceType.FullName == "TPR10.Api.FieldAdvances.Payments." + name && x.Lifetime == ServiceLifetime.Scoped);

    // Replacing the account at approval, releasing reservations at Begin, or allowing stale starts breaks this.
    [Theory]
    [InlineData(false, 130)]
    [InlineData(true, 130)]
    [InlineData(false, 20)]
    [InlineData(true, 20)]
    public async Task ActivationNeedsApprovalAndOldReservationsCleared(bool approvalFirst, int quantity)
    {
        await using var f = await Ready(); var original = await Original(f); var id = await Reviewed(f, original.Id);
        var old = await Account(f); var content = Content(quantity);
        var meta = await Meta(f, original.Id);
        var begun = Value(await Ceiling(f, original.Id, "Begin", content, meta));
        Assert.Equal(10000m, begun.Spendable); Assert.Equal(3000m, begun.Reserved);
        Assert.Equal(JsonSerializer.Serialize(begun), JsonSerializer.Serialize(Value(await Ceiling(f, original.Id, "Begin", content, meta))));
        Assert.Equal(409, (await Payment(f, original.Id, id, "Start")).Status);
        Assert.Equal(409, (await Ceiling(f, original.Id, "Activate")).Status);
        if (approvalFirst) await Approve(f, await L.Submit(f, await View(f, original.Id)));
        else await Cancel(f, original.Id, id);
        var beforeReady = await Account(f); Assert.Equal(10000m, beforeReady.Spendable);
        Assert.Equal(old.ApprovedRevisionId, beforeReady.ApprovedRevisionId);
        Assert.Equal(409, (await Ceiling(f, original.Id, "Activate")).Status);
        if (approvalFirst) await Cancel(f, original.Id, id);
        else await Approve(f, await L.Submit(f, await View(f, original.Id)));
        var ready = Value(await Ceiling(f, original.Id, "Activate")); Assert.Equal(quantity * 100m, ready.Spendable);
        Assert.Equal(0, ready.Reserved); Assert.DoesNotContain("CeilingAmendment", ready.HoldCodes);
        Assert.Equal(409, (await Payment(f, original.Id, id, "Start")).Status);
        Assert.Equal(200, (await Payment(f, original.Id, null, "Create")).Status);
        await using var db = f.CreateContext();
        Assert.Equal(6, await db.Set<AdvanceApprovalStep>().CountAsync(x => x.Outcome == "Approved"));
        Assert.Equal("Cancelled", (await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == id)).State);
    }

    [Fact]
    public async Task EverPaidBlocksOriginalAmendment()
    {
        await using var f = await Ready(); var original = await Original(f);
        await using (var db = f.CreateContext())
        { var a = await db.Set<AdvancePaymentAccount>().SingleAsync(); a.EverPaid = true; await db.SaveChangesAsync(); }
        Assert.Equal(409, (await Ceiling(f, original.Id, "Begin", Content(130))).Status);
        Assert.Equal(10000, (await Account(f)).Spendable);
        await using var check = f.CreateContext(); Assert.Single(await check.Set<AdvanceRequestRevision>().ToArrayAsync());
    }

    [Theory]
    [InlineData("ResumeOriginal", false)]
    [InlineData("ResumeOriginal", true)]
    [InlineData("TerminateRemaining", false)]
    [InlineData("TerminateRemaining", true)]
    public async Task DispositionRetainsOriginalAndRequiresIndependentCurrentFinalRole(string outcome, bool reject)
    {
        await using var f = await Ready(); var original = await Original(f); var id = await Reviewed(f, original.Id);
        Value(await Ceiling(f, original.Id, "Begin", Content(130)));
        var draft = await View(f, original.Id);
        if (reject) Assert.Equal(200, (await L.Decide(f, await L.Submit(f, draft), "Reject")).Status);
        else Assert.Equal(200, (await L.Command(f, draft, "WithdrawAsync")).Status);
        Assert.Equal(10000m, (await Account(f)).Spendable);
        Assert.Equal(409, (await Payment(f, original.Id, id, "Start")).Status);
        Assert.Equal(403, (await Disposition(f, original.Id, "Propose", outcome, actor: AuthorId)).Status);
        Value(await Disposition(f, original.Id, "Propose", outcome));
        var proposal = await Proposal(f, "PaymentDisposition");
        Assert.Equal(403, (await Disposition(f, original.Id, "Confirm", proposal, actor: Checker)).Status);
        if (outcome == "TerminateRemaining")
        {
            Assert.Equal(409, (await Disposition(f, original.Id, "Confirm", proposal)).Status);
            await Cancel(f, original.Id, id);
            Value(await Disposition(f, original.Id, "Propose", outcome)); proposal = await Proposal(f, "PaymentDisposition");
        }
        Value(await Disposition(f, original.Id, "Confirm", proposal));
        var account = await Account(f);
        Assert.Equal(10000m, account.Approved); Assert.Equal(original.RevisionId, account.ApprovedRevisionId);
        Assert.Equal(outcome == "ResumeOriginal" ? 10000m : 0m, account.Spendable);
        Assert.Equal("Approved", (await View(f, original.Id)).State);
        Assert.Equal(outcome == "ResumeOriginal" ? 200 : 409, (await Payment(f, original.Id, id, "Start")).Status);
    }

    [Fact]
    public async Task BeginRacesStartWithoutBothSucceeding()
    {
        await using var f = await Ready(); var original = await Original(f); var id = await Reviewed(f, original.Id);
        var meta = await Meta(f, original.Id); var paymentMeta = await Meta(f, original.Id, id);
        var result = await Task.WhenAll(Ceiling(f, original.Id, "Begin", Content(130), meta), Payment(f, original.Id, id, "Start", paymentMeta));
        Assert.Single(result, x => x.Status == 200); Assert.Single(result, x => x.Status == 409);
        Assert.Equal(3000m, (await Account(f)).Reserved);
    }

    [Fact]
    public async Task StartedAttemptBlocksBeginWithoutChangingRevisionOrReservation()
    {
        await using var f = await Ready(); var original = await Original(f); var id = await Reviewed(f, original.Id);
        Value(await Payment(f, original.Id, id, "Start"));
        Assert.Equal(409, (await Ceiling(f, original.Id, "Begin", Content(130))).Status);
        Assert.Equal(original.RevisionId, (await View(f, original.Id)).RevisionId);
        Assert.Equal(3000m, (await Account(f)).Reserved);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("audit-expired")]
    public async Task AuthorRevocationAndExpiryRollbackBegin(string mode)
    {
        var expireDuringAudit = mode == "audit-expired";
        await using var f = await Ready(); var original = await Original(f); var meta = await Meta(f, original.Id);
        if (mode == "revoked") await Revoke(f, AuthorId);
        else await SetExpiry(f, AuthorId, expireDuringAudit ? f.Clock.GetUtcNow().AddMinutes(1) : f.Clock.GetUtcNow());
        var result = await Ceiling(f, original.Id, "Begin", Content(130), meta,
            auditAction: expireDuringAudit ? () => f.Clock.Advance(TimeSpan.FromMinutes(2)) : null);
        Assert.Equal(403, result.Status);
        await using var db = f.CreateContext();
        Assert.Single(await db.Set<AdvanceRequestRevision>().ToArrayAsync()); Assert.Empty(await db.Set<AdvancePaymentHold>().ToArrayAsync());
        Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == meta.CommandId));
        Assert.Equal(original.RevisionId, (await View(f, original.Id)).RevisionId);
        Assert.Equal(meta, (await Meta(f, original.Id)) with { CommandId = meta.CommandId });
    }

    [Fact]
    public async Task AuditFailureRollsBackHoldRevisionReceiptAndVersions()
    {
        await using var f = await Ready(); var original = await Original(f); var meta = await Meta(f, original.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ceiling(f, original.Id, "Begin", Content(130), meta,
            auditAction: () => throw new InvalidOperationException("injected task9 audit failure")));
        await using var db = f.CreateContext();
        Assert.Single(await db.Set<AdvanceRequestRevision>().ToArrayAsync()); Assert.Empty(await db.Set<AdvancePaymentHold>().ToArrayAsync());
        Assert.False(await db.Set<FinanceCommandReceipt>().AnyAsync(x => x.CommandId == meta.CommandId));
        Assert.Equal(meta, (await Meta(f, original.Id)) with { CommandId = meta.CommandId });
    }

    [Fact]
    public async Task DelegateNeedsCurrentAuthorAndOwnerEligibilityIncludingReplay()
    {
        await using var f = await Ready(); var delegated = Guid.Parse("71000000-0000-0000-0000-000000000054");
        await Actor(f, delegated, "request.delegate"); var original = await Original(f, delegated);
        Assert.Equal(403, (await Ceiling(f, original.Id, "Begin", Content(130))).Status);
        var meta = await Meta(f, original.Id); var content = Content(130);
        Value(await Ceiling(f, original.Id, "Begin", content, meta, delegated));
        await using (var db = f.CreateContext())
        { Assert.False(await db.Set<AdvancePaymentParticipation>().AnyAsync(x => x.PersonId == delegated || x.PersonId == AuthorPersonId)); }
        await SetExpiry(f, AuthorId, f.Clock.GetUtcNow());
        Assert.Equal(403, (await Ceiling(f, original.Id, "Begin", content, meta, delegated)).Status);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("audit-expired")]
    public async Task DispositionRequiresCurrentCheckerThroughFinalAuthorization(string mode)
    {
        var duringAudit = mode == "audit-expired";
        await using var f = await Ready(); var original = await Original(f);
        Value(await Ceiling(f, original.Id, "Begin", Content(130)));
        Assert.Equal(200, (await L.Command(f, await View(f, original.Id), "WithdrawAsync")).Status);
        Value(await Disposition(f, original.Id, "Propose", "ResumeOriginal")); var proposal = await Proposal(f, "PaymentDisposition");
        if (mode == "revoked") await Revoke(f, Checker);
        else await SetExpiry(f, Checker, duringAudit ? f.Clock.GetUtcNow().AddMinutes(1) : f.Clock.GetUtcNow());
        Assert.Equal(403, (await Disposition(f, original.Id, "Confirm", proposal,
            auditAction: duringAudit ? () => f.Clock.Advance(TimeSpan.FromMinutes(2)) : null)).Status);
        await using var db = f.CreateContext(); Assert.Equal("Pending", (await db.Set<AdvancePaymentProposal>().SingleAsync(x => x.Id == proposal)).State);
        Assert.Null((await db.Set<AdvancePaymentHold>().SingleAsync()).ReleasedAtUtc);
        Assert.Equal("Withdrawn", (await View(f, original.Id)).State); Assert.Equal(10000, (await Account(f)).Spendable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrExpiredFinalHolderLeavesAmendmentHeld(bool duringAudit)
    {
        await using var f = await Ready(); var original = await Original(f);
        Value(await Ceiling(f, original.Id, "Begin", Content(130)));
        Assert.Equal(200, (await L.Command(f, await View(f, original.Id), "WithdrawAsync")).Status);
        Value(await Disposition(f, original.Id, "Propose", "ResumeOriginal")); var proposal = await Proposal(f, "PaymentDisposition");
        await SetExpiry(f, Director, duringAudit ? f.Clock.GetUtcNow().AddMinutes(1) : f.Clock.GetUtcNow());
        Assert.Equal(403, (await Disposition(f, original.Id, "Confirm", proposal,
            auditAction: duringAudit ? () => f.Clock.Advance(TimeSpan.FromMinutes(2)) : null)).Status);
        await using var db = f.CreateContext(); Assert.Null((await db.Set<AdvancePaymentHold>().SingleAsync()).ReleasedAtUtc);
        Assert.Equal("Withdrawn", (await View(f, original.Id)).State);
    }

    [Fact]
    public async Task ResumeReactivatesOriginalRetainsAmendmentAndMismatchAndRequiresFreshMetadata()
    {
        await using var f = await Ready(); var original = await Original(f); var id = await Reviewed(f, original.Id);
        var stale = await Meta(f, original.Id, id);
        Value(await Ceiling(f, original.Id, "Begin", Content(130))); var amended = await View(f, original.Id);
        Assert.Equal(200, (await L.Command(f, amended, "WithdrawAsync")).Status);
        await using (var db = f.CreateContext())
        {
            var request = await db.Set<AdvanceRequest>().SingleAsync();
            db.Add(new AdvancePaymentHold
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                FamilyId = request.FamilyId,
                Code = "PaymentMismatch",
                Reason = "เหตุอื่นคงอยู่",
                CreatedBy = Checker,
                CreatedByPersonId = Checker,
                CreatedAtUtc = f.Clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }
        Value(await Disposition(f, original.Id, "Propose", "ResumeOriginal")); var proposal = await Proposal(f, "PaymentDisposition");
        var meta = await Meta(f, original.Id);
        var resumed = Value(await Disposition(f, original.Id, "Confirm", proposal, meta: meta));
        Assert.Contains("PaymentMismatch", resumed.HoldCodes); Assert.DoesNotContain("CeilingAmendment", resumed.HoldCodes);
        Assert.Equal(JsonSerializer.Serialize(resumed), JsonSerializer.Serialize(Value(await Disposition(f, original.Id, "Confirm", proposal, meta: meta))));
        Assert.Equal(409, (await Disposition(f, original.Id, "Confirm", proposal)).Status);
        Assert.Equal(409, (await Payment(f, original.Id, id, "Start", stale)).Status);
        Assert.Equal(409, (await Payment(f, original.Id, id, "Start")).Status);
        await using var check = f.CreateContext();
        Assert.True((await check.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == original.RevisionId)).IsCurrent);
        var kept = await check.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == amended.RevisionId);
        Assert.False(kept.IsCurrent); Assert.Equal(13000m, kept.Total);
        Assert.Equal(3, await check.Set<AdvanceApprovalStep>().CountAsync(x => x.RevisionId == original.RevisionId && x.Outcome == "Approved"));
    }

    [Fact]
    public async Task BeginRejectsChangedReplayPayloadAndStaleMetadata()
    {
        await using var f = await Ready(); var original = await Original(f); var meta = await Meta(f, original.Id); var content = Content(130);
        Value(await Ceiling(f, original.Id, "Begin", content, meta));
        Assert.Equal(409, (await Ceiling(f, original.Id, "Begin", content with { Purpose = "แก้เหตุผล" }, meta)).Status);
        Assert.Equal(409, (await Ceiling(f, original.Id, "Activate", meta: meta with { CommandId = Guid.NewGuid() })).Status);
        await using var db = f.CreateContext(); Assert.Equal(2, await db.Set<AdvanceRequestRevision>().CountAsync());
        Assert.Single(await db.Set<AdvancePaymentHold>().ToArrayAsync());
    }

    [Fact]
    public async Task StartedUnknownMustWaitForIndependentUnpaidThenCancellation()
    {
        await using var f = await Ready(); var original = await Original(f); var id = await Reviewed(f, original.Id);
        Value(await Payment(f, original.Id, id, "Start"));
        using var files = new EvidenceFiles(); var evidence = await Evidence(f, files, original.Id);
        Value(await Recover(f, files, original.Id, id, evidence, "Unknown"));
        Value(await Recover(f, files, original.Id, id, evidence, "Confirm", await Proposal(f, "PaymentOutcome")));
        Assert.Equal(409, (await Ceiling(f, original.Id, "Begin", Content(130))).Status);
        Value(await Recover(f, files, original.Id, id, evidence, "Unpaid"));
        Assert.Equal(409, (await Ceiling(f, original.Id, "Begin", Content(130))).Status);
        Value(await Recover(f, files, original.Id, id, evidence, "Confirm", await Proposal(f, "PaymentOutcome")));
        Value(await Ceiling(f, original.Id, "Begin", Content(130))); Assert.Equal(3000, (await Account(f)).Reserved);
        await Cancel(f, original.Id, id); await Approve(f, await L.Submit(f, await View(f, original.Id)));
        Assert.Equal(13000, Value(await Ceiling(f, original.Id, "Activate")).Spendable);
    }

    [Fact]
    public async Task PolicyChangeRequiresFreshOwnerConfirmationAndFullApproval()
    {
        await using var f = await Ready(); var original = await Original(f);
        Value(await Ceiling(f, original.Id, "Begin", Content(130))); var view = await View(f, original.Id);
        var session = await f.SessionAsync(AuthorId); await using var db = f.CreateContext();
        var submission = L.Submission(f, db, session); var quote = await submission.QuoteAsync(AuthorId, original.Id, default);
        Assert.Equal(200, quote.Status);
        var review = await (from r in db.Set<PolicyReview>()
                            join a in db.Set<ActivePolicy>() on r.PolicyRevisionId equals a.PolicyRevisionId
                            where a.ProjectId == null
                            select r).SingleAsync();
        var company = JsonSerializer.Deserialize<PolicyView>(review.ReviewedContentJson!)!;
        await P.Review(f, await P.Submit(f, await P.Create(f, source: company, active: company,
            content: P.Content(8) with { Bands = [new(1, "director", 100000m)] })));
        Assert.Equal(409, (await submission.ConfirmAsync(AuthorId, original.Id, quote.Value!.ConfirmationHash, L.Meta(view.Version), default)).Status);
        Assert.Equal(10000, (await Account(f)).Spendable);
        await Approve(f, await L.Submit(f, view)); Assert.Equal(13000, Value(await Ceiling(f, original.Id, "Activate")).Spendable);
    }

    [Fact]
    public async Task ActivationRacesCancellationConfirmationAndStart()
    {
        await using var f = await Ready(); var original = await Original(f); var id = await Reviewed(f, original.Id);
        Value(await Ceiling(f, original.Id, "Begin", Content(130))); await Approve(f, await L.Submit(f, await View(f, original.Id)));
        Value(await Payment(f, original.Id, id, "CancelPropose")); var proposal = await Proposal(f, "PaymentCancellation");
        var meta = await Meta(f, original.Id); var installmentMeta = await Meta(f, original.Id, id);
        var result = await Task.WhenAll(Ceiling(f, original.Id, "Activate", meta: meta),
            Payment(f, original.Id, id, "CancelConfirm", installmentMeta, proposal));
        Assert.Equal(409, result[0].Status); Assert.Equal(200, result[1].Status);
        Assert.Equal(10000, (await Account(f)).Spendable);
        meta = await Meta(f, original.Id); installmentMeta = await Meta(f, original.Id, id);
        result = await Task.WhenAll(Ceiling(f, original.Id, "Activate", meta: meta), Payment(f, original.Id, id, "Start", installmentMeta));
        Assert.Equal(200, result[0].Status); Assert.Equal(409, result[1].Status);
        Assert.Equal(13000, (await Account(f)).Spendable);
    }

    [Fact]
    public async Task StaleApprovedRevisionCannotStartWithoutAmendmentHold()
    {
        await using var f = await Ready(); var original = await Original(f); var id = await Reviewed(f, original.Id);
        // Simulate a stale persisted active pointer: the account still references a fully approved historical revision.
        await using (var db = f.CreateContext())
        {
            var old = await db.Set<AdvanceRequestRevision>().SingleAsync(); old.IsCurrent = false; await db.SaveChangesAsync();
            db.Add(new AdvanceRequestRevision
            {
                Id = Guid.NewGuid(),
                RequestId = original.Id,
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RevisionNumber = 2,
                IsCurrent = true,
                ContentJson = old.ContentJson,
                ContentHash = old.ContentHash,
                Total = old.Total,
                CreatedBy = AuthorId,
                CreatedByPersonId = AuthorPersonId,
                CreatedAtUtc = f.Clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(409, (await Payment(f, original.Id, id, "Start")).Status); Assert.Equal(3000, (await Account(f)).Reserved);
    }

    private static async Task<Guid> Evidence(FieldAdvanceFixture f, EvidenceFiles files, Guid request)
    {
        var key = Guid.NewGuid().ToString("N"); var stored = await files.Store.WriteImmutableAsync(files.Options.ActiveStorageId, key, new MemoryStream([1, 2, 3]), default);
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request); var id = Guid.NewGuid();
        db.Add(new AdvanceEvidence
        {
            Id = id,
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            Sha256 = stored.Sha256,
            Length = stored.Length,
            ContentType = "image/png",
            State = "Ready",
            CreatedBy = Payer,
            CreatedByPersonId = Payer,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        db.Add(new AdvanceEvidenceLocation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            EvidenceId = id,
            StorageKey = new EvidenceLocator(files.Options.ActiveStorageId, key).ToString(),
            IsCurrent = true,
            VerifiedAtUtc = f.Clock.GetUtcNow(),
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync(); return id;
    }
    private static async Task<FinanceResult<PaymentView>> Recover(FieldAdvanceFixture f, EvidenceFiles files, Guid request, Guid id, Guid evidenceId, string outcome, Guid? proposal = null)
    {
        var actor = outcome == "Confirm" ? Checker : Payer; await using var db = f.CreateContext(); using var provider = await Provider(f, db, actor);
        var evidence = new FinanceEvidenceService(db, provider.GetRequiredService<PaymentAccess>(), files.Store, new RejectPdf(), files.Options, f.Clock);
        var service = new PaymentOutcomeService(db, provider.GetRequiredService<PaymentCommandExecutor>(), new(db, f.Clock), provider.GetRequiredService<FinancePersonResolver>(), evidence, files.Options, f.Clock);
        var meta = await Meta(f, request, id);
        return outcome == "Confirm" ? await service.ConfirmAsync(actor, proposal!.Value, meta, default)
            : await service.ProposeAsync(actor, id, new(outcome, null, [evidenceId]), meta, default);
    }
    private sealed class RejectPdf : IFinancePdfValidator
    { public Task<bool> ValidateAsync(byte[] content, CancellationToken ct) => Task.FromResult(false); }
    private sealed class EvidenceFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "task9-evidence-" + Guid.NewGuid().ToString("N"));
        public FinanceEvidenceOptions Options { get; }
        public FolderFinanceEvidenceStore Store { get; }
        public EvidenceFiles()
        {
            root = OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + root : root;
            Directory.CreateDirectory(root); var id = Guid.NewGuid();
            Options = new() { ActiveStorageId = id, Roots = new() { [id] = root } }; Store = new(Options);
        }
        public void Dispose() => Directory.Delete(root, true);
    }

    private static async Task Revoke(FieldAdvanceFixture f, Guid actor)
    {
        await using var db = f.CreateContext();
        var proposal = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == actor && x.ProjectId == ProjectId);
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.ProposalId == proposal.Id);
        assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = AuthorId; assignment.RevocationReason = "ถอนสิทธิ์ทดสอบ";
        await db.SaveChangesAsync();
    }

    private static async Task SetExpiry(FieldAdvanceFixture f, Guid actor, DateTimeOffset at)
    {
        await using var db = f.CreateContext(); await db.Set<GrantProposal>().Where(x => x.RecipientId == actor && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, at));
    }

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
        await Actor(f, Payer, "payment.record"); await Actor(f, Checker, "finance.check"); return f;
    }
    private static async Task Actor(FieldAdvanceFixture f, Guid actor, string capability, string? level = null)
    {
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlAsync($"INSERT INTO users(id,username,normalized_username) VALUES ({actor},{actor.ToString()},{actor.ToString()})");
        await db.Database.ExecuteSqlAsync($"INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) SELECT gen_random_uuid(),{actor},workspace_id,department_id,valid_from_utc,created_by,'task9' FROM employee_memberships WHERE user_id={AuthorId}");
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
    private static async Task<RequestView> View(FieldAdvanceFixture f, Guid id)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
        var v = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == id && x.IsCurrent);
        return new(id, v.Id, r.Version, r.State, new(r.WorkspaceId, r.ProjectId), r.OwnerPersonId, r.DelegatePersonId,
            JsonSerializer.Deserialize<RequestContent>(v.ContentJson)!, null, r.HoldCode);
    }
    private static async Task<AdvancePaymentAccount> Account(FieldAdvanceFixture f)
    { await using var db = f.CreateContext(); return await db.Set<AdvancePaymentAccount>().SingleAsync(); }
    private static async Task<PaymentCommand> Meta(FieldAdvanceFixture f, Guid id, Guid? installment = null)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == id);
        return new(Guid.NewGuid(), (await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == r.FamilyId)).Version, r.Version,
            installment is null ? null : (await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installment)).Version, "ทดสอบปรับวงเงิน");
    }
    private static PaymentView Value(FinanceResult<PaymentView> result)
    { Assert.Equal(200, result.Status); return Assert.IsType<PaymentView>(result.Value); }
    private static async Task<Guid> Proposal(FieldAdvanceFixture f, string kind)
    { await using var db = f.CreateContext(); return await db.Set<AdvancePaymentProposal>().Where(x => x.Kind == kind).OrderByDescending(x => x.RequestVersion).Select(x => x.Id).FirstAsync(); }
    private static async Task<Guid> Reviewed(FieldAdvanceFixture f, Guid request)
    {
        var id = Value(await Payment(f, request, null, "Create")).InstallmentId!.Value;
        Value(await Payment(f, request, id, "Submit")); Value(await Payment(f, request, id, "Review")); return id;
    }
    private static async Task Cancel(FieldAdvanceFixture f, Guid request, Guid id)
    {
        Value(await Payment(f, request, id, "CancelPropose"));
        Value(await Payment(f, request, id, "CancelConfirm", proposal: await Proposal(f, "PaymentCancellation")));
    }
    private static async Task<ServiceProvider> Provider(FieldAdvanceFixture f, Tpr10DbContext db, Guid actor, Action? auditAction = null)
    {
        var session = await f.SessionAsync(actor); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var paymentAccess = new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock);
        var services = new ServiceCollection().AddSingleton(db).AddSingleton(access).AddSingleton(people)
            .AddSingleton(new RequestAccess(db, access, people, f.Clock)).AddSingleton(L.Draft(f, db, session))
            .AddSingleton(new ApprovalRouteResolver(db, people, f.Clock)).AddSingleton<TimeProvider>(f.Clock)
            .AddSingleton<IAuditEventWriter>(new CallbackAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock), auditAction))
            .AddSingleton(paymentAccess).AddSingleton(new FamilyParticipation(db, session, access, people, f.Clock))
            .AddSingleton(new ReservationLedger(db, f.Clock)).AddTransient<PaymentCommandExecutor>();
        return services.BuildServiceProvider();
    }
    private static async Task<FinanceResult<PaymentView>> Ceiling(FieldAdvanceFixture f, Guid request, string method, RequestContent? content = null, PaymentCommand? meta = null, Guid? actor = null, Action? auditAction = null)
    {
        await using var db = f.CreateContext(); using var provider = await Provider(f, db, actor ?? AuthorId, auditAction);
        var type = typeof(PaymentAccess).Assembly.GetType("TPR10.Api.FieldAdvances.Payments.CeilingAmendmentService"); Assert.NotNull(type);
        var service = ActivatorUtilities.CreateInstance(provider, type); meta ??= await Meta(f, request);
        object[] args = method == "Begin" ? [actor ?? AuthorId, request, content!, meta, CancellationToken.None] : [actor ?? AuthorId, request, meta, CancellationToken.None];
        return await (Task<FinanceResult<PaymentView>>)type.GetMethod(method + "Async")!.Invoke(service, args)!;
    }
    private static async Task<FinanceResult<PaymentView>> Disposition(FieldAdvanceFixture f, Guid request, string method, object input, Guid? actor = null, Action? auditAction = null, PaymentCommand? meta = null)
    {
        var who = actor ?? (method == "Propose" ? Checker : Director);
        await using var db = f.CreateContext(); using var provider = await Provider(f, db, who, auditAction);
        var type = typeof(PaymentAccess).Assembly.GetType("TPR10.Api.FieldAdvances.Payments.PaymentDispositionService"); Assert.NotNull(type);
        var service = ActivatorUtilities.CreateInstance(provider, type); meta ??= await Meta(f, request);
        object[] args = method == "Propose" ? [who, request, input, meta, CancellationToken.None] : [who, input, meta, CancellationToken.None];
        return await (Task<FinanceResult<PaymentView>>)type.GetMethod(method + "Async")!.Invoke(service, args)!;
    }
    private static async Task<FinanceResult<PaymentView>> Payment(FieldAdvanceFixture f, Guid request, Guid? id, string operation, PaymentCommand? meta = null, Guid? proposal = null)
    {
        var actor = operation is "Review" or "CancelConfirm" ? Checker : Payer;
        await using var db = f.CreateContext(); using var provider = await Provider(f, db, actor);
        var executor = provider.GetRequiredService<PaymentCommandExecutor>(); var people = provider.GetRequiredService<FinancePersonResolver>(); var ledger = new ReservationLedger(db, f.Clock);
        var service = new InstallmentService(db, executor, ledger, f.Clock); meta ??= await Meta(f, request, id);
        var cancellation = new PaymentCancellationService(db, executor, ledger, people, null!, new(), f.Clock);
        return operation switch
        {
            "Create" => await service.CreateAsync(actor, request, new("3000", new("Cash", AuthorPersonId, null, null, null)), meta, default),
            "Submit" => await service.SubmitAsync(actor, id!.Value, meta, default),
            "Review" => await new InstallmentReviewService(db, executor, people, f.Clock).ReviewAsync(actor, id!.Value, "Approve", meta, default),
            "CancelPropose" => await cancellation.ProposeAsync(actor, id!.Value, meta, default),
            "CancelConfirm" => await cancellation.ConfirmAsync(actor, proposal!.Value, meta, default),
            _ => await new PaymentExecutionService(db, executor, ledger, people, null!, new(), f.Clock).StartAsync(actor, id!.Value, meta, default)
        };
    }
    private sealed class CallbackAudit(IAuditEventWriter inner, Action? action) : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null)
            => inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
        { await inner.WriteAsync(request, ct); action?.Invoke(); }
    }

}
