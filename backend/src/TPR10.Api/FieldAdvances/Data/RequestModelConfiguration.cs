using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.FieldAdvances.Data;

public static class RequestModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var request = Row<AdvanceRequest>(model, "field_advance_requests");
        request.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.Id });
        request.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.Id });
        request.Property(x => x.FamilyId).HasDefaultValueSql("gen_random_uuid()");
        request.Property(x => x.RelationKind).HasMaxLength(30).HasDefaultValue("Normal");
        request.HasOne<AdvanceFamily>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.OwnerPersonId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.Id, x.OwnerPersonId }).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<AdvanceRequest>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.SupplementOfRequestId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.Id }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_request_supplement_family");
        request.HasIndex(x => x.FamilyId).IsUnique().HasFilter("relation_kind='Normal'");
        request.HasOne<AdvanceRequest>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.SourceRequestId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        Person(request, "OwnerPersonId"); Person(request, "DelegatePersonId"); Account(request, "CreatedBy");
        request.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);
        request.Property(x => x.State).HasMaxLength(40);
        request.Property(x => x.HoldCode).HasMaxLength(100);
        request.ToTable(t =>
        {
            t.HasCheckConstraint("ck_advance_request_version", "version>=1");
            t.HasCheckConstraint("ck_advance_request_source", "source_request_id IS NULL OR source_request_id<>id");
            t.HasCheckConstraint("ck_advance_request_relation", "(relation_kind='Normal' AND supplement_of_request_id IS NULL) OR (relation_kind='Supplement' AND supplement_of_request_id IS NOT NULL AND supplement_of_request_id<>id AND source_request_id IS NULL)");
        });

        var revision = Row<AdvanceRequestRevision>(model, "field_advance_request_revisions");
        Request(revision);
        revision.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.RequestId, x.Id });
        revision.HasIndex(x => new { x.RequestId, x.RevisionNumber }).IsUnique();
        revision.HasIndex(x => x.RequestId).IsUnique().HasFilter("is_current");
        revision.Property(x => x.ContentJson).HasColumnType("jsonb");
        revision.Property(x => x.ContentHash).HasMaxLength(64);
        revision.Property(x => x.PolicySnapshotJson).HasColumnType("jsonb");
        revision.Property(x => x.PolicyHash).HasMaxLength(64);
        revision.Property(x => x.Total).HasPrecision(18, 2);
        revision.Property(x => x.ApprovalBasisAmount).HasColumnType("numeric").HasDefaultValue(0m).ValueGeneratedOnAddOrUpdate();
        revision.Property(x => x.ApprovalBasisJson).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").ValueGeneratedOnAddOrUpdate();
        foreach (var name in new[] { nameof(AdvanceRequestRevision.ApprovalBasisAmount), nameof(AdvanceRequestRevision.ApprovalBasisJson) })
        {
            revision.Property(name).Metadata.SetBeforeSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Save);
            revision.Property(name).Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Save);
        }
        revision.Property<Guid>("CompanyScopeProjectId").HasDefaultValue(Guid.Empty);
        revision.HasOne<PolicyRevision>().WithMany().HasForeignKey("WorkspaceId", "CompanyScopeProjectId", "CompanyPolicyRevisionId")
            .HasPrincipalKey("WorkspaceId", "ScopeProjectId", "Id").OnDelete(DeleteBehavior.Restrict);
        revision.HasOne<PolicyRevision>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "ProjectPolicyRevisionId")
            .HasPrincipalKey("WorkspaceId", "ScopeProjectId", "Id").OnDelete(DeleteBehavior.Restrict);
        Account(revision, "CreatedBy"); Person(revision, "CreatedByPersonId"); Person(revision, "ConfirmedByPersonId");
        revision.ToTable(t =>
        {
            t.HasCheckConstraint("ck_advance_revision_content", "revision_number>0 AND total>=0 AND jsonb_typeof(content_json)='object' AND content_hash ~ '^[a-f0-9]{64}$'");
            t.HasCheckConstraint("ck_advance_revision_company_scope", "company_scope_project_id='00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_advance_revision_basis", "approval_basis_amount>=total AND approval_basis_amount<=9999999999999999.99 AND approval_basis_amount=trunc(approval_basis_amount,2) AND jsonb_typeof(approval_basis_json)='object' AND jsonb_typeof(approval_basis_json->'Items')='array' AND jsonb_typeof(approval_basis_json->'Amount')='number' AND (approval_basis_json->>'Amount')::numeric=approval_basis_amount AND approval_basis_json ?& ARRAY['Amount','Items']");
            t.HasCheckConstraint("ck_advance_revision_policy", "(policy_snapshot_json IS NULL AND policy_hash IS NULL) OR (policy_snapshot_json IS NOT NULL AND policy_hash IS NOT NULL AND jsonb_typeof(policy_snapshot_json)='object' AND policy_hash ~ '^[a-f0-9]{64}$')");
            t.HasCheckConstraint("ck_advance_revision_submission", "(submitted_at_utc IS NULL AND confirmed_by_person_id IS NULL) OR (submitted_at_utc IS NOT NULL AND isfinite(submitted_at_utc) AND confirmed_by_person_id IS NOT NULL AND company_policy_revision_id IS NOT NULL AND policy_snapshot_json IS NOT NULL AND total>0)");
        });

        var line = Row<AdvanceRequestLine>(model, "field_advance_request_lines");
        Revision(line);
        line.HasIndex(x => new { x.RevisionId, x.LineOrder }).IsUnique();
        line.Property(x => x.Quantity).HasPrecision(20, 4);
        line.Property(x => x.UnitRate).HasPrecision(18, 2);
        line.Property(x => x.Amount).HasPrecision(18, 2);
        line.Property(x => x.CategoryName).HasMaxLength(200);
        line.Property(x => x.Unit).HasMaxLength(100);
        line.Property(x => x.Mode).HasMaxLength(30);
        line.ToTable(t => t.HasCheckConstraint("ck_advance_line_values", "line_order>0 AND amount>=0 AND ((mode='QuantityRate' AND quantity IS NOT NULL AND quantity>=0 AND unit_rate IS NOT NULL AND unit_rate>=0) OR (mode='LumpSum' AND quantity IS NULL AND unit_rate IS NULL))"));

        var step = Row<AdvanceApprovalStep>(model, "field_advance_approval_steps");
        Revision(step);
        step.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.RequestId, x.RevisionId, x.Id });
        step.HasIndex(x => new { x.RevisionId, x.StepOrder }).IsUnique();
        step.HasIndex(x => new { x.RevisionId, x.DecidedByPersonId }).IsUnique().HasFilter("outcome='Approved'");
        Person(step, "AssigneePersonId"); Person(step, "DecidedByPersonId"); Account(step, "DecidedBy");
        step.Property(x => x.LevelCode).HasMaxLength(64);
        step.Property(x => x.Outcome).HasMaxLength(30);
        step.Property(x => x.HoldCode).HasMaxLength(100);
        step.Property(x => x.AssignmentReason).HasMaxLength(2000);
        step.Property(x => x.DecisionReason).HasMaxLength(2000);
        step.ToTable(t =>
        {
            t.HasCheckConstraint("ck_advance_step_order", "step_order>0 AND length(btrim(level_code))>0");
            t.HasCheckConstraint("ck_advance_step_decision", "(outcome IS NULL AND decided_by_person_id IS NULL AND decided_by IS NULL AND decided_at_utc IS NULL AND decision_reason IS NULL) OR (outcome IS NOT NULL AND outcome IN ('Approved','Returned','Rejected') AND decided_by_person_id IS NOT NULL AND decided_by IS NOT NULL AND decided_at_utc IS NOT NULL AND isfinite(decided_at_utc) AND decision_reason IS NOT NULL AND length(btrim(decision_reason)) BETWEEN 1 AND 2000)");
        });

        var history = Row<AdvanceRequestEvent>(model, "field_advance_request_events");
        Revision(history);
        history.HasOne<AdvanceApprovalStep>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.RequestId, x.RevisionId, x.StepId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.RequestId, x.RevisionId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        history.HasOne<FinanceCommandReceipt>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CommandId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.CommandId }).OnDelete(DeleteBehavior.Restrict);
        Person(history, "ActorPersonId"); Account(history, "ActorId");
        history.Property(x => x.DataJson).HasColumnType("jsonb");
        history.Property(x => x.Kind).HasMaxLength(100);
        history.Property(x => x.Reason).HasMaxLength(2000);
        history.ToTable(t => t.HasCheckConstraint("ck_advance_event_data", "aggregate_version>=1 AND jsonb_typeof(data_json)='object' AND length(btrim(kind))>0"));

        var participant = Row<AdvanceRequestParticipant>(model, "field_advance_request_participants");
        Request(participant); Person(participant, "PersonId"); Account(participant, "CreatedBy");
        participant.Property(x => x.Role).HasMaxLength(30);
        participant.HasIndex(x => new { x.RequestId, x.PersonId, x.Role }).IsUnique();
        participant.ToTable(t => t.HasCheckConstraint("ck_advance_participant_role", "role IN ('Owner','Delegate','Approver','Checker','Payer')"));

        foreach (var type in new[] { typeof(AdvanceRequest), typeof(AdvanceRequestRevision), typeof(AdvanceRequestLine),
            typeof(AdvanceApprovalStep), typeof(AdvanceRequestEvent), typeof(AdvanceRequestParticipant) })
            foreach (var property in model.Entity(type).Metadata.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }

    private static EntityTypeBuilder<T> Row<T>(ModelBuilder model, string table) where T : class
    {
        var entity = model.Entity<T>(); entity.ToTable(table); entity.HasKey("Id");
        entity.HasOne<Project>().WithMany().HasForeignKey("WorkspaceId", "ProjectId")
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        if (typeof(T).GetProperty("CreatedAtUtc") is not null)
        {
            entity.Property<DateTimeOffset>("CreatedAtUtc").HasDefaultValueSql("now()");
            entity.ToTable(t => t.HasCheckConstraint("ck_" + table + "_time", "isfinite(created_at_utc)"));
        }
        return entity;
    }
    private static void Request<T>(EntityTypeBuilder<T> entity) where T : class =>
        entity.HasOne<AdvanceRequest>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "RequestId")
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    private static void Revision<T>(EntityTypeBuilder<T> entity) where T : class =>
        entity.HasOne<AdvanceRequestRevision>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "RequestId", "RevisionId")
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.RequestId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    private static void Person<T>(EntityTypeBuilder<T> entity, string property) where T : class =>
        entity.HasOne<FinancePerson>().WithMany().HasForeignKey("WorkspaceId", property)
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    private static void Account<T>(EntityTypeBuilder<T> entity, string property) where T : class =>
        entity.HasOne<IdentityUser>().WithMany().HasForeignKey(property).OnDelete(DeleteBehavior.Restrict);
}
