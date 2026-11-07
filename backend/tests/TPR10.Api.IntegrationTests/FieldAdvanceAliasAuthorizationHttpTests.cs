using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceAliasAuthorizationHttpTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("grants", "grant.propose")]
    [InlineData("policies", "policy.edit.project")]
    [InlineData("requests", "request.create")]
    public async Task Authenticated_aliases_share_person_but_not_finance_grant(string resource, string capability)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await f.ApprovedGrantAsync(capability, ProjectId);
        await using (var db = f.CreateContext())
        {
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = RecipientPersonId,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow(),
                Reason = "M2-R1 same-person alias fixture"
            });
            // A pending proposal makes the ungranted alias a scope candidate. Discovery must
            // exercise batch authorization, not merely omit an account with no candidates.
            db.Add(new GrantProposal
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RecipientId = AliasAccountId,
                RecipientPersonId = RecipientPersonId,
                ProposedBy = AuthorId,
                ProposedByPersonId = AuthorPersonId,
                State = "Pending",
                Capabilities = [capability],
                DelegableCapabilities = [],
                StartsAtUtc = f.Clock.GetUtcNow().AddMinutes(-1),
                CreatedAtUtc = f.Clock.GetUtcNow(),
                Reason = "M2-R1 candidate without approved authority"
            });
            await db.SaveChangesAsync();
            var people = new FinancePersonResolver(db, f.Clock);
            Assert.Equal(RecipientPersonId, await people.ResolveAsync(RecipientId, WorkspaceId, default));
            Assert.Equal(RecipientPersonId, await people.ResolveAsync(AliasAccountId, WorkspaceId, default));
            var assignment = Assert.Single(await db.Set<GrantAssignment>().ToArrayAsync());
            var approved = await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId);
            Assert.Equal(RecipientId, approved.RecipientId);
            Assert.Equal("Approved", approved.State);
        }

        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        using var granted = await Client(factory, f, RecipientId);
        using var alias = await Client(factory, f, AliasAccountId);
        Assert.NotEqual(granted.DefaultRequestHeaders.GetValues("Cookie").Single(), alias.DefaultRequestHeaders.GetValues("Cookie").Single());
        await AssertActiveSession(granted, RecipientId);
        await AssertActiveSession(alias, AliasAccountId);

        var root = $"/api/v1/field-advances/{resource}";
        using (var response = await granted.GetAsync(root + "/scopes"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var row = Assert.Single(await response.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);
            Assert.Equal(WorkspaceId, row.GetProperty("scope").GetProperty("workspaceId").GetGuid());
            Assert.Equal(ProjectId, row.GetProperty("scope").GetProperty("projectId").GetGuid());
            Assert.Equal(capability, Assert.Single(row.GetProperty("capabilities").EnumerateArray()).GetString());
        }
        using (var response = await alias.GetAsync(root + "/scopes"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
        }

        // A caller can bypass discovery and supply a known scope: scalar checks must
        // enforce the same account boundary even though both accounts resolve to one person.
        var list = root + $"?workspaceId={WorkspaceId}&projectId={ProjectId}&mode=mine";
        using (var response = await alias.GetAsync(list))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("finance-access-denied", body);
            Assert.DoesNotContain("capabilities", body);
        }
        using (var response = await granted.GetAsync(list))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Denial must not be an authentication/MFA failure or invalidate either session.
        await AssertActiveSession(alias, AliasAccountId);
        await AssertActiveSession(granted, RecipientId);
    }

    private static async Task AssertActiveSession(HttpClient client, Guid actor)
    {
        using var response = await client.GetAsync("/api/v1/auth/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(actor, session.GetProperty("userId").GetGuid());
        Assert.Equal("Active", session.GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.String, session.GetProperty("mfaVerifiedAtUtc").ValueKind);
    }
}
