using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.FieldAdvances.Data;

public static class PaymentModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var family = Row<AdvanceFamily>(model, "families");
        family.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.Id });
        family.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.Id, x.OwnerPersonId });
        Person(family, "OwnerPersonId");
        family.HasOne<AdvanceRequest>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.Id, x.PendingSupplementId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var account = Row<AdvancePaymentAccount>(model, "payment_accounts");
        account.HasIndex(x => x.RequestId).IsUnique();
        account.HasOne<AdvanceRequestRevision>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.RequestId, x.ApprovedRevisionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.RequestId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        account.ToTable(t => t.HasCheckConstraint("ck_payment_account_balance",
            "paid+reserved<=spendable AND spendable<=approved AND (paid=0 OR ever_paid) AND ((approved_revision_id IS NULL AND approved=0) OR (approved_revision_id IS NOT NULL AND approved>0))"));

        var installment = Row<AdvanceInstallment>(model, "installments");
        installment.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.Id });
        Account(installment, "CreatedBy");
        installment.ToTable(t => t.HasCheckConstraint("ck_installment_state", "state IN ('Draft','PendingReview','Returned','Reviewed','Paying','OutcomePending','CancelPending','Cancelled','Paid')"));

        var revision = Row<AdvanceInstallmentRevision>(model, "installment_revisions");
        revision.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.InstallmentId, x.Id });
        revision.HasIndex(x => new { x.InstallmentId, x.RevisionNumber }).IsUnique();
        revision.HasIndex(x => x.InstallmentId).IsUnique().HasFilter("is_current");
        Account(revision, "CreatedBy"); Account(revision, "ReviewedBy"); Person(revision, "ReviewedByPersonId");
        revision.HasOne<AdvanceFamily>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RecipientPersonId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.Id, x.OwnerPersonId }).OnDelete(DeleteBehavior.Restrict);
        revision.ToTable(t =>
        {
            t.HasCheckConstraint("ck_installment_revision", "revision_number>0 AND amount>0 AND length(btrim(reason))>0");
            t.HasCheckConstraint("ck_installment_destination", "(channel='Cash' AND bank_code IS NULL AND account_number IS NULL AND account_holder IS NULL) OR (channel='Transfer' AND bank_code IS NOT NULL AND account_number IS NOT NULL AND account_holder IS NOT NULL AND length(btrim(bank_code))>0 AND length(btrim(account_number))>0 AND length(btrim(account_holder))>0)");
            t.HasCheckConstraint("ck_installment_review", ReviewCheck());
        });

        var ledger = Row<AdvanceReservationEntry>(model, "reservation_entries");
        Receipt(ledger);
        ledger.HasIndex(x => new { x.WorkspaceId, x.CommandId, x.Action, x.InstallmentId }).IsUnique();
        ledger.ToTable(t => t.HasCheckConstraint("ck_reservation_entry", "action IN ('Reserve','Release','Consume') AND amount>0"));

        var attempt = Row<AdvancePaymentAttempt>(model, "payment_attempts");
        Receipt(attempt); Account(attempt, "PayerId"); Person(attempt, "PayerPersonId");
        attempt.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.InstallmentId, x.Id });
        attempt.HasOne<AdvanceInstallmentRevision>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.InstallmentId, x.RevisionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.InstallmentId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        attempt.HasIndex(x => new { x.WorkspaceId, x.CommandId }).IsUnique();
        attempt.HasIndex(x => x.InstallmentId).IsUnique().HasFilter("state IN ('Open','Unknown','Mismatch')");
        attempt.ToTable(t => t.HasCheckConstraint("ck_payment_attempt_state", "state IN ('Open','Unknown','Mismatch','Unpaid','Matched')"));

        var result = Row<AdvancePaymentResult>(model, "payment_results");
        Receipt(result); Account(result, "RecordedBy"); Person(result, "RecordedByPersonId"); Person(result, "RecipientPersonId");
        result.HasIndex(x => x.InstallmentId).IsUnique(); result.HasIndex(x => x.AttemptId).IsUnique();
        result.HasIndex(x => new { x.WorkspaceId, x.CommandId }).IsUnique();
        result.HasOne<AdvancePaymentAttempt>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.InstallmentId, x.AttemptId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.InstallmentId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        result.ToTable(t => t.HasCheckConstraint("ck_payment_result", "amount>0 AND isfinite(paid_on)"));

        var proposal = Row<AdvancePaymentProposal>(model, "payment_proposals");
        Receipt(proposal); Account(proposal, "ProposedBy"); Person(proposal, "ProposedByPersonId");
        Account(proposal, "ReviewedBy"); Person(proposal, "ReviewedByPersonId");
        proposal.HasIndex(x => new { x.WorkspaceId, x.CommandId }).IsUnique();
        proposal.ToTable(t => t.HasCheckConstraint("ck_payment_proposal", "family_version>0 AND request_version>0 AND (installment_version IS NULL OR installment_version>0) AND state IN ('Pending','Approved','Returned','Rejected') AND length(btrim(kind))>0 AND length(btrim(reason))>0 AND " + ReviewCheck()));

        var participation = Row<AdvancePaymentParticipation>(model, "payment_participations");
        Person(participation, "PersonId"); Account(participation, "CreatedBy");
        participation.HasIndex(x => new { x.FamilyId, x.PersonId, x.Role }).IsUnique();
        participation.ToTable(t => t.HasCheckConstraint("ck_payment_participation", "role IN ('Owner','Delegate','Approver','Checker','Payer')"));

        var hold = Row<AdvancePaymentHold>(model, "payment_holds");
        Account(hold, "CreatedBy"); Person(hold, "CreatedByPersonId"); Person(hold, "ReleasedByPersonId");
        hold.ToTable(t => t.HasCheckConstraint("ck_payment_hold", "length(btrim(code))>0 AND length(btrim(reason))>0 AND (installment_id IS NULL OR request_id IS NOT NULL) AND (code<>'PaymentMismatch' OR (request_id IS NULL AND installment_id IS NULL AND released_at_utc IS NULL)) AND ((released_at_utc IS NULL AND released_by_person_id IS NULL) OR (released_at_utc IS NOT NULL AND isfinite(released_at_utc) AND released_by_person_id IS NOT NULL))"));

        var history = Row<AdvancePaymentEvent>(model, "payment_events");
        Receipt(history); Account(history, "ActorId"); Person(history, "ActorPersonId");
        history.HasIndex(x => new { x.WorkspaceId, x.CommandId }).IsUnique();
        history.ToTable(t => t.HasCheckConstraint("ck_payment_event", "family_version>0 AND request_version>0 AND (installment_version IS NULL OR installment_version>0) AND length(btrim(kind))>0"));

        var evidence = Row<AdvanceEvidence>(model, "evidence");
        Account(evidence, "CreatedBy"); Person(evidence, "CreatedByPersonId");
        evidence.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.Id });
        evidence.ToTable(t => t.HasCheckConstraint("ck_payment_evidence", "length>0 AND length(btrim(content_type))>0 AND state IN ('Pending','Ready','Rejected','Missing')"));
        var location = Row<AdvanceEvidenceLocation>(model, "evidence_locations");
        location.HasOne<AdvanceEvidence>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.EvidenceId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        location.HasIndex(x => x.EvidenceId).IsUnique().HasFilter("is_current");
        location.ToTable(t => t.HasCheckConstraint("ck_evidence_location", "length(btrim(storage_key))>0 AND (NOT is_current OR verified_at_utc IS NOT NULL) AND (verified_at_utc IS NULL OR isfinite(verified_at_utc))"));
        // PostgreSQL identifiers have a 63-byte limit; composite scopes otherwise collide after truncation.
        foreach (var type in new[] { typeof(AdvanceFamily), typeof(AdvancePaymentAccount), typeof(AdvanceInstallment),
            typeof(AdvanceInstallmentRevision), typeof(AdvanceReservationEntry), typeof(AdvancePaymentAttempt),
            typeof(AdvancePaymentResult), typeof(AdvancePaymentProposal), typeof(AdvancePaymentParticipation),
            typeof(AdvancePaymentHold), typeof(AdvancePaymentEvent), typeof(AdvanceEvidence), typeof(AdvanceEvidenceLocation) })
        {
            var index = 0;
            foreach (var foreignKey in model.Entity(type).Metadata.GetForeignKeys())
                foreignKey.SetConstraintName($"fk_pay_{type.Name}_{index++}");
        }
    }

    private static string ReviewCheck() => "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL AND isfinite(reviewed_at_utc)))";

    private static EntityTypeBuilder<T> Row<T>(ModelBuilder model, string suffix) where T : class
    {
        var entity = model.Entity<T>(); entity.ToTable("field_advance_" + suffix); entity.HasKey("Id");
        entity.HasOne<Project>().WithMany().HasForeignKey("WorkspaceId", "ProjectId")
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        if (typeof(T).GetProperty("FamilyId") is not null)
            entity.HasOne<AdvanceFamily>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "FamilyId")
                .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        if (typeof(T).GetProperty("RequestId") is not null)
            entity.HasOne<AdvanceRequest>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId")
                .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        if (typeof(T).GetProperty("InstallmentId") is not null)
            entity.HasOne<AdvanceInstallment>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId", "InstallmentId")
                .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.FamilyId, x.RequestId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        foreach (var property in entity.Metadata.GetProperties().ToArray())
        {
            var column = Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
            property.SetColumnName(column);
            if (property.ClrType == typeof(decimal))
            {
                // Unconstrained numeric lets CHECK reject excess scale instead of PostgreSQL rounding first.
                entity.Property(property.Name).HasColumnType("numeric");
                entity.ToTable(t => t.HasCheckConstraint("ck_" + suffix + "_" + column,
                    $"{column}>=0 AND {column}<=9999999999999999.99 AND {column}=trunc({column},2)"));
            }
            if (property.Name.EndsWith("Json", StringComparison.Ordinal))
            {
                entity.Property(property.Name).HasColumnType("jsonb");
                var kind = property.Name == "EvidenceIdsJson" ? "array" : "object";
                entity.ToTable(t => t.HasCheckConstraint("ck_" + suffix + "_" + column, $"jsonb_typeof({column})='{kind}'"));
            }
            if (property.Name is "ContentHash" or "Sha256")
                entity.ToTable(t => t.HasCheckConstraint("ck_" + suffix + "_hash", $"{column} ~ '^[a-f0-9]{{64}}$'"));
            if (property.Name == "CreatedAtUtc")
            {
                entity.Property(property.Name).HasDefaultValueSql("now()");
                entity.ToTable(t => t.HasCheckConstraint("ck_" + suffix + "_time", "isfinite(created_at_utc)"));
            }
            if (property.Name == "Version")
            {
                entity.Property(property.Name).IsConcurrencyToken().HasDefaultValue(1L);
                entity.ToTable(t => t.HasCheckConstraint("ck_" + suffix + "_version", "version>0"));
            }
        }
        return entity;
    }

    private static void Receipt<T>(EntityTypeBuilder<T> entity) where T : class =>
        entity.HasOne<FinanceCommandReceipt>().WithMany().HasForeignKey("WorkspaceId", "CommandId")
            .HasPrincipalKey(x => new { x.WorkspaceId, x.CommandId }).OnDelete(DeleteBehavior.Restrict);
    private static void Person<T>(EntityTypeBuilder<T> entity, string property) where T : class =>
        entity.HasOne<FinancePerson>().WithMany().HasForeignKey("WorkspaceId", property)
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    private static void Account<T>(EntityTypeBuilder<T> entity, string property) where T : class =>
        entity.HasOne<IdentityUser>().WithMany().HasForeignKey(property).OnDelete(DeleteBehavior.Restrict);
}
