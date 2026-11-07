using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.FieldAdvances.Data;

public static class SettlementModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var round = Row<SettlementRound>(model, "settlement_rounds");
        Link<SettlementRound, SettlementRound>(round, "SourceRoundId");
        round.Property(x => x.RoundKind).HasDefaultValue("Ordinary");
        Link<SettlementRound, SettlementAdjustmentCase>(round, "AdjustmentCaseId");
        Check(round, "kind", "(round_kind='Ordinary' AND source_round_id IS NULL AND adjustment_case_id IS NULL) OR (round_kind='Adjustment' AND source_round_id IS NOT NULL)");
        var revision = Row<SettlementRevision>(model, "settlement_revisions");
        Link<SettlementRevision, SettlementRound>(revision, "RoundId");
        Link<SettlementRevision, SettlementRevision>(revision, "SourceRevisionId");
        revision.HasIndex("RoundId", "RevisionNumber").IsUnique();
        revision.HasIndex("RoundId").IsUnique().HasFilter("accepted_at_utc IS NOT NULL");
        Person(revision, "ConfirmedByPersonId"); Account(revision, "ConfirmedBy");
        Check(revision, "revision", "revision_number>0 AND ((confirmed_by IS NULL AND confirmed_by_person_id IS NULL AND confirmed_at_utc IS NULL) OR (confirmed_by IS NOT NULL AND confirmed_by_person_id IS NOT NULL AND confirmed_at_utc IS NOT NULL))");
        var line = Row<SettlementLine>(model, "settlement_lines");
        Link<SettlementLine, SettlementRevision>(line, "RevisionId");
        Link<SettlementLine, SettlementLine>(line, "SourceLineId");
        line.HasAlternateKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId", "RevisionId", "Id");
        Check(line, "line", "(quantity IS NOT NULL AND rate IS NOT NULL AND lump_sum IS NULL AND quantity>=0 AND rate>0 AND amount=round(quantity*rate,2)) OR (quantity IS NULL AND rate IS NULL AND lump_sum IS NOT NULL AND lump_sum>=0 AND amount=lump_sum)");
        var step = Row<SettlementApprovalStep>(model, "settlement_approval_steps");
        Link<SettlementApprovalStep, SettlementRevision>(step, "RevisionId");
        Person(step, "AssigneePersonId");
        step.HasIndex("RevisionId", "StepOrder").IsUnique();
        step.HasIndex("RevisionId", "ReviewedByPersonId").IsUnique();
        Check(step, "step", "(step_order=1 AND level_code='Supervisor') OR (step_order=2 AND level_code='ProjectManager') OR (step_order=3 AND level_code='Finance')");
        var doc = Row<ExpenseDocument>(model, "expense_documents");
        Link<ExpenseDocument, AdvanceEvidence>(doc, "EvidenceId");
        Check(doc, "document", "length(btrim(issuer))>0 AND ((has_no_number AND document_number IS NULL) OR (NOT has_no_number AND document_number IS NOT NULL AND length(btrim(document_number))>0))");
        doc.HasIndex("WorkspaceId", "Fingerprint");
        var allocation = Row<ExpenseAllocation>(model, "expense_allocations");
        Link<ExpenseAllocation, ExpenseDocument>(allocation, "DocumentId");
        Link<ExpenseAllocation, SettlementRevision>(allocation, "RevisionId");
        allocation.HasOne<SettlementLine>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId", "RevisionId", "LineId")
            .HasPrincipalKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId", "RevisionId", "Id").OnDelete(DeleteBehavior.Restrict);
        allocation.HasIndex("DocumentId", "LineId").IsUnique();
        var duplicate = Row<ExpenseDuplicateCase>(model, "expense_duplicate_cases");
        Link<ExpenseDuplicateCase, ExpenseDocument>(duplicate, "DocumentId");
        duplicate.HasOne<ExpenseDocument>().WithMany().HasForeignKey("OtherWorkspaceId", "OtherProjectId", "OtherFamilyId", "OtherRequestId", "OtherDocumentId")
            .HasPrincipalKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId", "Id").OnDelete(DeleteBehavior.Restrict);
        duplicate.HasIndex("DocumentId", "OtherDocumentId", "DocumentVersion", "OtherDocumentVersion").IsUnique();
        Check(duplicate, "duplicate", "document_id<>other_document_id AND document_version>0 AND other_document_version>0");
        var completion = Row<WorkCompletion>(model, "work_completions");
        Link<WorkCompletion, WorkCompletion>(completion, "SupersedesId");
        Check(completion, "due", "clearing_days>=0 AND clearing_due_on=completed_on+clearing_days AND refund_due_on=clearing_due_on");
        var declaration = Row<SubmissionDeclaration>(model, "submission_declarations");
        Link<SubmissionDeclaration, WorkCompletion>(declaration, "WorkCompletionId");
        Link<SubmissionDeclaration, SubmissionDeclaration>(declaration, "SupersedesId");
        var refund = Row<Refund>(model, "refunds");
        Link<Refund, AdvanceEvidence>(refund, "EvidenceId");
        Link<Refund, SettlementAdjustmentCase>(refund, "AdjustmentCaseId");
        Check(refund, "refund", "amount>0 AND channel IN ('Cash','Transfer')");
        var correction = Row<RefundCorrection>(model, "refund_corrections");
        Link<RefundCorrection, Refund>(correction, "RefundId");
        Link<RefundCorrection, AdvanceEvidence>(correction, "EvidenceId");
        Link<RefundCorrection, SettlementAdjustmentCase>(correction, "AdjustmentCaseId");
        Link<RefundCorrection, RefundCorrection>(correction, "SupersedesId");
        correction.HasIndex("SupersedesId").IsUnique();
        Adjustment(correction, "RefundId");
        var expense = Row<SettlementExpenseAdjustment>(model, "settlement_expense_adjustments");
        Link<SettlementExpenseAdjustment, SettlementAdjustmentCase>(expense, "AdjustmentCaseId");
        Link<SettlementExpenseAdjustment, SettlementRevision>(expense, "SourceRevisionId");
        Link<SettlementExpenseAdjustment, SettlementRevision>(expense, "RevisionId");
        expense.HasIndex("RevisionId").IsUnique().HasFilter("accepted_at_utc IS NOT NULL");
        Adjustment(expense, "SourceRevisionId");
        var adjustment = Row<SettlementAdjustmentCase>(model, "settlement_adjustment_cases");
        Link<SettlementAdjustmentCase, ClosureRecord>(adjustment, "ClosureRecordId");
        Link<SettlementAdjustmentCase, AdvanceEvidence>(adjustment, "ExternalEvidenceId");
        Link<SettlementAdjustmentCase, Refund>(adjustment, "RootRefundId");
        Link<SettlementAdjustmentCase, SettlementRound>(adjustment, "RootExpenseRoundId");
        Check(adjustment, "baseline", "(root_refund_id IS NULL AND root_expense_round_id IS NULL AND baseline_version IS NULL AND baseline_amount IS NULL) OR (baseline_version IS NOT NULL AND baseline_version>0 AND baseline_amount IS NOT NULL AND ((root_refund_id IS NOT NULL AND root_expense_round_id IS NULL AND kind='RefundCorrection') OR (root_expense_round_id IS NOT NULL AND root_refund_id IS NULL AND kind='ExpenseAdjustment')))");
        var casePayment = Row<SettlementCasePayment>(model, "settlement_case_payments");
        Link<SettlementCasePayment, SettlementAdjustmentCase>(casePayment, "CaseId");
        Link<SettlementCasePayment, AdvanceEvidence>(casePayment, "EvidenceId");
        Link<SettlementCasePayment, SettlementCasePayment>(casePayment, "SupersedesId");
        casePayment.HasIndex("SupersedesId").IsUnique();
        Check(casePayment, "amount_positive", "amount>0");
        var caseClosure = Row<SettlementCaseClosure>(model, "settlement_case_closures");
        Link<SettlementCaseClosure, SettlementAdjustmentCase>(caseClosure, "CaseId");
        caseClosure.HasIndex("CaseId").IsUnique();
        Check(caseClosure, "settled", "outstanding=0 AND accepted_at_utc IS NOT NULL");
        var excess = Row<SettlementExcessCase>(model, "settlement_excess_cases");
        Link<SettlementExcessCase, ExpenseDocument>(excess, "DocumentId");
        Link<SettlementExcessCase, SettlementRevision>(excess, "SourceRevisionId");
        excess.HasIndex("DocumentId").IsUnique().HasFilter("state<>'Resolved'");
        Check(excess, "amount_positive", "amount>0 AND claimed_amount>cap_amount AND amount=claimed_amount-cap_amount");
        var excessPayment = Row<SettlementExcessPayment>(model, "settlement_excess_payments");
        Link<SettlementExcessPayment, SettlementExcessCase>(excessPayment, "ExcessId");
        Link<SettlementExcessPayment, AdvanceEvidence>(excessPayment, "EvidenceId");
        Link<SettlementExcessPayment, SettlementExcessPayment>(excessPayment, "SupersedesId");
        Person(excessPayment, "RecipientPersonId");
        excessPayment.HasIndex("SupersedesId").IsUnique();
        Check(excessPayment, "amount_positive", "amount>0");
        var disposition = Row<SettlementExcessDisposition>(model, "settlement_excess_dispositions");
        Link<SettlementExcessDisposition, SettlementExcessCase>(disposition, "ExcessId");
        Link<SettlementExcessDisposition, AdvanceEvidence>(disposition, "EvidenceId");
        Check(disposition, "code", "code IN ('AwaitingExternalReview','NoAdditionalPaymentRequired','FinancialSettlementRecorded')");
        var incident = Row<PaymentIncidentResolution>(model, "payment_incident_resolutions");
        incident.HasOne<AdvancePaymentAttempt>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId", "InstallmentId", "AttemptId")
            .HasPrincipalKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId", "InstallmentId", "Id").OnDelete(DeleteBehavior.Restrict);
        // A mismatch hold is family-scoped in 7C; do not invent a request-scoped hold.
        model.Entity<AdvancePaymentHold>().HasAlternateKey("WorkspaceId", "ProjectId", "FamilyId", "Id");
        incident.HasOne<AdvancePaymentHold>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "FamilyId", "HoldId")
            .HasPrincipalKey("WorkspaceId", "ProjectId", "FamilyId", "Id").OnDelete(DeleteBehavior.Restrict);
        Link<PaymentIncidentResolution, AdvanceEvidence>(incident, "EvidenceId");
        Person(incident, "WrongRecipientPersonId");
        incident.HasIndex("AttemptId").IsUnique().HasFilter("accepted_at_utc IS NOT NULL");
        Check(incident, "recipient", "wrong_recipient_received=0 OR wrong_recipient_person_id IS NOT NULL");
        var recovery = Row<PaymentIncidentRecovery>(model, "payment_incident_recoveries");
        Link<PaymentIncidentRecovery, PaymentIncidentResolution>(recovery, "ResolutionId");
        Link<PaymentIncidentRecovery, AdvanceEvidence>(recovery, "EvidenceId");
        Person(recovery, "ReturnedByPersonId");
        Check(recovery, "recovery", "amount>0 AND return_kind IN ('Owner','WrongRecipient')");
        var closure = Row<ClosureRecord>(model, "closure_records");
        closure.Property(x => x.ActorKind).HasDefaultValue("Human");
        closure.HasIndex("RequestId").IsUnique();
        Link<ClosureRecord, WorkCompletion>(closure, "WorkCompletionId");
        Link<ClosureRecord, SubmissionDeclaration>(closure, "DeclarationId");
        Check(closure, "closure", "request_version>0 AND family_version>0 AND accepted_at_utc IS NOT NULL");

        foreach (var type in model.Model.GetEntityTypes().Where(x => x.ClrType.Namespace == typeof(SettlementRound).Namespace
            && x.FindProperty("AcceptedAtUtc") is not null))
        {
            var index = 0;
            foreach (var fk in type.GetForeignKeys()) fk.SetConstraintName($"fk_st_{type.ClrType.Name}_{index++}");
        }
    }

    private static EntityTypeBuilder<T> Row<T>(ModelBuilder model, string suffix) where T : class
    {
        var e = model.Entity<T>(); e.ToTable("field_advance_" + suffix); e.HasKey("Id");
        e.HasAlternateKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId", "Id");
        e.HasOne<AdvanceRequest>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId")
            .HasPrincipalKey("WorkspaceId", "ProjectId", "FamilyId", "Id").OnDelete(DeleteBehavior.Restrict);
        e.HasOne<FinanceCommandReceipt>().WithMany().HasForeignKey("WorkspaceId", "CommandId")
            .HasPrincipalKey("WorkspaceId", "CommandId").OnDelete(DeleteBehavior.Restrict);
        // A command can create several lines/allocations/steps/duplicate pairs; accepted applications remain unique.
        if (typeof(T) != typeof(ExpenseDuplicateCase) && typeof(T) != typeof(SettlementLine) && typeof(T) != typeof(ExpenseAllocation) && typeof(T) != typeof(SettlementApprovalStep))
            e.HasIndex("WorkspaceId", "CommandId").IsUnique();
        Account(e, "ProposedBy"); Person(e, "ProposedByPersonId"); Account(e, "ReviewedBy"); Person(e, "ReviewedByPersonId");
        foreach (var p in e.Metadata.GetProperties().ToArray())
        {
            var c = Regex.Replace(p.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant(); p.SetColumnName(c);
            var t = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
            if (t == typeof(decimal))
            {
                e.Property(p.Name).HasColumnType("numeric");
                var quantity = p.Name == "Quantity";
                var max = quantity ? "9999999999999999.9999" : "9999999999999999.99";
                Check(e, c, $"{c}>={(p.Name == "Delta" ? "-" + max : "0")} AND {c}<={max} AND {c}=trunc({c},{(quantity ? 4 : 2)})");
            }
            if (p.Name.EndsWith("Json", StringComparison.Ordinal))
            {
                e.Property(p.Name).HasColumnType("jsonb"); Check(e, c, $"jsonb_typeof({c})='object'");
            }
            if (t == typeof(DateOnly) || t == typeof(DateTimeOffset)) Check(e, c, $"isfinite({c})");
        }
        e.Property("CreatedAtUtc").HasDefaultValueSql("now()");
        e.Property("Version").IsConcurrencyToken().HasDefaultValue(1L);
        Check(e, "version", "version>0");
        const string humanReview = "((reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL) OR (reviewed_by IS NOT NULL AND reviewed_by_person_id IS NOT NULL AND reviewed_at_utc IS NOT NULL)) AND (accepted_at_utc IS NULL OR (reviewed_at_utc IS NOT NULL AND state='Accepted')) AND (state<>'Accepted' OR accepted_at_utc IS NOT NULL)";
        Check(e, "review", typeof(T) == typeof(ClosureRecord)
            ? "(actor_kind='Human' AND command_id IS NOT NULL AND proposed_by IS NOT NULL AND proposed_by_person_id IS NOT NULL AND (" + humanReview + ")) OR (actor_kind='System' AND command_id IS NULL AND proposed_by IS NULL AND proposed_by_person_id IS NULL AND reviewed_by IS NULL AND reviewed_by_person_id IS NULL AND reviewed_at_utc IS NULL AND accepted_at_utc IS NOT NULL AND state='Accepted')"
            : humanReview);
        return e;
    }
    private static void Adjustment<T>(EntityTypeBuilder<T> e, string source) where T : class
    {
        e.HasIndex(source, "BaseVersion").IsUnique().HasFilter("accepted_at_utc IS NOT NULL");
        Check(e, "adjustment", "base_version>0 AND after_amount=before_amount+delta");
    }
    private static void Link<T, P>(EntityTypeBuilder<T> e, string property) where T : class where P : class =>
        e.HasOne<P>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId", property)
            .HasPrincipalKey("WorkspaceId", "ProjectId", "FamilyId", "RequestId", "Id").OnDelete(DeleteBehavior.Restrict);
    private static void Person<T>(EntityTypeBuilder<T> e, string property) where T : class =>
        e.HasOne<FinancePerson>().WithMany().HasForeignKey("WorkspaceId", property).HasPrincipalKey("WorkspaceId", "Id").OnDelete(DeleteBehavior.Restrict);
    private static void Account<T>(EntityTypeBuilder<T> e, string property) where T : class =>
        e.HasOne<IdentityUser>().WithMany().HasForeignKey(property).OnDelete(DeleteBehavior.Restrict);
    private static void Check<T>(EntityTypeBuilder<T> e, string name, string sql) where T : class =>
        e.ToTable(t => t.HasCheckConstraint("ck_st_" + typeof(T).Name + "_" + name, sql));
}
