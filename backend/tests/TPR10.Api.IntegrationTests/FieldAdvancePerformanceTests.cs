using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Grants;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.Organization;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using Xunit.Abstractions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using G = TPR10.Api.IntegrationTests.FieldAdvanceGrantTests;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;
using L = TPR10.Api.IntegrationTests.FieldAdvanceRequestLifecycleTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed partial class FieldAdvancePerformanceTests(PostgresFixture postgres, ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Policy_content_queries_do_not_grow_per_history_row(bool timeline)
    {
        await using var f = await P.Ready(postgres);
        PolicyView? active = null;
        var counts = new List<int>();
        foreach (var size in new[] { 4, 20 })
        {
            for (var i = active is null ? 0 : 4; i < size; i++)
                active = await P.Review(f, await P.Submit(f, await P.Create(f, source: active, active: active, content: P.Content(i + 1))));
            var session = await f.SessionAsync(AuthorId);
            var counter = new QueryCounter();
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(counter).Options);
            var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
            var resolver = new PolicyResolver(db, access, session, f.Clock);
            var timer = Stopwatch.StartNew();
            if (timeline)
            {
                var result = await resolver.ResolveAsync(new(WorkspaceId, null), f.Clock.GetUtcNow(), default);
                Assert.Equal(200, result.Status); Assert.Equal(active!.Id, result.Value!.CompanyVersionId);
            }
            else
            {
                var result = await new PolicyReadService(db, access, session, f.Clock, resolver).ListAsync(AuthorId, new(WorkspaceId, null), 1, 100, default);
                Assert.Equal(200, result.Status); Assert.Equal(size, result.Value!.Length);
            }
            output.WriteLine($"policy timeline={timeline} rows={size} queries={counter.Count} locked-read-ms={timer.Elapsed.TotalMilliseconds:F1}");
            Assert.InRange(counter.Count, 1, 24);
            counts.Add(counter.Count);
        }
        Assert.True(counts[1] <= counts[0] + 2, $"Per-row query amplification: {counts[0]} -> {counts[1]}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grant_filtering_queries_do_not_grow_with_unpaged_candidates(bool growPage)
    {
        await using var f = await G.ReadyAsync(postgres);
        var counts = new List<int>();
        foreach (var size in new[] { 4, 20 })
        {
            for (var i = size == 4 ? 0 : 4; i < size; i++) Assert.Equal(201, (await G.ProposeAsync(f)).Status);
            var session = await f.SessionAsync(AuthorId);
            var counter = new QueryCounter();
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(counter).Options);
            var reader = new GrantReadService(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), G.Service(f, db, session),
                new FinancePersonResolver(db, f.Clock), new ScopeAccess(db, session, new EffectiveRolePolicy(db), f.Clock), session, f.Clock);
            var timer = Stopwatch.StartNew();
            var pageSize = growPage ? size : 1;
            var result = await reader.ListAsync(new(WorkspaceId, ProjectId), false, 1, pageSize, default);
            Assert.Equal(200, result.Status); Assert.Equal(pageSize, result.Value!.Length);
            Assert.All(result.Value, row => Assert.Equal("Pending", row.State));
            output.WriteLine($"grant candidates={size} page-size={pageSize} queries={counter.Count} locked-read-ms={timer.Elapsed.TotalMilliseconds:F1}");
            Assert.InRange(counter.Count, 1, 80);
            counts.Add(counter.Count);
        }
        Assert.True(counts[1] <= counts[0] + 2, $"Per-candidate query amplification: {counts[0]} -> {counts[1]}");
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("policy")]
    [InlineData("request")]
    public async Task Scope_discovery_queries_grow_by_batches_not_scopes(string kind)
    {
        await using var f = await G.ReadyAsync(postgres);
        var counts = new List<int>();
        foreach (var size in new[] { 4, 20 })
        {
            for (var i = size == 4 ? 1 : 4; i < size; i++)
            {
                var project = Guid.NewGuid();
                await using (var seed = f.CreateContext())
                {
                    seed.Add(new Project { Id = project, WorkspaceId = WorkspaceId, Code = $"PERF{i}", Name = $"จำลอง {i}", CreatedBy = AuthorId, CreatedAtUtc = f.Clock.GetUtcNow() });
                    await seed.SaveChangesAsync();
                }
                await G.AuthorityAsync(f, AuthorId, AuthorPersonId, projectId: project);
            }
            await using (var seed = f.CreateContext())
                await seed.Set<GrantProposal>().Where(x => x.RecipientId == AuthorId).ExecuteUpdateAsync(x =>
                    x.SetProperty(p => p.Capabilities, new[] { "grant.propose", "grant.review", "grant.revoke", "policy.edit.project", "policy.review", "request.create", "request.delegate", "request.approve", "report.read" }));
            var session = await f.SessionAsync(AuthorId);
            var counter = new QueryCounter();
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(counter).Options);
            var reader = new GrantReadService(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), G.Service(f, db, session),
                new FinancePersonResolver(db, f.Clock), new ScopeAccess(db, session, new EffectiveRolePolicy(db), f.Clock), session, f.Clock);
            var timer = Stopwatch.StartNew();
            if (kind == "grant")
            {
                var result = await reader.ScopesAsync(1, 100, default);
                Assert.Equal(200, result.Status); Assert.Equal(size, result.Value!.Length);
                Assert.All(result.Value, row => Assert.Equal(new[] { "grant.propose", "grant.review", "grant.revoke" }, row.Capabilities));
            }
            else if (kind == "policy")
            {
                var result = await new PolicyWorkspaceService(db, FieldAdvanceAccessTests.Access(db, session, f.Clock),
                    new ScopeAccess(db, session, new EffectiveRolePolicy(db), f.Clock), session).ScopesAsync(1, 100, default);
                Assert.Equal(200, result.Status); Assert.Equal(size, result.Value!.Length);
                Assert.All(result.Value, row => Assert.Equal(new[] { "policy.edit.project", "policy.review" }, row.Capabilities));
            }
            else
            {
                var result = await RequestReader(f, db, session).ScopesAsync(AuthorId, 1, 100, default);
                Assert.Equal(200, result.Status); Assert.Equal(size, result.Value!.Length);
                Assert.All(result.Value, row => Assert.Equal(new[] { "request.create", "request.delegate", "request.approve", "report.read" }, row.Capabilities));
            }
            output.WriteLine($"{kind} discovery scopes={size} queries={counter.Count} elapsed-ms={timer.Elapsed.TotalMilliseconds:F1}");
            Assert.InRange(counter.Count, 1, 24);
            counts.Add(counter.Count);
        }
        Assert.True(counts[1] <= counts[0] + 2, $"Per-scope amplification ({kind}): {counts[0]} -> {counts[1]}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_history_queries_do_not_grow_with_page_size(bool system)
    {
        await using var f = await L.Ready(postgres);
        var request = await L.Create(f);
        var session = await f.SessionAsync(AuthorId);
        await using (var seed = f.CreateContext())
            for (var i = 1; i < 20; i++)
            {
                f.Clock.Advance(TimeSpan.FromSeconds(1));
                if (system)
                {
                    var audit = new TPR10.Api.Auditing.AuditEventWriter(seed, new TPR10.Api.Correlation.CorrelationContext(), f.Clock);
                    await audit.WriteAsync(new TPR10.Api.Auditing.SecurityAuditRequest(null, null, WorkspaceId, ProjectId, null,
                        "field-advance.request.reevaluate", "field-advance-request", request.Id, "success", new Dictionary<string, string>
                        {
                            ["actor-type"] = "system",
                            ["source"] = "request-routing-worker",
                            ["reason"] = "system-re-evaluation",
                            ["assignment-id"] = Guid.Empty.ToString(),
                            ["evidence-reference"] = System.Text.Json.JsonSerializer.Serialize(new { revisionId = request.RevisionId, version = i + 1, before = new { State = "Held" }, after = new { State = "PendingApproval" } })
                        }), default);
                    await seed.SaveChangesAsync();
                    continue;
                }
                var result = await L.Draft(f, seed, session).UpdateAsync(AuthorId, request.Id,
                    request.Content with { Purpose = $"history {i}" }, L.Meta(request.Version), default);
                Assert.Equal(200, result.Status); request = result.Value!;
            }
        var counts = new List<int>();
        foreach (var size in new[] { 4, 20 })
        {
            var counter = new QueryCounter();
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(counter).Options);
            var timer = Stopwatch.StartNew();
            var result = await RequestReader(f, db, session).HistoryAsync(AuthorId, request.Id, 1, size, default);
            Assert.Equal(200, result.Status); Assert.Equal(size, result.Value!.Length);
            Assert.Equal(AuthorId, result.Value[0].Actor);
            Assert.All(result.Value.Skip(1), row => Assert.Equal(system ? null : AuthorId, row.Actor));
            Assert.Equal(Enumerable.Range(1, size).Select(x => (long)x), result.Value.Select(x => x.Version));
            if (system) Assert.All(result.Value.Skip(1), row =>
            {
                Assert.Equal("Held", row.Metadata.GetProperty("before").GetProperty("State").GetString());
                Assert.Equal(request.RevisionId, row.RevisionId);
            });
            output.WriteLine($"request history system={system} rows={size} queries={counter.Count} elapsed-ms={timer.Elapsed.TotalMilliseconds:F1}");
            Assert.InRange(counter.Count, 1, 40);
            counts.Add(counter.Count);
        }
        Assert.True(counts[1] <= counts[0] + 2, $"Per-history-row amplification: {counts[0]} -> {counts[1]}");
    }

    private static RequestReadService RequestReader(FieldAdvanceFixture f, Tpr10DbContext db, TPR10.Api.Identity.Sessions.RequestSession session)
    {
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock);
        var people = new FinancePersonResolver(db, f.Clock);
        return new(db, access, people, new(db, access, people, f.Clock), L.Draft(f, db, session),
            new(db, session, new EffectiveRolePolicy(db), f.Clock), f.Clock, new(db, access, session, f.Clock));
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        internal int Count { get; private set; }
        internal double LockAcquisitionMs { get; private set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock(7241002)", StringComparison.Ordinal))
                LockAcquisitionMs += eventData.Duration.TotalMilliseconds;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
    }
}
