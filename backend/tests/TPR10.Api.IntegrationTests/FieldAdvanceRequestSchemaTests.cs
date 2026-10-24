using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using TPR10.Api.FieldAdvances.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using TPR10.Api.Data.Entities;
using System.Data;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRequestSchemaTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public async Task One_current_revision(int number, bool current)
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var db = f.CreateContext();
        db.Add(f.NewRevision(number, current));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Theory]
    [InlineData("project-company")]
    [InlineData("owner-company")]
    [InlineData("revision-request")]
    [InlineData("line-revision")]
    [InlineData("step-revision")]
    [InlineData("policy-project")]
    [InlineData("policy-company")]
    [InlineData("participant-request")]
    public async Task Cross_scope_reference_is_rejected(string reference)
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var db = f.CreateContext();
        var otherProject = Guid.Parse("71000000-0000-0000-0000-000000000012");
        switch (reference)
        {
            case "project-company":
            case "owner-company":
                db.Add(new AdvanceRequest
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WorkspaceId,
                    ProjectId = reference == "project-company" ? OtherProjectId : ProjectId,
                    OwnerPersonId = reference == "owner-company" ? Guid.Parse("72000000-0000-0000-0000-000000000041") : AuthorPersonId,
                    CreatedBy = AuthorId
                });
                break;
            case "line-revision":
                var line = f.Line; line.Id = Guid.NewGuid(); line.LineOrder = 2; line.ProjectId = otherProject; db.Add(line); break;
            case "step-revision":
                var step = f.Step; step.Id = Guid.NewGuid(); step.StepOrder = 2; step.ProjectId = otherProject; db.Add(step); break;
            case "participant-request":
                db.Add(new AdvanceRequestParticipant
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = WorkspaceId,
                    ProjectId = otherProject,
                    RequestId = f.Request.Id,
                    PersonId = ReviewerPersonId,
                    Role = "Approver",
                    CreatedBy = AuthorId
                }); break;
            default:
                var revision = f.NewRevision(2, false);
                if (reference == "revision-request") revision.ProjectId = otherProject;
                if (reference == "policy-company") revision.CompanyPolicyRevisionId = ProjectRevisionId;
                if (reference == "policy-project")
                {
                    var request = new AdvanceRequest
                    {
                        Id = Guid.NewGuid(),
                        WorkspaceId = WorkspaceId,
                        ProjectId = otherProject,
                        OwnerPersonId = AuthorPersonId,
                        CreatedBy = AuthorId
                    };
                    db.Add(request); revision.RequestId = request.Id; revision.ProjectId = otherProject;
                }
                db.Add(revision); break;
        }
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23503", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("hash")]
    [InlineData("policy")]
    [InlineData("total")]
    [InlineData("unsubmit")]
    [InlineData("line")]
    [InlineData("insert-line")]
    [InlineData("delete-line")]
    [InlineData("move-line")]
    [InlineData("step-role")]
    [InlineData("insert-step")]
    public async Task Submitted_content_is_immutable(string mutation)
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync(submitted: true);
        await using var db = f.CreateContext();
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync();
        switch (mutation)
        {
            case "content": revision.ContentJson = "{}"; break;
            case "hash": revision.ContentHash = new('c', 64); break;
            case "policy": revision.PolicySnapshotJson = "{\"ClearingDays\":99}"; break;
            case "total": revision.Total = 99; break;
            case "unsubmit": revision.SubmittedAtUtc = null; revision.ConfirmedByPersonId = null; break;
            case "line": (await db.Set<AdvanceRequestLine>().SingleAsync()).Amount = 99; break;
            case "delete-line": db.Remove(await db.Set<AdvanceRequestLine>().SingleAsync()); break;
            case "move-line":
                var next = f.NewRevision(2, false); db.Add(next);
                (await db.Set<AdvanceRequestLine>().SingleAsync()).RevisionId = next.Id; break;
            case "insert-line": f.Line.Id = Guid.NewGuid(); f.Line.LineOrder = 2; db.Add(f.Line); break;
            case "step-role": (await db.Set<AdvanceApprovalStep>().SingleAsync()).LevelCode = "Other"; break;
            case "insert-step": f.Step.Id = Guid.NewGuid(); f.Step.StepOrder = 2; db.Add(f.Step); break;
        }
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("owner")]
    [InlineData("delegate")]
    [InlineData("participant-update")]
    [InlineData("participant-delete")]
    [InlineData("request-delete")]
    [InlineData("revision-delete")]
    public async Task Identity_and_history_cannot_be_rewritten(string mutation)
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var db = f.CreateContext();
        var request = await db.Set<AdvanceRequest>().SingleAsync();
        switch (mutation)
        {
            case "project":
                var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                    "UPDATE field_advance_requests SET project_id='71000000-0000-0000-0000-000000000012'"));
                Assert.Equal("23514", error.SqlState); return;
            case "owner": request.OwnerPersonId = ReviewerPersonId; break;
            case "delegate": request.DelegatePersonId = ReviewerPersonId; break;
            case "participant-update": (await db.Set<AdvanceRequestParticipant>().SingleAsync()).PersonId = ReviewerPersonId; break;
            case "participant-delete": db.Remove(await db.Set<AdvanceRequestParticipant>().SingleAsync()); break;
            case "request-delete": db.Remove(request); break;
            case "revision-delete": db.Remove(await db.Set<AdvanceRequestRevision>().SingleAsync()); break;
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Draft_can_change_and_reassignment_does_not_change_submitted_snapshots()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var db = f.CreateContext();
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync();
        revision.ContentJson = "{\"Purpose\":\"revised trip\"}";
        revision.SubmittedAtUtc = DateTimeOffset.UtcNow; revision.ConfirmedByPersonId = AuthorPersonId;
        await db.SaveChangesAsync();
        await db.Entry(revision).ReloadAsync();
        var before = revision.ContentJson;
        var step = await db.Set<AdvanceApprovalStep>().SingleAsync();
        step.AssigneePersonId = RecipientPersonId; step.AssignmentReason = "Reassigned";
        await db.SaveChangesAsync();
        await db.Entry(revision).ReloadAsync();
        Assert.Equal(before, revision.ContentJson);
        Assert.Equal("{\"ClearingDays\": 7}", revision.PolicySnapshotJson);
        Assert.Equal(RecipientPersonId, (await db.Set<AdvanceApprovalStep>().AsNoTracking().SingleAsync()).AssigneePersonId);
        revision.IsCurrent = false; await db.SaveChangesAsync();
        db.Add(f.NewRevision(2)); await db.SaveChangesAsync();
        Assert.Equal(2, await db.Set<AdvanceRequestRevision>().CountAsync());
        Assert.Single(await db.Set<AdvanceRequestRevision>().Where(x => x.IsCurrent).ToArrayAsync());
    }
    [Fact]
    public async Task Empty_database_has_request_schema()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await using var db = f.CreateContext();
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
        foreach (var table in FieldAdvanceRequestFixture.Tables) Assert.Contains(table, tables);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Step_order_and_successful_person_are_unique_per_revision()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var db = f.CreateContext();
        var duplicate = new AdvanceApprovalStep
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = f.Request.Id,
            RevisionId = f.Revision.Id,
            StepOrder = 1,
            LevelCode = "Manager"
        };
        db.Add(duplicate);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(error.InnerException).SqlState);
        duplicate.StepOrder = 2; await db.SaveChangesAsync();
        var revision = await db.Set<AdvanceRequestRevision>().SingleAsync();
        revision.SubmittedAtUtc = DateTimeOffset.UtcNow; revision.ConfirmedByPersonId = AuthorPersonId;
        await db.SaveChangesAsync();
        var first = await db.Set<AdvanceApprovalStep>().SingleAsync(x => x.StepOrder == 1);
        Decide(first); await db.SaveChangesAsync();
        Decide(duplicate);
        error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.Entry(duplicate).State = EntityState.Detached;
        first.DecisionReason = "rewritten";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static void Decide(AdvanceApprovalStep step)
    {
        step.Outcome = "Approved"; step.DecidedByPersonId = ReviewerPersonId;
        step.DecidedBy = ReviewerId; step.DecidedAtUtc = DateTimeOffset.UtcNow; step.DecisionReason = "Approved after review";
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("truncate")]
    public async Task Event_history_is_append_only(string mutation)
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync(true);
        await using var db = f.CreateContext();
        var receipt = f.NewReceipt(); var history = f.NewEvent(receipt.CommandId);
        db.AddRange(receipt, history); await db.SaveChangesAsync();
        if (mutation == "truncate")
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE field_advance_request_events"));
            Assert.Equal("23514", error.SqlState);
        }
        else
        {
            if (mutation == "delete") db.Remove(history); else history.Reason = "Rewritten";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using var read = f.CreateContext();
        Assert.Equal("Reviewed", (await read.Set<AdvanceRequestEvent>().SingleAsync()).Reason);
    }

    [Theory]
    [InlineData("step")]
    [InlineData("revision")]
    [InlineData("receipt")]
    [InlineData("actor")]
    public async Task Event_references_are_scoped(string reference)
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var db = f.CreateContext();
        var receipt = f.NewReceipt();
        if (reference == "receipt") receipt.WorkspaceId = OtherWorkspaceId;
        db.Add(receipt); await db.SaveChangesAsync();
        var history = f.NewEvent(receipt.CommandId);
        if (reference == "actor") history.ActorPersonId = Guid.Parse("72000000-0000-0000-0000-000000000041");
        if (reference == "revision") history.ProjectId = Guid.Parse("71000000-0000-0000-0000-000000000012");
        if (reference == "step")
        {
            var revision = f.NewRevision(2, false); db.Add(revision); await db.SaveChangesAsync();
            history.RevisionId = revision.Id; // Both revisions exist; the step belongs to the first.
        }
        db.Add(history);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23503", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("step")]
    public async Task Stale_snapshot_cannot_edit_content_after_concurrent_submission(string child)
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var stale = f.CreateContext();
        await using var transaction = await stale.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        Assert.Null((await stale.Set<AdvanceRequestRevision>().SingleAsync()).SubmittedAtUtc);
        await using (var submit = f.CreateContext())
        {
            var revision = await submit.Set<AdvanceRequestRevision>().SingleAsync();
            revision.SubmittedAtUtc = DateTimeOffset.UtcNow; revision.ConfirmedByPersonId = AuthorPersonId;
            await submit.SaveChangesAsync();
        }
        if (child == "line") (await stale.Set<AdvanceRequestLine>().SingleAsync()).Amount = 99;
        else (await stale.Set<AdvanceApprovalStep>().SingleAsync()).LevelCode = "Other";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => stale.SaveChangesAsync());
        var update = Assert.IsType<DbUpdateException>(error.InnerException);
        Assert.Equal("40001", Assert.IsType<PostgresException>(update.InnerException).SqlState);
        await transaction.RollbackAsync();
        await using var read = f.CreateContext();
        Assert.Equal(12.35m, (await read.Set<AdvanceRequestLine>().SingleAsync()).Amount);
        Assert.Equal("Supervisor", (await read.Set<AdvanceApprovalStep>().SingleAsync()).LevelCode);
    }

    [Fact]
    public async Task Failure_rolls_back_result_assignee_participation_audit_and_existing_receipt_together()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync(true);
        await using (var db = f.CreateContext())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var request = await db.Set<AdvanceRequest>().SingleAsync(); request.Version++; request.State = "Approved";
            var step = await db.Set<AdvanceApprovalStep>().SingleAsync(); step.AssigneePersonId = RecipientPersonId; Decide(step);
            var receipt = f.NewReceipt();
            db.AddRange(receipt, f.NewEvent(receipt.CommandId), new AdvanceRequestParticipant
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                RequestId = request.Id,
                PersonId = ReviewerPersonId,
                Role = "Approver",
                CreatedBy = ReviewerId
            }, new AuditEvent
            {
                Id = Guid.NewGuid(),
                EventType = "request.approved",
                OccurredAtUtc = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid().ToString(),
                ActorId = ReviewerId,
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                TargetId = request.Id
            });
            await db.SaveChangesAsync();
            // Fail after the first batch reached PostgreSQL, then roll back the whole command.
            (await db.Set<AdvanceRequestRevision>().SingleAsync()).ContentJson = "{}";
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            await transaction.RollbackAsync();
        }
        await using var read = f.CreateContext();
        var retained = await read.Set<AdvanceRequest>().SingleAsync(); Assert.Equal(1L, retained.Version); Assert.Equal("Draft", retained.State);
        var retainedStep = await read.Set<AdvanceApprovalStep>().SingleAsync(); Assert.Null(retainedStep.Outcome);
        Assert.Equal(ReviewerPersonId, retainedStep.AssigneePersonId);
        Assert.Empty(await read.Set<AdvanceRequestEvent>().ToArrayAsync());
        Assert.Empty(await read.Set<FinanceCommandReceipt>().ToArrayAsync());
        Assert.Empty(await read.AuditEvents.ToArrayAsync());
        Assert.Equal("Owner", (await read.Set<AdvanceRequestParticipant>().SingleAsync()).Role);
    }

    [Fact]
    public async Task Empty_schema_can_roundtrip_but_downgrade_cannot_discard_requests()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await using var db = f.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(FieldAdvanceRequestFixture.PreviousMigration);
        await db.Database.MigrateAsync();
        await f.SeedRequestAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(FieldAdvanceRequestFixture.PreviousMigration));
        Assert.Equal("23514", error.SqlState);
        Assert.Equal(1, await db.Set<AdvanceRequest>().CountAsync());
        Assert.Equal(2, await db.Set<PolicyRevision>().CountAsync());
    }

    [Fact]
    public async Task Stored_quantities_money_and_aggregate_version_roundtrip_exactly()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var db = f.CreateContext();
        var line = await db.Set<AdvanceRequestLine>().SingleAsync();
        Assert.Equal(1.2345m, line.Quantity); Assert.Equal(12.35m, line.Amount);
        line.Quantity = 9999999999999999.9999m; line.Amount = 9999999999999999.99m;
        var request = await db.Set<AdvanceRequest>().SingleAsync(); request.Version = 2147483648L;
        await db.SaveChangesAsync(); await db.Entry(line).ReloadAsync(); await db.Entry(request).ReloadAsync();
        Assert.Equal(9999999999999999.9999m, line.Quantity); Assert.Equal(9999999999999999.99m, line.Amount);
        Assert.Equal(2147483648L, request.Version);
    }

    [Fact]
    public async Task Aggregate_version_rejects_stale_writer()
    {
        await using var f = await FieldAdvanceRequestFixture.CreateAsync(postgres);
        await f.SeedRequestAsync();
        await using var first = f.CreateContext(); await using var second = f.CreateContext();
        var a = await first.Set<AdvanceRequest>().SingleAsync(); var b = await second.Set<AdvanceRequest>().SingleAsync();
        a.Version++; await first.SaveChangesAsync(); b.Version++; b.State = "Withdrawn";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Migration_preserves_7A()
    {
        await using var database = new IdentityDatabase(postgres.ConnectionString);
        await database.InitializeAsync();
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(FieldAdvanceRequestFixture.PreviousMigration);
        await FieldAdvanceFixture.SeedDirectoryAsync(db);
        await new FieldAdvanceFixture(database).SeedPoliciesAsync();
        const string snapshot = "SELECT jsonb_agg(to_jsonb(p) ORDER BY id)::text AS \"Value\" FROM field_advance_policy_revisions p";
        var before = await db.Database.SqlQueryRaw<string>(snapshot).SingleAsync();
        await db.Database.MigrateAsync();
        var retainedPolicy = await db.Database.SqlQueryRaw<string>(snapshot).SingleAsync();
        Assert.Equal(before, retainedPolicy);
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), x => x.EndsWith("_AddFieldAdvanceRequests"));
    }
}
