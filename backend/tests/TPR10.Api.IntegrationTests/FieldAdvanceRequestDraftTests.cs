using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Identity.Sessions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRequestDraftTests(PostgresFixture postgres)
{
    // Catches missing registration/implementation before any production draft code exists.
    [Fact]
    public void Draft_service_is_registered()
    {
        var services = new ServiceCollection().AddFieldAdvances();
        Assert.Contains(services, x => x.ServiceType.FullName == "TPR10.Api.FieldAdvances.Requests.RequestDraftService"
            && x.Lifetime == ServiceLifetime.Scoped);
    }

    // Catches account IDs substituted for person IDs, missing history, or proxy owner session checks.
    [Fact]
    public async Task Self_and_delegate_draft_keep_actor_history()
    {
        await using var f = await Ready();
        var input = Input();
        var proxy = await Create(f, input, actor: RecipientId);
        var self = await Create(f, input);
        Assert.Equal(201, self.Status);
        Assert.Equal(201, proxy.Status);
        Assert.Null(self.Value!.DelegatePersonId);
        Assert.Equal(RecipientPersonId, proxy.Value!.DelegatePersonId);
        Assert.Equal(AuthorPersonId, proxy.Value.OwnerPersonId);
        Assert.Equal(123.45m, proxy.Value.Estimate!.Total);
        await using var db = f.CreateContext();
        var row = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == proxy.Value.Id);
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.RequestId == row.Id);
        Assert.Equal(RecipientId, row.CreatedBy);
        Assert.Equal(RecipientId, revision.CreatedBy);
        Assert.Equal(RecipientPersonId, revision.CreatedByPersonId);
        Assert.Null(revision.SubmittedAtUtc);
        Assert.Null(revision.PolicySnapshotJson);
        var history = await db.Set<AdvanceRequestEvent>().SingleAsync(x => x.RequestId == row.Id);
        Assert.Equal(RecipientId, history.ActorId);
        Assert.Equal(RecipientPersonId, history.ActorPersonId);
        Assert.Equal(1, history.AggregateVersion);
        var participants = await db.Set<AdvanceRequestParticipant>().Where(x => x.RequestId == row.Id).ToArrayAsync();
        Assert.Contains(participants, x => x.PersonId == AuthorPersonId && x.Role == "Owner");
        Assert.Contains(participants, x => x.PersonId == RecipientPersonId && x.Role == "Delegate");
        Assert.Equal(2, participants.Length);
        Assert.Empty(await db.Set<AdvanceApprovalStep>().ToArrayAsync());
    }

    [Fact]
    public async Task Duplicate_command_returns_one_request()
    {
        await using var f = await Ready();
        var input = Input(); var meta = Meta();
        var first = await Create(f, input, meta);
        Assert.Equal(201, first.Status);
        await using var db = f.CreateContext();
        var before = await Snapshot(db);
        var retry = await Create(f, input, meta);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(retry));
        Assert.Equal(before, await Snapshot(db));
        Assert.Single(await db.Set<AdvanceRequest>().ToArrayAsync());
        Assert.Single(await db.Set<AdvanceRequestEvent>().ToArrayAsync());
    }

    [Fact]
    public async Task Same_command_different_payload_conflicts()
    {
        await using var f = await Ready();
        var input = Input(); var meta = Meta();
        Assert.Equal(201, (await Create(f, input, meta)).Status);
        foreach (var changed in new[]
        {
            input with { Content = input.Content with { Purpose = "เปลี่ยนงาน" } },
            input with { Content = input.Content with { Kind = "Team", TeamPersonIds = [RecipientPersonId] } },
            input with { Content = input.Content with { FieldStart = input.Content.FieldStart.AddDays(1) } },
            input with { Content = input.Content with { FieldEnd = input.Content.FieldEnd.AddDays(1) } },
            input with { Content = input.Content with { NeededOn = input.Content.NeededOn.AddDays(1) } },
            input with { Content = input.Content with { Lines = [input.Content.Lines[0] with { Id = Guid.NewGuid() }] } },
            input with { Content = input.Content with { Lines = [input.Content.Lines[0] with { CategoryId = Guid.NewGuid() }] } },
            input with { Content = input.Content with { Lines = [input.Content.Lines[0] with { Description = "ใหม่" }] } },
            input with { Content = input.Content with { Lines = [input.Content.Lines[0] with { Mode = "LumpSum" }] } },
            input with { Content = input.Content with { Lines = [input.Content.Lines[0] with { Quantity = "2" }] } },
            input with { Content = input.Content with { Lines = [input.Content.Lines[0] with { UnitRate = "100" }] } },
            input with { Content = input.Content with { Lines = [input.Content.Lines[0] with { LumpSum = "123.45" }] } },
            input with { OwnerPersonId = RecipientPersonId }
        }) Assert.Equal(409, (await Create(f, changed, meta)).Status);
        Assert.Equal(409, (await Create(f, input, meta with { Reason = "เหตุผลต่าง" })).Status);
        Assert.Equal(409, (await Create(f, input, meta with { ExpectedVersion = 1 })).Status);
        Assert.Equal(409, (await Create(f, input, meta, RecipientId)).Status);
        var sibling = Guid.Parse("71000000-0000-0000-0000-000000000012");
        var grant = await FieldAdvanceGrantTests.AuthorityAsync(f, AuthorId, AuthorPersonId, projectId: sibling);
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => db.Set<GrantAssignment>().Any(a => a.Id == grant && a.ProposalId == x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.create" }));
        Assert.Equal(409, (await Create(f, input with { Scope = new(WorkspaceId, sibling) }, meta)).Status);
        Assert.Single(await db.Set<AdvanceRequest>().ToArrayAsync());
    }

    // Catches partial commits and tracked entities contaminating a retry on the same DbContext.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rollback_removes_request_event_and_receipt(bool atCommit)
    {
        await using var f = await Ready();
        var session = await f.SessionAsync(AuthorId);
        await using var db = f.CreateContext();
        var before = await Snapshot(db);
        await db.Database.ExecuteSqlRawAsync(atCommit ? """
            CREATE FUNCTION fail_draft() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test-draft-fault'; END $$;
            CREATE CONSTRAINT TRIGGER fail_draft AFTER INSERT ON field_advance_command_receipts
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_draft();
            """ : """
            CREATE FUNCTION fail_draft() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test-draft-fault'; END $$;
            CREATE TRIGGER fail_draft BEFORE INSERT ON field_advance_request_events
            FOR EACH ROW EXECUTE FUNCTION fail_draft();
            """);
        var input = Input(); var meta = Meta();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Service(f, db, session).CreateAsync(AuthorId, input, meta, default));
        while (error.InnerException is not null) error = error.InnerException;
        Assert.Equal("P0001", Assert.IsType<Npgsql.PostgresException>(error).SqlState);
        Assert.Empty(db.ChangeTracker.Entries());
        await db.SaveChangesAsync();
        Assert.Equal(before, await Snapshot(db));
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION fail_draft() CASCADE");
        Assert.Equal(201, (await Service(f, db, session).CreateAsync(AuthorId, input, meta, default)).Status);
        Assert.Single(await db.Set<AdvanceRequest>().ToArrayAsync());
    }

    [Fact]
    public async Task Update_increments_version_preserves_identity_and_replays_before_stale_version_check()
    {
        await using var f = await Ready();
        var created = await Create(f, Input(), actor: RecipientId);
        Assert.Equal(201, created.Status);
        var first = created.Value!;
        var content = first.Content with { Purpose = "ปรับงาน", Lines = [first.Content.Lines[0] with { Quantity = "2" }] };
        var meta = Meta(1);
        var edited = await Update(f, first.Id, content, meta, RecipientId);
        Assert.Equal(200, edited.Status);
        Assert.Equal(2, edited.Value!.Version);
        Assert.Equal(200m, edited.Value.Estimate!.Total);
        Assert.Equal(first.RevisionId, edited.Value.RevisionId);
        Assert.Equal(first.Scope, edited.Value.Scope);
        Assert.Equal(first.OwnerPersonId, edited.Value.OwnerPersonId);
        Assert.Equal(first.DelegatePersonId, edited.Value.DelegatePersonId);
        Assert.Equal(JsonSerializer.Serialize(edited), JsonSerializer.Serialize(await Update(f, first.Id, content, meta, RecipientId)));
        Assert.Equal(409, (await Update(f, first.Id, first.Content, Meta(1), RecipientId)).Status);
        Assert.Equal(403, (await Update(f, first.Id, content, Meta(2), AuthorId)).Status);
        await using var db = f.CreateContext();
        Assert.Equal(2, await db.Set<AdvanceRequestEvent>().CountAsync());
        Assert.Equal(2, await db.Set<AdvanceRequestParticipant>().CountAsync());
        Assert.Single(await db.Set<AdvanceRequestLine>().ToArrayAsync());
    }

    [Fact]
    public async Task Concurrent_create_and_update_have_one_effect()
    {
        await using var f = await Ready();
        var session = await f.SessionAsync(AuthorId);
        await using var a = f.CreateContext(); await using var b = f.CreateContext();
        var input = Input(); var meta = Meta();
        var creates = await Task.WhenAll(
            Service(f, a, session).CreateAsync(AuthorId, input, meta, default),
            Service(f, b, session).CreateAsync(AuthorId, input, meta, default));
        Assert.All(creates, x => Assert.Equal(201, x.Status));
        Assert.Equal(creates[0].Value!.Id, creates[1].Value!.Id);
        var id = creates[0].Value!.Id;
        var updates = await Task.WhenAll(
            Service(f, a, session).UpdateAsync(AuthorId, id, input.Content with { Purpose = "หนึ่ง" }, Meta(1), default),
            Service(f, b, session).UpdateAsync(AuthorId, id, input.Content with { Purpose = "สอง" }, Meta(1), default));
        Assert.Equal(new[] { 200, 409 }, updates.Select(x => x.Status).Order().ToArray());
        await using var check = f.CreateContext();
        Assert.Single(await check.Set<AdvanceRequest>().ToArrayAsync());
        Assert.Equal(2, await check.Set<AdvanceRequestEvent>().CountAsync());
    }

    [Fact]
    public async Task Concurrent_duplicate_update_has_one_receipt_and_changed_update_inputs_conflict()
    {
        await using var f = await Ready();
        var input = Input(); var createMeta = Meta();
        var first = await Create(f, input, createMeta);
        Assert.Equal(201, first.Status);
        var session = await f.SessionAsync(AuthorId);
        await using var a = f.CreateContext(); await using var b = f.CreateContext();
        var meta = Meta(1); var id = first.Value!.Id;
        var results = await Task.WhenAll(
            Service(f, a, session).UpdateAsync(AuthorId, id, input.Content, meta, default),
            Service(f, b, session).UpdateAsync(AuthorId, id, input.Content, meta, default));
        Assert.All(results, x => Assert.Equal(200, x.Status));
        Assert.Equal(JsonSerializer.Serialize(results[0]), JsonSerializer.Serialize(results[1]));
        Assert.Equal(409, (await Update(f, id, input.Content with { Purpose = "ใหม่" }, meta)).Status);
        Assert.Equal(409, (await Update(f, id, input.Content, meta with { ExpectedVersion = 2 })).Status);
        Assert.Equal(409, (await Update(f, id, input.Content, meta with { Reason = "ใหม่" })).Status);
        Assert.Equal(409, (await Update(f, id, input.Content, createMeta)).Status);
        var second = await Create(f, input);
        Assert.Equal(201, second.Status);
        Assert.Equal(409, (await Update(f, second.Value!.Id, input.Content, meta)).Status);
        Assert.Single(await a.Set<FinanceCommandReceipt>().Where(x => x.CommandId == meta.CommandId).ToArrayAsync());
        Assert.Equal(2, await a.Set<AdvanceRequestEvent>().CountAsync(x => x.RequestId == id));
    }

    [Fact]
    public async Task Submitted_revision_cannot_be_overwritten_even_if_aggregate_says_draft()
    {
        await using var f = await Ready();
        var first = await Create(f, Input());
        Assert.Equal(201, first.Status);
        await using var db = f.CreateContext();
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync();
        revision.CompanyPolicyRevisionId = (await db.Set<ActivePolicy>().SingleAsync()).PolicyRevisionId;
        revision.PolicySnapshotJson = "{}"; revision.PolicyHash = new('a', 64);
        revision.SubmittedAtUtc = f.Clock.GetUtcNow(); revision.ConfirmedByPersonId = AuthorPersonId;
        await db.SaveChangesAsync();
        var before = await Snapshot(db);
        Assert.Equal(409, (await Update(f, first.Value!.Id, first.Value.Content, Meta(1))).Status);
        Assert.Equal(before, await Snapshot(db));
    }

    [Fact]
    public async Task Team_draft_and_reordered_lines_preserve_content_and_server_totals()
    {
        await using var f = await Ready();
        var input = Input();
        var one = input.Content.Lines[0];
        var two = one with { Id = Guid.NewGuid(), Quantity = "2" };
        input = input with { Content = input.Content with { Kind = "Team", TeamPersonIds = [AuthorPersonId, RecipientPersonId], Lines = [one, two] } };
        var first = await Create(f, input);
        Assert.Equal(201, first.Status);
        Assert.Equal(323.45m, first.Value!.Estimate!.Total);
        var result = await Update(f, first.Value.Id, input.Content with { Lines = [two, one] }, Meta(1));
        Assert.Equal(200, result.Status);
        Assert.Equal(new[] { two.Id, one.Id }, result.Value!.Estimate!.Lines.Select(x => x.Id).ToArray());
        await using var db = f.CreateContext();
        var lines = await db.Set<AdvanceRequestLine>().OrderBy(x => x.LineOrder).ToArrayAsync();
        Assert.Equal(new[] { 200m, 123.45m }, lines.Select(x => x.Amount).ToArray());
        Assert.Single(await db.Set<AdvanceRequestParticipant>().ToArrayAsync());
    }

    [Fact]
    public async Task Revoked_after_success_retry_returns_no_data()
    {
        await using var f = await Ready();
        var input = Input(); var meta = Meta();
        var first = await Create(f, input, meta);
        Assert.Equal(201, first.Status);
        var updateMeta = Meta(1);
        Assert.Equal(200, (await Update(f, first.Value!.Id, input.Content, updateMeta)).Status);
        await using var db = f.CreateContext();
        var before = await Snapshot(db);
        await Revoke(db, f, AuthorId);
        var retry = await Create(f, input, meta);
        Assert.Equal(403, retry.Status); Assert.Null(retry.Value);
        var updateRetry = await Update(f, first.Value.Id, input.Content, updateMeta);
        Assert.Equal(403, updateRetry.Status); Assert.Null(updateRetry.Value);
        Assert.Equal(before, await Snapshot(db));
    }

    [Theory]
    [InlineData("owner-revoked")]
    [InlineData("owner-expired")]
    [InlineData("owner-company-only")]
    [InlineData("owner-disabled")]
    [InlineData("owner-unlinked")]
    [InlineData("actor-no-delegate")]
    [InlineData("other-project")]
    public async Task Proxy_requires_current_exact_project_owner_eligibility(string defect)
    {
        await using var f = await Ready();
        await using var db = f.CreateContext();
        var input = Input();
        if (defect == "owner-revoked") await Revoke(db, f, AuthorId);
        if (defect == "owner-expired")
            await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow()));
        if (defect == "owner-company-only")
        {
            await Revoke(db, f, AuthorId);
            await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == null)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.create" }));
        }
        if (defect == "owner-disabled")
            await db.Set<TPR10.Api.Identity.Data.IdentityUser>().Where(x => x.Id == AuthorId).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsActive, false));
        if (defect == "owner-unlinked")
            await db.Set<FinanceAccountLink>().Where(x => x.UserId == AuthorId).ExecuteUpdateAsync(x => x
                .SetProperty(p => p.EndedAtUtc, f.Clock.GetUtcNow()).SetProperty(p => p.EndedBy, ReviewerId)
                .SetProperty(p => p.EndReason, "สิ้นสุดเพื่อทดสอบ").SetProperty(p => p.Version, p => p.Version + 1));
        if (defect == "actor-no-delegate") await Revoke(db, f, RecipientId);
        if (defect == "other-project") input = input with { Scope = new(WorkspaceId, Guid.Parse("71000000-0000-0000-0000-000000000012")) };
        var result = await Create(f, input, actor: RecipientId);
        Assert.Equal(403, result.Status); Assert.Null(result.Value);
        Assert.Empty(await db.Set<AdvanceRequest>().ToArrayAsync());
    }

    [Theory]
    [InlineData("dates")]
    [InlineData("kind")]
    [InlineData("team")]
    [InlineData("precision")]
    [InlineData("line-id")]
    [InlineData("purpose")]
    public async Task Invalid_content_is_rejected_without_writes(string defect)
    {
        await using var f = await Ready();
        var input = Input(); var c = input.Content;
        c = defect switch
        {
            "dates" => c with { FieldEnd = c.FieldStart.AddDays(-1) },
            "kind" => c with { Kind = "Other" },
            "team" => c with { Kind = "Team", TeamPersonIds = [AuthorPersonId, AuthorPersonId] },
            "precision" => c with { Lines = [c.Lines[0] with { Quantity = "1.00000" }] },
            "line-id" => c with { Lines = [c.Lines[0], c.Lines[0]] },
            _ => c with { Purpose = " " }
        };
        Assert.Equal(400, (await Create(f, input with { Content = c })).Status);
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<AdvanceRequest>().ToArrayAsync());
    }

    [Theory]
    [InlineData("AwaitingOwner")]
    [InlineData("PendingApproval")]
    [InlineData("Held")]
    [InlineData("Returned")]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    [InlineData("Withdrawn")]
    public async Task Non_draft_is_not_directly_editable(string state)
    {
        await using var f = await Ready();
        var first = await Create(f, Input());
        Assert.Equal(201, first.Status);
        await using var db = f.CreateContext();
        await db.Set<AdvanceRequest>().Where(x => x.Id == first.Value!.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.State, state));
        var before = await Snapshot(db);
        var result = await Update(f, first.Value!.Id, first.Value.Content, Meta(1));
        Assert.NotEqual(200, result.Status); Assert.Null(result.Value);
        Assert.Equal(before, await Snapshot(db));
    }

    // A command taking the row first, or authorizing before the advisory lock, breaks this test.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_waits_for_identity_lock_before_row_lock_and_checks_committed_revocation(bool update)
    {
        await using var f = await Ready();
        var input = Input(); var createMeta = Meta(); var updateMeta = Meta(1);
        var first = await Create(f, input, createMeta);
        Assert.Equal(201, first.Status);
        if (update) Assert.Equal(200, (await Update(f, first.Value!.Id, input.Content, updateMeta)).Status);
        var session = await f.SessionAsync(AuthorId);
        await using var revoke = f.CreateContext();
        var before = await Snapshot(revoke);
        await using var tx = await revoke.Database.BeginTransactionAsync();
        await revoke.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        await using var command = f.CreateContext();
        var waiting = update
            ? Service(f, command, session).UpdateAsync(AuthorId, first.Value!.Id, input.Content, updateMeta, default)
            : Service(f, command, session).CreateAsync(AuthorId, input, createMeta, default);
        await using var probe = f.CreateContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!await probe.Database.SqlQueryRaw<bool>("""
                SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=7241002 AND NOT granted) AS "Value"
                """).SingleAsync(deadline.Token))
            {
                Assert.False(waiting.IsCompleted);
                await Task.Delay(20, deadline.Token);
            }
            await revoke.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM field_advance_requests WHERE id={first.Value!.Id} FOR UPDATE NOWAIT");
            await Revoke(revoke, f, AuthorId);
        }
        finally { await tx.CommitAsync(); }
        var result = await waiting;
        Assert.Equal(403, result.Status); Assert.Null(result.Value);
        Assert.Equal(before, await Snapshot(probe));
    }

    [Fact]
    public async Task Update_failure_restores_lines_version_and_history_and_same_context_can_retry()
    {
        await using var f = await Ready();
        var first = await Create(f, Input());
        Assert.Equal(201, first.Status);
        var session = await f.SessionAsync(AuthorId);
        await using var db = f.CreateContext();
        var before = await Snapshot(db);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_draft() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test-update-fault'; END $$;
            CREATE CONSTRAINT TRIGGER fail_draft AFTER INSERT ON field_advance_command_receipts
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_draft();
            """);
        var content = first.Value!.Content with { Lines = [first.Value.Content.Lines[0] with { Quantity = "2" }] };
        var meta = Meta(1);
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Service(f, db, session).UpdateAsync(AuthorId, first.Value.Id, content, meta, default));
        Assert.Equal("P0001", error.SqlState);
        Assert.Empty(db.ChangeTracker.Entries());
        await db.SaveChangesAsync();
        Assert.Equal(before, await Snapshot(db));
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION fail_draft() CASCADE");
        Assert.Equal(200, (await Service(f, db, session).UpdateAsync(AuthorId, first.Value.Id, content, meta, default)).Status);
        Assert.Equal(200m, (await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleAsync()).Total);
        Assert.Equal(2, await db.Set<AdvanceRequestEvent>().CountAsync());
    }

    [Fact]
    public async Task Alias_of_owner_remains_self_and_original_creator_is_retained_on_edit()
    {
        await using var f = await Ready();
        await using var db = f.CreateContext();
        db.Add(new FinanceAccountLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = AliasAccountId,
            PersonId = AuthorPersonId,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow(),
            Reason = "บัญชีอื่นบุคคลเดิม"
        });
        await db.SaveChangesAsync();
        var grant = await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, AuthorPersonId);
        await db.Set<GrantProposal>().Where(x => db.Set<GrantAssignment>().Any(a => a.Id == grant && a.ProposalId == x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.create" }));
        var first = await Create(f, Input(), actor: AliasAccountId);
        Assert.Equal(201, first.Status); Assert.Null(first.Value!.DelegatePersonId);
        Assert.Equal(200, (await Update(f, first.Value.Id, first.Value.Content, Meta(1))).Status);
        Assert.Equal(AliasAccountId, (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync()).CreatedBy);
        var revision = await db.Set<AdvanceRequestRevision>().AsNoTracking().SingleAsync();
        Assert.Equal(AliasAccountId, revision.CreatedBy); Assert.Equal(AuthorPersonId, revision.CreatedByPersonId);
        var events = await db.Set<AdvanceRequestEvent>().AsNoTracking().OrderBy(x => x.AggregateVersion).ToArrayAsync();
        Assert.Equal(new[] { AliasAccountId, AuthorId }, events.Select(x => x.ActorId).ToArray());
        Assert.All(events, x => Assert.Equal(AuthorPersonId, x.ActorPersonId));
        Assert.Single(await db.Set<AdvanceRequestParticipant>().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_during_audit_rolls_back_all_draft_writes(bool update)
    {
        await using var f = await Ready();
        var input = Input();
        var first = update ? (await Create(f, input)).Value! : null;
        var session = await f.SessionAsync(AuthorId);
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.ProjectId == ProjectId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow().AddMinutes(1)));
        var before = await Snapshot(db);
        var writer = new ExpiringAudit(new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var service = Service(f, db, session, writer);
        var result = update
            ? await service.UpdateAsync(AuthorId, first!.Id, input.Content, Meta(1), default)
            : await service.CreateAsync(AuthorId, input, Meta(), default);
        Assert.Equal(403, result.Status); Assert.Null(result.Value);
        Assert.Empty(db.ChangeTracker.Entries());
        await db.SaveChangesAsync();
        Assert.Equal(before, await Snapshot(db));
    }

    private sealed class ExpiringAudit(IAuditEventWriter inner, ManualTimeProvider clock) : IAuditEventWriter
    {
        public Task WriteAsync(string eventType, Guid targetId, IReadOnlyDictionary<string, string> metadata, CancellationToken ct, Guid? actorId = null) =>
            inner.WriteAsync(eventType, targetId, metadata, ct, actorId);
        public async Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)
        {
            await inner.WriteAsync(request, ct);
            clock.Advance(TimeSpan.FromMinutes(2));
        }
    }

    private async Task<FieldAdvanceFixture> Ready()
    {
        var f = await P.Ready(postgres);
        await P.Review(f, await P.Submit(f, await P.Create(f)));
        await using var db = f.CreateContext();
        foreach (var row in await db.Set<GrantProposal>().Where(x => x.ProjectId == ProjectId).ToArrayAsync())
            row.Capabilities = ["request.create", "request.delegate"];
        await db.SaveChangesAsync();
        // Owner eligibility must not depend on any live owner's session.
        await db.Set<TPR10.Api.Identity.Data.IdentitySession>().ExecuteDeleteAsync();
        return f;
    }
    private static CreateRequest Input() => new(new(WorkspaceId, ProjectId), AuthorPersonId,
        new("Individual", "สำรวจพื้นที่", new(2026, 10, 24), new(2026, 10, 25), new(2026, 10, 23), [],
            [new(Guid.NewGuid(), Guid.Parse("78000000-0000-0000-0000-000000000001"), "ค่าเดินทาง", "QuantityRate", "1.2345", null, null)]));
    private static RequestCommand Meta(long version = 0) => new(Guid.NewGuid(), version, "ทดสอบร่าง");
    private static async Task<FinanceResult<RequestView>> Create(FieldAdvanceFixture f, CreateRequest input, RequestCommand? meta = null, Guid? actor = null)
    {
        var who = actor ?? AuthorId; var session = await f.SessionAsync(who);
        await using var db = f.CreateContext();
        return await Service(f, db, session).CreateAsync(who, input, meta ?? Meta(), default);
    }
    private static async Task<FinanceResult<RequestView>> Update(FieldAdvanceFixture f, Guid id, RequestContent content, RequestCommand meta, Guid? actor = null)
    {
        var who = actor ?? AuthorId; var session = await f.SessionAsync(who);
        await using var db = f.CreateContext();
        return await Service(f, db, session).UpdateAsync(who, id, content, meta, default);
    }
    private static RequestDraftService Service(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession session, IAuditEventWriter? audit = null)
    {
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var people = new FinancePersonResolver(db, f.Clock);
        return new(db, access, people, new RequestAccess(db, access, people, f.Clock),
            new PolicyResolver(db, access, session, f.Clock), audit ?? new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
    }
    private static async Task<string> Snapshot(Tpr10DbContext db) => JsonSerializer.Serialize(new
    {
        requests = await db.Set<AdvanceRequest>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        revisions = await db.Set<AdvanceRequestRevision>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        lines = await db.Set<AdvanceRequestLine>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        events = await db.Set<AdvanceRequestEvent>().CountAsync(),
        participants = await db.Set<AdvanceRequestParticipant>().CountAsync(),
        receipts = await db.Set<FinanceCommandReceipt>().CountAsync(),
        audit = await db.AuditEvents.CountAsync(),
        metadata = await db.AuditMetadata.CountAsync()
    });
    private static async Task Revoke(Tpr10DbContext db, FieldAdvanceFixture f, Guid actor)
    {
        var grants = await (from a in db.Set<GrantAssignment>()
                            join p in db.Set<GrantProposal>() on a.ProposalId equals p.Id
                            where p.RecipientId == actor && p.ProjectId == ProjectId
                            select a).ToArrayAsync();
        foreach (var grant in grants)
        {
            grant.RevokedAtUtc = f.Clock.GetUtcNow(); grant.RevokedBy = ReviewerId;
            grant.RevocationReason = "ถอนเพื่อทดสอบ"; grant.Version++;
        }
        await db.SaveChangesAsync();
    }
}
