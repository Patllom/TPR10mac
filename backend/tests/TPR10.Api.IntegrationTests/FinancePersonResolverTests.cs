using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FinancePersonResolverTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    public async Task Resolves_only_one_active_link_without_unordered_row_limit_warning(int count, bool samePerson)
    {
        await using var f = await CreateAsync(postgres);
        await f.SeedPeopleAsync();
        await using (var seed = f.CreateContext())
        {
            // Corrupt/legacy duplicates must still fail closed. Only this disposable database
            // relaxes the index and history trigger; production retains both guards.
            if (count > 1)
            {
                await seed.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_finance_account_links_workspace_id_user_id\"");
                await seed.Database.ExecuteSqlRawAsync("ALTER TABLE finance_account_links DISABLE TRIGGER finance_account_link_history");
            }
            var persons = new[] { RecipientPersonId, AuthorPersonId, ReviewerPersonId };
            for (var i = 0; i < count; i++)
                seed.Add(new FinanceAccountLink
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WorkspaceId,
                    UserId = AliasAccountId,
                    PersonId = samePerson ? RecipientPersonId : persons[i],
                    CreatedBy = AuthorId,
                    CreatedAtUtc = f.Clock.GetUtcNow(),
                    Reason = "Synthetic resolver regression"
                });
            await seed.SaveChangesAsync();
        }

        await using var db = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>()
            .UseNpgsql(f.Database.ConnectionString)
            .ConfigureWarnings(w => w.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning))
            .Options);
        var actual = await new FinancePersonResolver(db, f.Clock).ResolveAsync(AliasAccountId, WorkspaceId, default);
        Assert.Equal(count == 1 ? (Guid?)RecipientPersonId : null, actual);
    }
}
