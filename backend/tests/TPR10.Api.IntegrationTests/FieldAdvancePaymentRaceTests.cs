using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Evidence;
using TPR10.Api.FieldAdvances.Payments;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePaymentRaceTests(PostgresFixture postgres)
{
    internal static readonly Guid Checker = Guid.Parse("71000000-0000-0000-0000-000000000045");
    internal static readonly Guid CheckerPerson = Guid.Parse("72000000-0000-0000-0000-000000000045");

    // การข้าม lock/รุ่นข้อมูลทำให้เริ่มจ่ายหลังเหตุยุติ หรือบันทึกทั้งสองคำสั่งได้
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Start_and_termination_or_amendment_have_one_serialized_winner(bool startFirst, bool amendment)
    {
        await using var f = await Ready(postgres); var (request, id) = await Reviewed(f); using var files = new Files();
        if (amendment)
        {
            await Grant(f, AuthorId, AuthorPersonId, "request.create");
            foreach (var (actor, person) in new[] { (AuthorId, AuthorPersonId), (ReviewerId, ReviewerPersonId) })
            {
                await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person, company: true);
                await using var policyDb = f.CreateContext();
                await policyDb.Set<GrantProposal>().Where(x => x.RecipientId == actor && x.ProjectId == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "policy.edit.company", "policy.review" }));
            }
            var policy = await FieldAdvancePolicyTests.Run(f, AuthorId, service => service.CreateDraftAsync(AuthorId,
                new(WorkspaceId, null), null, FieldAdvancePolicyTests.Content() with { Bands = [new(1, "director", 10000m)] },
                new(Guid.NewGuid(), 1, "กติกาสำหรับทดสอบปรับวงเงิน") { Policy = new("Company", SourcePolicyRevisionId: CompanyRevisionId) }, default));
            Assert.Equal(201, policy.Status);
            await FieldAdvancePolicyTests.Review(f, await FieldAdvancePolicyTests.Submit(f, policy.Value!));
        }
        var startMeta = await Meta(f, request, id); var stopMeta = await Meta(f, request, null);
        var gate = new Barrier();
        Task<FinanceResult<PaymentView>> Start() => Call(f, request, id, "Start", files, meta: startMeta,
            audit: db => gate.Audit(f, startFirst, db));
        async Task<FinanceResult<PaymentView>> Stop()
        {
            var actor = amendment ? AuthorId : Checker;
            var session = await f.SessionAsync(actor); await using var db = f.CreateContext();
            var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
            var pa = new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock);
            var executor = new PaymentCommandExecutor(db, pa, new(db, session, access, people, f.Clock),
                gate.Audit(f, !startFirst, db), f.Clock);
            if (amendment)
            {
                var content = new TPR10.Api.FieldAdvances.Requests.RequestContent("Individual", "ปรับวงเงิน", new(2026, 10, 24), new(2026, 10, 25), new(2026, 10, 23), [],
                    [new(Guid.NewGuid(), Guid.Parse("78000000-0000-0000-0000-000000000001"), "ค่าเดินทาง", "QuantityRate", "30", null, null)]);
                return await new CeilingAmendmentService(db, executor, FieldAdvanceRequestLifecycleTests.Draft(f, db, session), people, f.Clock)
                    .BeginAsync(actor, request, content, stopMeta, default);
            }
            return await new PaymentDispositionService(db, executor, people, f.Clock)
                .ProposeAsync(Checker, request, "TerminateRemaining", stopMeta, default);
        }
        var first = startFirst ? Start() : Stop();
        await Task.WhenAny(gate.Entered.Task, first).WaitAsync(TimeSpan.FromSeconds(15));
        if (first.IsCompleted) Assert.Fail("คำสั่งแรกจบก่อน audit barrier: " + JsonSerializer.Serialize(await first));
        var second = startFirst ? Stop() : Start();
        try { await Waiting(f, second); } finally { gate.Release.TrySetResult(); }
        Assert.Equal(200, (await first).Status); Assert.Equal(409, (await second).Status);
        await using var verify = f.CreateContext();
        Assert.Equal(startFirst ? 1 : 0, await verify.Set<AdvancePaymentAttempt>().CountAsync());
        Assert.Equal(startFirst ? 0 : 1, await verify.Set<AdvancePaymentHold>().CountAsync(x => x.Code == (amendment ? "CeilingAmendment" : "PaymentTermination") && x.ReleasedAtUtc == null));
        Assert.Equal(1, await verify.Set<FinanceCommandReceipt>().CountAsync(x => x.CommandId == startMeta.CommandId || x.CommandId == stopMeta.CommandId));
        await Unpaid(f);
    }

    // การอ่านสิทธิ์ก่อนรอ lock แล้วใช้ผลเก่าทำให้ผู้ถูกถอนสิทธิ์ยังตรวจ/เริ่มจ่ายได้
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Committed_revocation_while_command_waits_denies_checker_or_payer(bool checker)
    {
        await using var f = await Ready(postgres); var request = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000m, default);
        var id = Value(await Prepare(f, request, null, "Create")).InstallmentId!.Value;
        Value(await Prepare(f, request, id, "Submit"));
        if (!checker) Value(await Prepare(f, request, id, "Approve"));
        using var files = new Files();
        await using var revoker = f.CreateContext(); await using var tx = await revoker.Database.BeginTransactionAsync();
        await revoker.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var actor = checker ? Checker : RecipientId;
        var grants = await revoker.Set<GrantAssignment>().Where(x => revoker.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == actor)).ToArrayAsync();
        Assert.NotEmpty(grants);
        foreach (var grant in grants) { grant.RevokedAtUtc = f.Clock.GetUtcNow(); grant.RevokedBy = AuthorId; grant.RevocationReason = "ถอนสิทธิ์ในฐานทดสอบ"; }
        await revoker.SaveChangesAsync();
        var command = checker ? Prepare(f, request, id, "Approve") : Call(f, request, id, "Start", files);
        try { await Waiting(f, command); } finally { await tx.CommitAsync(); }
        var result = await command; Assert.Equal(403, result.Status); Assert.Null(result.Value);
        await using var verify = f.CreateContext();
        Assert.Empty(await verify.Set<AdvancePaymentAttempt>().ToArrayAsync());
        Assert.Equal(checker ? "PendingReview" : "Reviewed", (await verify.Set<AdvanceInstallment>().SingleAsync()).State);
        await Unpaid(f);
    }

    // เหตุจ่ายผิดต้องแข่งบน lock ชุดเดียวกับ start และไม่ทำให้ยอดจริงหาย
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mismatch_and_sibling_installment_start_serialize_without_losing_hold(bool mismatchFirst)
    {
        await using var f = await Ready(postgres); var (request, incident) = await Reviewed(f); using var files = new Files();
        var next = Value(await Prepare(f, request, null, "Create")).InstallmentId!.Value;
        Value(await Prepare(f, request, next, "Submit")); Value(await Prepare(f, request, next, "Approve"));
        Value(await Call(f, request, incident, "Start", files));
        var observed = (await Actual(f, files, request)) with { Amount = "3100" };
        var incidentMeta = await Meta(f, request, incident); var startMeta = await Meta(f, request, next); var gate = new Barrier();
        Task<FinanceResult<PaymentView>> Incident() => Call(f, request, incident, "Mismatch", files, observed, incidentMeta,
            audit: db => gate.Audit(f, mismatchFirst, db));
        Task<FinanceResult<PaymentView>> Start() => Call(f, request, next, "Start", files, meta: startMeta,
            audit: db => gate.Audit(f, !mismatchFirst, db));
        var first = mismatchFirst ? Incident() : Start(); await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var second = mismatchFirst ? Start() : Incident();
        try { await Waiting(f, second); } finally { gate.Release.TrySetResult(); }
        Assert.Equal(200, (await first).Status); Assert.Equal(409, (await second).Status);
        if (!mismatchFirst) Value(await Call(f, request, incident, "Mismatch", files, observed));
        Assert.Equal(409, (await Call(f, request, next, "Start", files)).Status);
        await using var db = f.CreateContext();
        Assert.Single(await db.Set<AdvancePaymentHold>().Where(x => x.Code == "PaymentMismatch" && x.ReleasedAtUtc == null).ToArrayAsync());
        Assert.Equal(mismatchFirst ? 1 : 2, await db.Set<AdvancePaymentAttempt>().CountAsync());
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(); Assert.Equal(6000m, account.Reserved); Assert.Equal(0m, account.Paid);
        Assert.Empty(await db.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvanceReservationEntry>().Where(x => x.Action == "Consume" || x.Action == "Release").ToArrayAsync());
    }

    internal static async Task Waiting(FieldAdvanceFixture f, Task command)
    {
        await using var observer = f.CreateContext(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await observer.Database.SqlQueryRaw<bool>("SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.datname=current_database() AND l.locktype='advisory' AND l.objid=7241002 AND NOT l.granted) AS \"Value\"").SingleAsync(deadline.Token))
        { Assert.False(command.IsCompleted, "คำสั่งต้องรอ PostgreSQL lock จริง"); await Task.Yield(); }
    }

    internal sealed class Barrier
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IAuditEventWriter Audit(FieldAdvanceFixture f, bool block, Tpr10DbContext db)
            => new BarrierAudit(this, f, block, db);
        private sealed class BarrierAudit(Barrier gate, FieldAdvanceFixture f, bool block, Tpr10DbContext db) : IAuditEventWriter
        {
            public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null)
                => throw new InvalidOperationException("ใช้ structured audit เท่านั้น");
            public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
            {
                // เขียน audit จริงในธุรกรรมเดียวก่อนเปิด barrier
                await new AuditEventWriter(db, new CorrelationContext(), f.Clock).WriteAsync(request, ct);
                if (block) { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
            }
        }
    }

    internal static async Task<string> Snapshot(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        return JsonSerializer.Serialize(new
        {
            Families = await db.Set<AdvanceFamily>().OrderBy(x => x.Id).ToArrayAsync(),
            Requests = await db.Set<AdvanceRequest>().OrderBy(x => x.Id).ToArrayAsync(),
            Accounts = await db.Set<AdvancePaymentAccount>().OrderBy(x => x.Id).ToArrayAsync(),
            Installments = await db.Set<AdvanceInstallment>().OrderBy(x => x.Id).ToArrayAsync(),
            Attempts = await db.Set<AdvancePaymentAttempt>().OrderBy(x => x.Id).ToArrayAsync(),
            Results = await db.Set<AdvancePaymentResult>().OrderBy(x => x.Id).ToArrayAsync(),
            Entries = await db.Set<AdvanceReservationEntry>().OrderBy(x => x.Id).ToArrayAsync(),
            Participation = await db.Set<AdvancePaymentParticipation>().OrderBy(x => x.Id).ToArrayAsync(),
            Receipts = await db.Set<FinanceCommandReceipt>().OrderBy(x => x.CommandId).ToArrayAsync(),
            Events = await db.Set<AdvancePaymentEvent>().OrderBy(x => x.Id).ToArrayAsync(),
            Evidence = await db.Set<AdvanceEvidence>().OrderBy(x => x.Id).ToArrayAsync(),
            Locations = await db.Set<AdvanceEvidenceLocation>().OrderBy(x => x.Id).ToArrayAsync(),
            Audits = await db.AuditEvents.OrderBy(x => x.Id).ToArrayAsync(),
            AuditMetadata = await db.Database.SqlQueryRaw<string>("SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text), '[]')::text AS \"Value\" FROM audit_event_metadata t").SingleAsync()
        });
    }
    internal static async Task Unpaid(FieldAdvanceFixture f, Guid? request = null)
    {
        await using var db = f.CreateContext(); var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => request == null || x.RequestId == request);
        Assert.Equal(0m, account.Paid); Assert.Equal(3000m, account.Reserved);
        Assert.Empty(await db.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvanceReservationEntry>().Where(x => x.Action == "Consume").ToArrayAsync());
    }
    internal static PaymentView Value(FinanceResult<PaymentView> result)
    { Assert.Equal(200, result.Status); return Assert.IsType<PaymentView>(result.Value); }
    internal static async Task<FieldAdvanceFixture> Ready(PostgresFixture postgres)
    {
        var f = await CreateAsync(postgres); await f.SeedPoliciesAsync(); await Grant(f, RecipientId, RecipientPersonId, "payment.record");
        await using (var db = f.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"INSERT INTO users(id,username,normalized_username) VALUES ({Checker},'execution-checker','EXECUTION-CHECKER')");
            await db.Database.ExecuteSqlAsync($"INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) SELECT gen_random_uuid(),{Checker},workspace_id,department_id,valid_from_utc,created_by,'test checker' FROM employee_memberships WHERE user_id={RecipientId}");
            db.Add(new FinancePerson { Id = CheckerPerson, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "test checker" });
            db.Add(new FinanceAccountLink { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, UserId = Checker, PersonId = CheckerPerson, CreatedBy = AuthorId, CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1), Reason = "test checker" });
            await db.SaveChangesAsync();
        }
        await Grant(f, Checker, CheckerPerson, "finance.check"); return f;
    }
    internal static async Task Grant(FieldAdvanceFixture f, Guid actor, Guid person, string capability)
    {
        var id = await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person); await using var db = f.CreateContext();
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id);
        (await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId)).Capabilities = [capability]; await db.SaveChangesAsync();
    }
    internal static async Task Revoke(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.RecipientId == RecipientId));
        assignment.RevokedAtUtc = f.Clock.GetUtcNow(); assignment.RevokedBy = AuthorId; assignment.RevocationReason = "ถอนก่อนสำรองฐานทดสอบ";
        await db.SaveChangesAsync();
    }
    internal static async Task<PaymentCommand> Meta(FieldAdvanceFixture f, Guid request, Guid? id)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        return new(Guid.NewGuid(), (await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == r.FamilyId)).Version, r.Version,
            id is null ? null : (await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == id)).Version, "test execution");
    }
    internal static async Task<(Guid, Guid)> Reviewed(FieldAdvanceFixture f)
    {
        var request = await new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000m, default);
        var id = Value(await Prepare(f, request, null, "Create")).InstallmentId!.Value;
        Value(await Prepare(f, request, id, "Submit")); Value(await Prepare(f, request, id, "Approve")); return (request, id);
    }
    internal static async Task<FinanceResult<PaymentView>> Prepare(FieldAdvanceFixture f, Guid request, Guid? id, string operation)
    {
        var actor = operation == "Approve" ? Checker : RecipientId; var session = await f.SessionAsync(actor);
        await using var db = f.CreateContext(); var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var paymentAccess = new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock);
        var executor = new PaymentCommandExecutor(db, paymentAccess, new(db, session, access, people, f.Clock), new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var service = new InstallmentService(db, executor, new(db, f.Clock), f.Clock); var meta = await Meta(f, request, id);
        return operation switch
        {
            "Create" => await service.CreateAsync(actor, request, new("3000", new("Transfer", AuthorPersonId, "test-bank", "owner-account", "owner")), meta, default),
            "Submit" => await service.SubmitAsync(actor, id!.Value, meta, default),
            _ => await new InstallmentReviewService(db, executor, people, f.Clock).ReviewAsync(actor, id!.Value, "Approve", meta, default)
        };
    }
    internal static async Task<FinanceResult<PaymentView>> Call(FieldAdvanceFixture f, Guid request, Guid id, string operation, Files files,
        ActualPaymentInput? input = null, PaymentCommand? meta = null, Guid? actor = null, Func<Tpr10DbContext, IAuditEventWriter>? audit = null)
    {
        var user = actor ?? RecipientId; var session = await f.SessionAsync(user);
        await using var db = f.CreateContext();
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var paymentAccess = new PaymentAccess(db, access, people, new(db, access, people, f.Clock), f.Clock);
        var executor = new PaymentCommandExecutor(db, paymentAccess, new(db, session, access, people, f.Clock), audit?.Invoke(db) ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var evidence = new FinanceEvidenceService(db, paymentAccess, files.Store, new RejectPdf(), files.Options, f.Clock);
        var service = new PaymentExecutionService(db, executor, new(db, f.Clock), people, evidence, files.Options, f.Clock);
        meta ??= await Meta(f, request, id);
        if (operation == "Mismatch") return await new PaymentHoldService(db, executor, people, evidence, files.Options, f.Clock)
            .ReportMismatchAsync(user, id, input!, meta, default);
        return operation == "Start" ? await service.StartAsync(user, id, meta, default)
            : await service.RecordAsync(user, id, input!, meta, default);
    }
    internal static async Task<ActualPaymentInput> Actual(FieldAdvanceFixture f, Files files, Guid request)
    {
        var key = Guid.NewGuid().ToString("N"); var stored = await files.Store.WriteImmutableAsync(files.Options.ActiveStorageId, key, new MemoryStream([1, 2, 3]), default);
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request); var id = Guid.NewGuid();
        db.Add(new AdvanceEvidence
        {
            Id = id,
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            Sha256 = stored.Sha256,
            Length = stored.Length,
            ContentType = "image/png",
            State = "Ready",
            CreatedBy = RecipientId,
            CreatedByPersonId = RecipientPersonId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        db.Add(new AdvanceEvidenceLocation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = r.WorkspaceId,
            ProjectId = r.ProjectId,
            FamilyId = r.FamilyId,
            RequestId = request,
            EvidenceId = id,
            StorageKey = new EvidenceLocator(files.Options.ActiveStorageId, key).ToString(),
            IsCurrent = true,
            VerifiedAtUtc = f.Clock.GetUtcNow(),
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        await db.SaveChangesAsync(); return new("3000", AuthorPersonId, DateOnly.FromDateTime(f.Clock.GetUtcNow().UtcDateTime), "transfer-reference", [id]);
    }
    internal sealed class Files : IDisposable
    {
        internal readonly string root = Path.Combine(Path.GetTempPath(), "tpr10-execution-" + Guid.NewGuid().ToString("N"));
        public FinanceEvidenceOptions Options { get; }
        public FolderFinanceEvidenceStore Store { get; }
        public Files()
        {
            // The real store rejects symlink ancestors; canonicalize the macOS temporary directory.
            root = OperatingSystem.IsMacOS() && root.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + root : root;
            Directory.CreateDirectory(root); var id = Guid.NewGuid();
            Options = new() { ActiveStorageId = id, Roots = new() { [id] = root } }; Store = new(Options);
        }
        public void Remove() { foreach (var path in Directory.GetFiles(root)) File.Delete(path); }
        public void Dispose() => Directory.Delete(root, true);
    }
    internal sealed class RejectPdf : IFinancePdfValidator { public Task<bool> ValidateAsync(byte[] content, CancellationToken ct) => Task.FromResult(false); }
}
