using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementCommandTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Receipt_binds_payload_action_request_actor_and_versions_without_repeating_mutation()
    {
        await using var f = await Ready(postgres);
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        var request = await Request(f); var meta = await Meta(f, request);
        var initial = await Execute(f, AuthorId, request, SettlementActions.Draft, meta);
        Assert.Equal(200, initial.Status);
        Assert.Equal(meta.RequestVersion + 1, initial.Value!.RequestVersion);
        Assert.Equal(meta.FamilyVersion + 1, initial.Value.FamilyVersion);
        await using var db = f.CreateContext(); var executor = await Executor(f, db, AuthorId);
        var replay = await executor.ExecuteAsync(AuthorId, request, SettlementActions.Draft, meta, new('a', 64),
            _ => throw new InvalidOperationException("Replay must not execute mutation"), default);
        Assert.Equal(initial, replay);
        Assert.Equal(409, (await Execute(f, AuthorId, request, SettlementActions.Draft, meta, new('b', 64))).Status);
        Assert.Equal(409, (await Execute(f, AuthorId, request, SettlementActions.Declare, meta)).Status);
        Assert.Equal(409, (await Execute(f, AuthorId, await Request(f), SettlementActions.Draft, meta)).Status);
        Assert.Equal(409, (await Execute(f, AuthorId, request, SettlementActions.Draft, meta with { RequestVersion = 99 })).Status);
        Assert.Single(await db.Set<FinanceCommandReceipt>().ToArrayAsync());
        Assert.Single(await db.AuditEvents.Where(x => x.EventType.StartsWith("field-advance.settlement.")).ToArrayAsync());
    }

    [Theory]
    [InlineData("body-error")]
    [InlineData("audit-error")]
    [InlineData("commit-error")]
    [InlineData("final-revocation")]
    [InlineData("body-conflict")]
    public async Task Failed_command_rolls_back_saved_data_receipt_audit_participation_and_versions(string failure)
    {
        await using var f = await Ready(postgres);
        var grant = await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var request = await Request(f); var meta = await Meta(f, request);
        await using var db = f.CreateContext();
        if (failure == "commit-error")
        {
            // Deferred database failure fires at actual COMMIT, after SaveChanges and final authorization.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION task3_reject_commit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'task3 synthetic commit failure' USING ERRCODE='23514'; END $$;
                CREATE CONSTRAINT TRIGGER task3_commit_failure AFTER INSERT ON field_advance_command_receipts
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION task3_reject_commit();
                """);
        }
        IAuditEventWriter audit = new AuditEventWriter(db, new CorrelationContext(), f.Clock);
        if (failure == "audit-error") audit = new FieldAdvanceRequestRaceTests.CallbackAudit(audit,
            () => throw new InvalidOperationException("task3 audit failure"));
        var executor = await Executor(f, db, RecipientId, audit);
        async Task<FinanceResult<SettlementCommandResult>> Body(CancellationToken ct)
        {
            var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request, ct);
            var round = new SettlementRound
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RequestId = request,
                FamilyId = r.FamilyId,
                CommandId = meta.CommandId,
                ProposedBy = RecipientId,
                ProposedByPersonId = RecipientPersonId,
                CreatedAtUtc = f.Clock.GetUtcNow()
            };
            db.Add(round);
            db.Add(new SettlementRevision
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RequestId = request,
                FamilyId = r.FamilyId,
                RoundId = round.Id,
                RevisionNumber = 1,
                Total = 123.45m,
                CommandId = meta.CommandId,
                ProposedBy = RecipientId,
                ProposedByPersonId = RecipientPersonId,
                CreatedAtUtc = f.Clock.GetUtcNow()
            });
            await db.SaveChangesAsync(ct);
            if (failure == "body-error") throw new InvalidOperationException("task3 body failure");
            if (failure == "body-conflict") return new(409, "test-conflict", null);
            if (failure == "final-revocation")
            {
                var a = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == grant, ct);
                a.RevokedAtUtc = f.Clock.GetUtcNow(); a.RevokedBy = AuthorId; a.RevocationReason = "ถอนระหว่างคำสั่ง"; a.Version++;
                await db.SaveChangesAsync(ct);
            }
            return new(200, "ok", new(request, null, "Approved", meta.RequestVersion, meta.FamilyVersion, null));
        }
        async Task Run() => await executor.ExecuteAsync(RecipientId, request, SettlementActions.Close, meta, new('a', 64), Body, default);
        if (failure is "body-error" or "audit-error") await Assert.ThrowsAsync<InvalidOperationException>(Run);
        else if (failure == "commit-error") await Assert.ThrowsAsync<Npgsql.PostgresException>(Run);
        else
        {
            var result = await executor.ExecuteAsync(RecipientId, request, SettlementActions.Close, meta, new('a', 64), Body, default);
            Assert.Equal(failure == "final-revocation" ? 403 : 409, result.Status);
        }
        Assert.Empty(db.ChangeTracker.Entries());
        await using var verify = f.CreateContext();
        Assert.Empty(await verify.Set<SettlementRound>().ToArrayAsync());
        Assert.Empty(await verify.Set<SettlementRevision>().ToArrayAsync());
        Assert.Empty(await verify.Set<FinanceCommandReceipt>().ToArrayAsync());
        Assert.Empty(await verify.Set<AdvancePaymentParticipation>().ToArrayAsync());
        Assert.Empty(await verify.AuditEvents.Where(x => x.EventType.StartsWith("field-advance.settlement.")).ToArrayAsync());
        var after = await Meta(f, request);
        Assert.Equal(meta.RequestVersion, after.RequestVersion); Assert.Equal(meta.FamilyVersion, after.FamilyVersion);
    }

    [Fact]
    public async Task Existing_entity_requires_matching_version_and_cannot_review_own_proposal()
    {
        await using var f = await Ready(postgres);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var request = await Request(f);
        var completion = await Completion(f, request, AuthorId, AuthorPersonId);
        Assert.True((await Check(f, RecipientId, request, SettlementActions.CompletionReview, completion.Id)).Allowed);
        await using var db = f.CreateContext(); var executor = await Executor(f, db, RecipientId);
        var meta = await Meta(f, request, 999);
        var conflict = await executor.ExecuteAsync(RecipientId, request, SettlementActions.CompletionReview, completion.Id, meta, new('a', 64),
            _ => throw new InvalidOperationException("stale entity must not mutate"), default);
        Assert.Equal(409, conflict.Status);
        var own = await Completion(f, request, RecipientId, RecipientPersonId);
        Assert.False((await Check(f, RecipientId, request, SettlementActions.CompletionReview, own.Id)).Allowed);
        var otherRequest = await Request(f);
        Assert.False((await Check(f, RecipientId, otherRequest, SettlementActions.CompletionReview, completion.Id)).Allowed);
        Assert.False((await Check(f, RecipientId, request, SettlementActions.CompletionReview)).Allowed);
    }

    [Fact]
    public async Task Closed_request_allows_separate_case_review_but_not_original_draft_or_reopening()
    {
        await using var f = await Ready(postgres);
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var request = await Request(f); var completion = await Completion(f, request, AuthorId, AuthorPersonId);
        Guid caseId;
        await using (var db = f.CreateContext())
        {
            var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
            var declaration = await Row<SubmissionDeclaration>(f, db, r); declaration.WorkCompletionId = completion.Id;
            db.Add(declaration); await db.SaveChangesAsync();
            var closure = await Row<ClosureRecord>(f, db, r);
            closure.WorkCompletionId = completion.Id; closure.DeclarationId = declaration.Id; closure.RequestVersion = r.Version; closure.FamilyVersion = 1;
            closure.State = "Accepted"; closure.AcceptedAtUtc = f.Clock.GetUtcNow(); closure.ReviewedAtUtc = f.Clock.GetUtcNow();
            closure.ReviewedBy = ReviewerId; closure.ReviewedByPersonId = ReviewerPersonId;
            db.Add(closure); await db.SaveChangesAsync();
            var adjustment = await Row<SettlementAdjustmentCase>(f, db, r); adjustment.ClosureRecordId = closure.Id;
            db.Add(adjustment); r.State = "Closed"; await db.SaveChangesAsync(); caseId = adjustment.Id;
        }
        Assert.False((await Check(f, AuthorId, request, SettlementActions.Draft)).Allowed);
        Assert.True((await Check(f, RecipientId, request, SettlementActions.CaseReview, caseId)).Allowed);
        await using var context = f.CreateContext(); var executor = await Executor(f, context, RecipientId);
        var meta = await Meta(f, request, 1);
        var success = await executor.ExecuteAsync(RecipientId, request, SettlementActions.CaseReview, caseId, meta, new('a', 64),
            async ct =>
            {
                (await context.Set<SettlementAdjustmentCase>().SingleAsync(x => x.Id == caseId, ct)).Reason = "ตรวจเคสแยก";
                return new(200, "ok", new(request, caseId, "Draft", 0, 0, 1));
            }, default);
        Assert.Equal(200, success.Status);
        Assert.Equal(2, success.Value!.EntityVersion);
        Assert.Equal(meta.RequestVersion, success.Value.RequestVersion);
        Assert.Equal("Closed", (await context.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == request)).State);
        Assert.Single(await context.Set<ClosureRecord>().ToArrayAsync());
        var next = await Meta(f, request, 2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(RecipientId, request, SettlementActions.CaseReview, caseId, next, new('b', 64),
            async ct =>
            {
                (await context.Set<AdvanceRequest>().SingleAsync(x => x.Id == request, ct)).State = "Approved";
                await context.SaveChangesAsync(ct); return new(200, "ok", new(request, caseId, "Draft", 0, 0, 2));
            }, default));
        Assert.Equal("Closed", (await context.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == request)).State);
    }

    [Fact]
    public async Task Close_transition_rechecks_authority_and_replays_without_reopening_or_second_mutation()
    {
        await using var f = await Ready(postgres);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var request = await Request(f); var meta = await Meta(f, request);
        await using var db = f.CreateContext(); var executor = await Executor(f, db, RecipientId);
        var closed = await executor.ExecuteAsync(RecipientId, request, SettlementActions.Close, meta, new('a', 64),
            async ct =>
            {
                (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request, ct)).State = "Closed";
                return new(200, "ok", new(request, null, "Closed", 0, 0, null));
            }, default);
        Assert.Equal(200, closed.Status);
        var replay = await executor.ExecuteAsync(RecipientId, request, SettlementActions.Close, meta, new('a', 64),
            _ => throw new InvalidOperationException("No second close mutation"), default);
        Assert.Equal(closed, replay);
        var another = await executor.ExecuteAsync(RecipientId, request, SettlementActions.Close, await Meta(f, request), new('a', 64),
            _ => throw new InvalidOperationException("Closed original cannot mutate"), default);
        Assert.Equal(409, another.Status);
        Assert.Single(await db.Set<FinanceCommandReceipt>().ToArrayAsync());
    }

    [Fact]
    public async Task Same_person_second_account_cannot_replay_first_accounts_receipt()
    {
        await using var f = await Ready(postgres);
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await using (var db = f.CreateContext())
        {
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = AuthorPersonId,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1),
                Reason = "บัญชีที่สอง"
            });
            await db.SaveChangesAsync();
        }
        await Grant(f, AliasAccountId, AuthorPersonId, "request.create");
        var request = await Request(f); var meta = await Meta(f, request);
        Assert.Equal(200, (await Execute(f, AuthorId, request, SettlementActions.Draft, meta)).Status);
        Assert.Equal(409, (await Execute(f, AliasAccountId, request, SettlementActions.Draft, meta)).Status);
        await using var verify = f.CreateContext();
        Assert.Single(await verify.Set<FinanceCommandReceipt>().ToArrayAsync());
    }

    [Fact]
    public async Task Competing_commands_wait_for_database_lock_and_replay_one_result()
    {
        await using var f = await Ready(postgres);
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        var request = await Request(f); var meta = await Meta(f, request);
        await using var firstDb = f.CreateContext(); await using var secondDb = f.CreateContext();
        var first = await Executor(f, firstDb, AuthorId); var second = await Executor(f, secondDb, AuthorId);
        await secondDb.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)secondDb.Database.GetDbConnection()).ProcessID;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = first.ExecuteAsync(AuthorId, request, SettlementActions.Draft, meta, new('a', 64), async ct =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return new(200, "ok", new(request, null, "Approved", 0, 0, null));
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var secondTask = second.ExecuteAsync(AuthorId, request, SettlementActions.Draft, meta, new('a', 64),
            _ => throw new InvalidOperationException("Competing replay must not mutate twice"), default);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var probe = f.CreateContext();
            while (await probe.Database.SqlQuery<int>($"SELECT cardinality(pg_blocking_pids({pid})) AS \"Value\"").SingleAsync(deadline.Token) == 0)
            {
                Assert.False(secondTask.IsCompleted); await Task.Delay(20, deadline.Token);
            }
        }
        finally { release.TrySetResult(); }
        var result = await firstTask; Assert.Equal(200, result.Status);
        Assert.Equal(result, await secondTask.WaitAsync(TimeSpan.FromSeconds(15)));
        await using var verify = f.CreateContext();
        Assert.Single(await verify.Set<FinanceCommandReceipt>().ToArrayAsync());
        Assert.Single(await verify.AuditEvents.Where(x => x.EventType.StartsWith("field-advance.settlement.")).ToArrayAsync());
    }

    [Fact]
    public async Task Foreign_entity_is_denied_without_waiting_for_another_familys_entity_lock()
    {
        await using var f = await Ready(postgres);
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var request = await Request(f); var other = await Request(f);
        var entity = await Completion(f, other, AuthorId, AuthorPersonId);
        await using var holder = f.CreateContext(); await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM field_advance_work_completions WHERE id={entity.Id} FOR UPDATE");
        var pending = Check(f, RecipientId, request, SettlementActions.CompletionReview, entity.Id);
        bool completed;
        try { completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(3))) == pending; }
        finally { await tx.RollbackAsync(); }
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(completed, "Foreign entity lookup must filter scope before taking its row lock");
        Assert.False(result.Allowed);
    }

    internal static async Task<T> Row<T>(FieldAdvanceFixture f, Tpr10DbContext db, AdvanceRequest r,
        Guid? actor = null, Guid? person = null) where T : class, new()
    {
        var receipt = FieldAdvancePaymentFixture.Receipt(); receipt.ActorId = actor ?? AuthorId;
        db.Add(receipt); await db.SaveChangesAsync();
        var row = new T(); var e = db.Entry(row);
        e.Property("Id").CurrentValue = Guid.NewGuid(); e.Property("WorkspaceId").CurrentValue = WorkspaceId;
        e.Property("ProjectId").CurrentValue = ProjectId; e.Property("FamilyId").CurrentValue = r.FamilyId;
        e.Property("RequestId").CurrentValue = r.Id; e.Property("CommandId").CurrentValue = receipt.CommandId;
        e.Property("ProposedBy").CurrentValue = actor ?? AuthorId; e.Property("ProposedByPersonId").CurrentValue = person ?? AuthorPersonId;
        e.Property("CreatedAtUtc").CurrentValue = f.Clock.GetUtcNow(); return row;
    }
    internal static async Task<WorkCompletion> Completion(FieldAdvanceFixture f, Guid request, Guid actor, Guid person)
    {
        await using var db = f.CreateContext(); var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var row = await Row<WorkCompletion>(f, db, r, actor, person);
        row.CompletedOn = new(2026, 10, 20); row.ClearingDueOn = row.CompletedOn; row.RefundDueOn = row.CompletedOn;
        db.Add(row); await db.SaveChangesAsync(); return row;
    }
}
