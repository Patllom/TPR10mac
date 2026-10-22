using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvancePolicyBaseProvenanceTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Project_only_actor_cannot_install_unrelated_retired_basis_to_read_company_content(bool update)
    {
        await using var f = await P.Ready(postgres);
        var first = await P.Review(f, await P.Submit(f, await P.Create(f)));
        var project = await P.Create(f, project: true, basis: first.Id);
        var unrelated = await P.Review(f, await P.Submit(f, await P.Create(f, source: first, active: first, content: P.Content(11))));
        var latest = await P.Review(f, await P.Submit(f, await P.Create(f, source: unrelated, active: unrelated, content: P.Content(19))));
        await using (var change = f.CreateContext())
        {
            foreach (var grant in await change.Set<GrantProposal>().Where(x => x.RecipientId == RecipientId).ToArrayAsync())
                grant.Capabilities = grant.ProjectId is null ? [] : ["policy.edit.project"];
            await change.SaveChangesAsync();
        }
        var session = await f.SessionAsync(RecipientId);
        await using var db = f.CreateContext();
        var service = P.Service(f, db, session);
        var resolver = new PolicyResolver(db, FieldAdvanceAccessTests.Access(db, session, f.Clock), session, f.Clock);
        var before = await db.Set<PolicyRevision>().CountAsync();
        var receipts = await db.Set<FinanceCommandReceipt>().CountAsync();
        var meta = update ? P.Meta(project) with { Policy = P.Meta(project).Policy! with { BaseCompanyVersionId = unrelated.Id } }
            : new CommandMeta(Guid.NewGuid(), 1, "ทดสอบฐานนอกสิทธิ์") { Policy = new("Override", project.Id, unrelated.Id) };
        var result = update
            ? await service.UpdateDraftAsync(RecipientId, project.Id, P.Content(), meta, default)
            : await service.CreateDraftAsync(RecipientId, new(WorkspaceId, ProjectId), unrelated.Id, P.Content(), meta, default);
        Assert.Equal(403, result.Status); Assert.Null(result.Value);
        Assert.Equal(before, await db.Set<PolicyRevision>().CountAsync());
        Assert.Equal(receipts, await db.Set<FinanceCommandReceipt>().CountAsync());
        var context = await resolver.CompanyContextAsync(new(WorkspaceId, ProjectId), project.Id, default);
        Assert.Equal(200, context.Status);
        Assert.Equal(first.Id, context.Value!.Basis.Id);
        Assert.Equal(latest.Id, context.Value.Latest.Id);
        Assert.Equal(403, (await resolver.CompanyContextAsync(new(WorkspaceId, ProjectId), unrelated.Id, default)).Status);
        // Both permitted choices remain writable; previous basis may have retired meanwhile.
        foreach (var allowed in new[] { first.Id, latest.Id })
        {
            if (update)
            {
                var options = P.Meta(project) with { Policy = P.Meta(project).Policy! with { BaseCompanyVersionId = allowed } };
                var changed = await P.Run(f, RecipientId, s => s.UpdateDraftAsync(RecipientId, project.Id, P.Content(), options, default));
                Assert.Equal(200, changed.Status); project = changed.Value!;
            }
            else
            {
                var options = new CommandMeta(Guid.NewGuid(), 1, "ฐานที่อ่านได้") { Policy = new("Override", project.Id, allowed) };
                Assert.Equal(201, (await service.CreateDraftAsync(RecipientId, new(WorkspaceId, ProjectId), allowed, P.Content(), options, default)).Status);
            }
        }
    }
}
