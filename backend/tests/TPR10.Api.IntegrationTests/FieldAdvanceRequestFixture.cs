using TPR10.Api.Data;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

internal sealed class FieldAdvanceRequestFixture(FieldAdvanceFixture foundation) : IAsyncDisposable
{
    internal const string PreviousMigration = "20261020233422_AddFieldAdvancePolicyLifecycle";
    internal static readonly string[] Tables = ["field_advance_requests", "field_advance_request_revisions",
        "field_advance_request_lines", "field_advance_approval_steps", "field_advance_request_events",
        "field_advance_request_participants"];
    public Tpr10DbContext CreateContext() => foundation.CreateContext();
    internal AdvanceRequest Request { get; } = new()
    {
        Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId,
        OwnerPersonId = AuthorPersonId, DelegatePersonId = RecipientPersonId, CreatedBy = AuthorId
    };
    internal AdvanceRequestRevision Revision { get; private set; } = null!;
    internal AdvanceRequestLine Line { get; private set; } = null!;
    internal AdvanceApprovalStep Step { get; private set; } = null!;
    internal AdvanceRequestRevision NewRevision(int number = 1, bool current = true) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, RequestId = Request.Id,
        RevisionNumber = number, IsCurrent = current, ContentJson = "{\"Purpose\":\"field work\"}",
        ContentHash = new('a', 64), Total = 12.35m, CreatedBy = AuthorId, CreatedByPersonId = AuthorPersonId,
        CompanyPolicyRevisionId = CompanyRevisionId, ProjectPolicyRevisionId = ProjectRevisionId,
        PolicySnapshotJson = "{\"ClearingDays\":7}", PolicyHash = new('b', 64)
    };
    internal FinanceCommandReceipt NewReceipt() => new()
    {
        CommandId = Guid.NewGuid(), WorkspaceId = WorkspaceId, ActorId = ReviewerId,
        Action = "request.approve", PayloadSha256 = new('c', 64), Status = 200, ResultJson = "{}"
    };
    internal AdvanceRequestEvent NewEvent(Guid commandId) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, RequestId = Request.Id,
        RevisionId = Revision.Id, StepId = Step.Id, CommandId = commandId, ActorId = ReviewerId,
        ActorPersonId = ReviewerPersonId, Kind = "Approved", DataJson = "{}", Reason = "Reviewed", AggregateVersion = 2
    };
    internal async Task SeedRequestAsync(bool submitted = false)
    {
        await using var db = CreateContext();
        Revision = NewRevision();
        Line = new()
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, RequestId = Request.Id,
            RevisionId = Revision.Id, LineOrder = 1, CategoryId = Guid.NewGuid(), CategoryName = "Travel",
            Description = "Trip", Mode = "QuantityRate", Unit = "trip", Quantity = 1.2345m, UnitRate = 10m, Amount = 12.35m
        };
        Step = new()
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, RequestId = Request.Id,
            RevisionId = Revision.Id, StepOrder = 1, LevelCode = "Supervisor", AssigneePersonId = ReviewerPersonId
        };
        db.AddRange(Request, Revision, Line, Step);
        db.Add(new AdvanceRequestParticipant
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, ProjectId = ProjectId, RequestId = Request.Id,
            PersonId = AuthorPersonId, Role = "Owner", CreatedBy = AuthorId
        });
        await db.SaveChangesAsync();
        if (submitted)
        {
            Revision.SubmittedAtUtc = DateTimeOffset.UtcNow;
            Revision.ConfirmedByPersonId = AuthorPersonId;
            await db.SaveChangesAsync();
        }
    }
    public static async Task<FieldAdvanceRequestFixture> CreateAsync(PostgresFixture postgres)
    {
        var foundation = await FieldAdvanceFixture.CreateAsync(postgres);
        try
        {
            await foundation.SeedPoliciesAsync();
            return new(foundation);
        }
        catch { await foundation.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => foundation.DisposeAsync();
}
