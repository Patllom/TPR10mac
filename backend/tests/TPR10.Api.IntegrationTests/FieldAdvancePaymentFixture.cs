using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

// Explicit financial test data in the existing disposable PostgreSQL fixture only.
internal sealed class FieldAdvancePaymentFixture(FieldAdvanceFixture foundation) : IAsyncDisposable
{
    internal static readonly Guid PayerPersonId = Guid.Parse("72000000-0000-0000-0000-000000000034");
    public Tpr10DbContext CreateContext() => foundation.CreateContext();
    public static async Task<FieldAdvancePaymentFixture> CreateAsync(PostgresFixture postgres)
    {
        var foundation = await FieldAdvanceFixture.CreateAsync(postgres);
        try
        {
            await foundation.SeedPoliciesAsync();
            await using var db = foundation.CreateContext();
            await db.Database.ExecuteSqlAsync($"INSERT INTO finance_persons(id,workspace_id,created_by,reason) VALUES ({PayerPersonId},{WorkspaceId},{AuthorId},'payment test payer')");
            await db.Database.ExecuteSqlAsync($"INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,reason) VALUES (gen_random_uuid(),{WorkspaceId},{AliasAccountId},{PayerPersonId},{AuthorId},'payment test payer mapping')");
            return new(foundation);
        }
        catch { await foundation.DisposeAsync(); throw; }
    }

    public async Task<Guid> SeedApprovedAsync(decimal amount, CancellationToken ct)
    {
        await using var db = CreateContext();
        var request = new AdvanceRequest { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, OwnerPersonId = AuthorPersonId, CreatedBy = AuthorId, State = "Approved" };
        var revision = new AdvanceRequestRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request.Id,
            RevisionNumber = 1,
            IsCurrent = true,
            ContentJson = "{}",
            ContentHash = new('a', 64),
            Total = amount,
            CreatedBy = AuthorId,
            CreatedByPersonId = AuthorPersonId,
            CompanyPolicyRevisionId = CompanyRevisionId,
            PolicySnapshotJson = "{}",
            PolicyHash = new('b', 64)
        };
        db.AddRange(request, revision);
        var step = new AdvanceApprovalStep
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = request.Id,
            RevisionId = revision.Id,
            StepOrder = 1,
            LevelCode = "Supervisor",
            AssigneePersonId = ReviewerPersonId
        };
        db.Add(step); await db.SaveChangesAsync(ct);
        revision.SubmittedAtUtc = DateTimeOffset.UtcNow; revision.ConfirmedByPersonId = AuthorPersonId;
        await db.SaveChangesAsync(ct);
        step.Outcome = "Approved"; step.DecidedBy = ReviewerId; step.DecidedByPersonId = ReviewerPersonId;
        step.DecidedAtUtc = DateTimeOffset.UtcNow; step.DecisionReason = "Test approval";
        var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == request.Id, ct);
        account.ApprovedRevisionId = revision.Id; account.Approved = amount; account.Spendable = amount;
        await db.SaveChangesAsync(ct);
        return request.Id;
    }

    internal async Task<(AdvanceInstallment Installment, AdvanceInstallmentRevision Revision)> SeedInstallmentAsync(Guid requestId)
    {
        await using var db = CreateContext();
        var request = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == requestId);
        var installment = new AdvanceInstallment { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, FamilyId = request.FamilyId, RequestId = requestId, CreatedBy = RecipientId };
        var revision = new AdvanceInstallmentRevision
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            FamilyId = request.FamilyId,
            RequestId = requestId,
            InstallmentId = installment.Id,
            RevisionNumber = 1,
            IsCurrent = true,
            Amount = 2000m,
            Channel = "Cash",
            RecipientPersonId = AuthorPersonId,
            ContentHash = new('c', 64),
            Reason = "Test installment",
            CreatedBy = RecipientId
        };
        db.AddRange(installment, revision); await db.SaveChangesAsync();
        return (installment, revision);
    }

    internal static FinanceCommandReceipt Receipt() => new()
    {
        WorkspaceId = WorkspaceId,
        CommandId = Guid.NewGuid(),
        ActorId = AliasAccountId,
        Action = "payment.test",
        PayloadSha256 = new('d', 64),
        Status = 200,
        ResultJson = "{}"
    };
    public ValueTask DisposeAsync() => foundation.DisposeAsync();
}
