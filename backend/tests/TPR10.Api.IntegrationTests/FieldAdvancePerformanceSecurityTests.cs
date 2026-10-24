using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Grants;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Identity.Sessions;
using TPR10.Api.Organization;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using G = TPR10.Api.IntegrationTests.FieldAdvanceGrantTests;

namespace TPR10.Api.IntegrationTests;

public sealed partial class FieldAdvancePerformanceTests
{
    private static readonly string[] DiscoveryCapabilities = ["grant.propose", "grant.review", "grant.revoke", "policy.edit.project", "policy.review", "request.create", "request.delegate", "request.approve", "report.read"];

    private static GrantReadService GrantReader(FieldAdvanceFixture f, Tpr10DbContext db, RequestSession session) =>
        new(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), G.Service(f, db, session), new(db, f.Clock),
            new(db, session, new EffectiveRolePolicy(db), f.Clock), session, f.Clock);

    private static async Task<FinanceResult<GrantScopeChoice[]>> Discover(FieldAdvanceFixture f, Tpr10DbContext db,
        RequestSession session, string kind, int page = 1, int size = 100)
    {
        if (kind == "grant") return await GrantReader(f, db, session).ScopesAsync(page, size, default);
        if (kind == "policy")
        {
            var result = await new PolicyWorkspaceService(db, FieldAdvanceAccessTests.Access(db, session, f.Clock),
                new ScopeAccess(db, session, new EffectiveRolePolicy(db), f.Clock), session).ScopesAsync(page, size, default);
            return new(result.Status, result.Code, result.Value?.Select(x => new GrantScopeChoice(x.Scope, x.WorkspaceName, x.ProjectName, x.Capabilities)).ToArray());
        }
        var requests = await RequestReader(f, db, session).ScopesAsync(AuthorId, page, size, default);
        return new(requests.Status, requests.Code, requests.Value?.Select(x => new GrantScopeChoice(x.Scope, x.WorkspaceName, x.ProjectName, x.Capabilities)).ToArray());
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("policy")]
    [InlineData("request")]
    public async Task Scope_paging_skips_denied_candidates_across_batch_boundary(string kind)
    {
        await using var f = await G.ReadyAsync(postgres);
        // 101 revoked scopes precede the two allowed scopes. A company grant must not authorize them.
        for (var i = 0; i < 102; i++)
        {
            var id = Guid.Parse($"71000000-0000-0000-0000-{i + 100:000000000000}");
            await using (var db = f.CreateContext())
            {
                db.Add(new Project { Id = id, WorkspaceId = WorkspaceId, Code = $"B{i}", Name = $"scope {i}", CreatedBy = AuthorId, CreatedAtUtc = f.Clock.GetUtcNow() });
                await db.SaveChangesAsync();
            }
            await G.AuthorityAsync(f, AuthorId, AuthorPersonId, projectId: id);
        }
        await G.AuthorityAsync(f, AuthorId, AuthorPersonId, company: true);
        var first = Guid.Parse("71000000-0000-0000-0000-000000000200");
        var second = Guid.Parse("71000000-0000-0000-0000-000000000201");
        await using (var seed = f.CreateContext())
        {
            await seed.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, DiscoveryCapabilities));
            await seed.Set<GrantAssignment>().Where(x => x.ProjectId != null && x.ProjectId != first && x.ProjectId != second)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(a => a.RevokedBy, ReviewerId)
                    .SetProperty(a => a.RevocationReason, "paging fixture"));
            // Retained company proposal is deliberately irrelevant to each discovery UI.
            await seed.Set<GrantProposal>().Where(x => x.ProjectId == null).ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "payment.record" }));
        }
        var session = await f.SessionAsync(AuthorId);
        var counter = new QueryCounter();
        await using var read = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(counter).Options);
        var page1 = await Discover(f, read, session, kind, 1, 1);
        var firstCount = counter.Count;
        var page2 = await Discover(f, read, session, kind, 2, 1);
        var secondCount = counter.Count - firstCount;
        var page3 = await Discover(f, read, session, kind, 3, 1);
        var thirdCount = counter.Count - firstCount - secondCount;
        Assert.All(new[] { firstCount, secondCount, thirdCount }, count => Assert.InRange(count, 1, 30));
        output.WriteLine($"{kind} denied-candidate boundary: 104 candidate scopes, page-size=1; page queries={firstCount}/{secondCount}/{thirdCount}");
        Assert.Equal(200, page1.Status); Assert.Equal(200, page2.Status); Assert.Equal(200, page3.Status);
        Assert.Equal(new FinanceScope(WorkspaceId, first), Assert.Single(page1.Value!).Scope);
        Assert.Equal("scope 100", page1.Value![0].ProjectName);
        Assert.Equal(new FinanceScope(WorkspaceId, second), Assert.Single(page2.Value!).Scope);
        Assert.Equal("scope 101", page2.Value![0].ProjectName);
        Assert.Empty(page3.Value!);
    }

    [Theory]
    [InlineData("grant", false)]
    [InlineData("grant", true)]
    [InlineData("policy", false)]
    [InlineData("policy", true)]
    [InlineData("request", false)]
    [InlineData("request", true)]
    public async Task Discovery_final_check_reads_fresh_expiry_and_revocation(string kind, bool revoke)
    {
        await using var f = await G.ReadyAsync(postgres);
        await using (var seed = f.CreateContext())
            await seed.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId).ExecuteUpdateAsync(x =>
                x.SetProperty(p => p.Capabilities, DiscoveryCapabilities).SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow().AddSeconds(1)));
        var session = await f.SessionAsync(AuthorId);
        var changed = false;
        var interceptor = new BeforeFinalSession(async () =>
        {
            changed = true;
            if (!revoke) { f.Clock.Advance(TimeSpan.FromSeconds(1)); return; }
            // Deliberately bypass the writer lock in this disposable fixture to probe the fresh final read.
            // Normal writers serialize on 7241002; the concurrent-read test below observes real lock waits.
            await using var change = f.CreateContext();
            await change.Set<GrantAssignment>().ExecuteUpdateAsync(x => x.SetProperty(a => a.RevokedAtUtc, f.Clock.GetUtcNow())
                .SetProperty(a => a.RevokedBy, ReviewerId).SetProperty(a => a.RevocationReason, "final-read fault injection"));
        });
        await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(interceptor).Options);
        var result = await Discover(f, db, session, kind);
        Assert.True(changed); Assert.Equal(403, result.Status); Assert.Null(result.Value);
    }

    private sealed class BeforeFinalSession(Func<Task> change, int ordinal = 3) : DbCommandInterceptor
    {
        private int sessions;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM sessions", StringComparison.Ordinal) && ++sessions == ordinal) await change();
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grant_page_final_coverage_rejects_expired_or_revoked_delegation_with_management_still_active(bool revoke)
    {
        await using var f = await G.ReadyAsync(postgres);
        Assert.Equal(201, (await G.ProposeAsync(f)).Status);
        var delegation = await G.AuthorityAsync(f, AuthorId, AuthorPersonId);
        await using (var seed = f.CreateContext())
        {
            var proposal = await seed.Set<GrantAssignment>().Where(x => x.Id == delegation).Select(x => x.ProposalId).SingleAsync();
            await seed.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId && x.Id != proposal)
                .ExecuteUpdateAsync(x => x.SetProperty(p => p.DelegableCapabilities, Array.Empty<string>()));
            await seed.Set<GrantProposal>().Where(x => x.Id == proposal).ExecuteUpdateAsync(x =>
                x.SetProperty(p => p.Capabilities, Array.Empty<string>()).SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow().AddSeconds(1)));
        }
        var session = await f.SessionAsync(AuthorId);
        var changed = false;
        var interceptor = new BeforeFinalSession(async () =>
        {
            changed = true;
            if (!revoke) { f.Clock.Advance(TimeSpan.FromSeconds(1)); return; }
            // Fault injection bypasses writer serialization, as in the discovery final-check test.
            await using var change = f.CreateContext();
            await change.Set<GrantAssignment>().Where(x => x.Id == delegation).ExecuteUpdateAsync(x =>
                x.SetProperty(a => a.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(a => a.RevokedBy, ReviewerId)
                    .SetProperty(a => a.RevocationReason, "final coverage fault injection"));
        }, ordinal: 6);
        await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(interceptor).Options);
        var result = await GrantReader(f, db, session).ListAsync(new(WorkspaceId, ProjectId), false, 1, 100, default);
        Assert.True(changed); Assert.Equal(403, result.Status); Assert.Null(result.Value);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        Assert.True((await FieldAdvanceAccessTests.Access(db, session, f.Clock).CheckAsync(AuthorId, new(WorkspaceId, ProjectId), "grant.propose", default)).Allowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grant_page_matches_scalar_detail_flags_and_payload(bool queue)
    {
        await using var f = await G.ReadyAsync(postgres);
        var pending = (await G.ProposeAsync(f)).Value!;
        var approved = (await G.ProposeAsync(f)).Value!;
        Assert.Equal(200, (await G.ReviewAsync(f, approved.Id)).Status);
        foreach (var actor in new[] { AuthorId, ReviewerId })
        {
            var session = await f.SessionAsync(actor);
            await using var db = f.CreateContext();
            var reader = GrantReader(f, db, session);
            var result = await reader.ListAsync(new(WorkspaceId, ProjectId), queue, 1, 100, default);
            Assert.Equal(200, result.Status);
            Assert.Contains(result.Value!, x => x.Id == pending.Id);
            if (queue) Assert.DoesNotContain(result.Value!, x => x.Id == approved.Id);
            else Assert.Contains(result.Value!, x => x.Id == approved.Id);
            foreach (var row in result.Value!)
            {
                var scalar = await reader.DetailsAsync(new(WorkspaceId, ProjectId), row.Id, default);
                Assert.Equal(200, scalar.Status);
                Assert.Equal(JsonSerializer.Serialize(scalar.Value), JsonSerializer.Serialize(row));
            }
            var p = result.Value!.Single(x => x.Id == pending.Id);
            Assert.Equal(actor == ReviewerId, p.CanReview); Assert.Equal(actor == ReviewerId, p.CanApprove);
            Assert.False(p.CanRevoke);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(20)]
    public async Task Concurrent_scope_reads_measure_database_lock_wait_separately(int scopes)
    {
        await using var f = await G.ReadyAsync(postgres);
        for (var i = 1; i < scopes; i++)
        {
            var project = Guid.NewGuid();
            await using (var seed = f.CreateContext())
            {
                seed.Add(new Project { Id = project, WorkspaceId = WorkspaceId, Code = $"C{i}", Name = $"concurrent {i}", CreatedBy = AuthorId, CreatedAtUtc = f.Clock.GetUtcNow() });
                await seed.SaveChangesAsync();
            }
            await G.AuthorityAsync(f, AuthorId, AuthorPersonId, projectId: project);
        }
        var session = await f.SessionAsync(AuthorId);
        var app = "m2-" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(f.Database.ConnectionString) { ApplicationName = app }.ConnectionString;
        await using var blocker = f.CreateContext();
        await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            var counter = new QueryCounter();
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(connection).AddInterceptors(counter).Options);
            var timer = Stopwatch.StartNew();
            var result = await GrantReader(f, db, session).ScopesAsync(1, 100, default);
            Assert.Equal(200, result.Status); Assert.Equal(scopes, result.Value!.Length);
            return (Queries: counter.Count, Elapsed: timer.Elapsed.TotalMilliseconds, Lock: counter.LockAcquisitionMs);
        }).ToArray();
        await using var observer = new NpgsqlConnection(f.Database.ConnectionString);
        await observer.OpenAsync();
        var observed = Stopwatch.StartNew();
        var waiters = 0;
        try
        {
            while (waiters < 4 && observed.Elapsed < TimeSpan.FromSeconds(10))
            {
                await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name = @app AND wait_event_type = 'Lock' AND wait_event = 'advisory'", observer);
                command.Parameters.AddWithValue("app", app);
                waiters = Convert.ToInt32(await command.ExecuteScalarAsync());
                if (waiters < 4) await Task.Delay(10);
            }
        }
        finally { await tx.CommitAsync(); }
        var releaseMs = observed.Elapsed.TotalMilliseconds;
        var results = await Task.WhenAll(tasks);
        Assert.Equal(4, waiters);
        Assert.All(results, x => { Assert.InRange(x.Queries, 1, 24); Assert.True(x.Lock > 0); });
        foreach (var result in results)
            output.WriteLine($"concurrent scopes={scopes} readers=4 queries={result.Queries} total-ms={result.Elapsed:F1} lock-acquisition-command-ms={result.Lock:F1} remaining-ms={result.Elapsed - result.Lock:F1}");
        output.WriteLine($"Observed {waiters} PostgreSQL advisory-lock waiters; controlled blocker released after {releaseMs:F1}ms observation. Lock-command duration includes round-trip overhead; local contention experiment, not production SLA.");
    }
}
