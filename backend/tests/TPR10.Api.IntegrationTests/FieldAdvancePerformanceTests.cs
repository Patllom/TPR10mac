using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Grants;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.Organization;
using TPR10.Api.Organization.Data;
using TPR10.Api.Scopes;
using Xunit.Abstractions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using G = TPR10.Api.IntegrationTests.FieldAdvanceGrantTests;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePerformanceTests(PostgresFixture postgres, ITestOutputHelper output)
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
            counts.Add(counter.Count);
        }
        Assert.True(counts[1] <= counts[0] + 2, $"Per-row query amplification: {counts[0]} -> {counts[1]}");
    }

    [Fact]
    public async Task Grant_filtering_queries_do_not_grow_with_unpaged_candidates()
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
            var result = await reader.ListAsync(new(WorkspaceId, ProjectId), false, 1, 1, default);
            Assert.Equal(200, result.Status); Assert.Single(result.Value!);
            output.WriteLine($"grant candidates={size} page-size=1 queries={counter.Count} locked-read-ms={timer.Elapsed.TotalMilliseconds:F1}");
            counts.Add(counter.Count);
        }
        Assert.True(counts[1] <= counts[0] + 2, $"Per-candidate query amplification: {counts[0]} -> {counts[1]}");
    }

    [Fact]
    public async Task Grant_scope_discovery_measurement_preserves_authorization_before_paging()
    {
        await using var f = await G.ReadyAsync(postgres);
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
            var session = await f.SessionAsync(AuthorId);
            var counter = new QueryCounter();
            await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>().UseNpgsql(f.Database.ConnectionString).AddInterceptors(counter).Options);
            var reader = new GrantReadService(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), G.Service(f, db, session),
                new FinancePersonResolver(db, f.Clock), new ScopeAccess(db, session, new EffectiveRolePolicy(db), f.Clock), session, f.Clock);
            var timer = Stopwatch.StartNew();
            var result = await reader.ScopesAsync(1, 100, default);
            Assert.Equal(200, result.Status); Assert.Equal(size, result.Value!.Length);
            Assert.All(result.Value, row => Assert.Contains("grant.propose", row.Capabilities));
            output.WriteLine($"grant discovery scopes={size} queries={counter.Count} locked-read-ms={timer.Elapsed.TotalMilliseconds:F1}; residual cross-scope batching follow-up, not an SLA");
        }
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        internal int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
    }
}
