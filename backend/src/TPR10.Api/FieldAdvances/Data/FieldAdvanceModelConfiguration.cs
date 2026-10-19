using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.FieldAdvances.Data;

public static class FieldAdvanceModelConfiguration
{
    private const string EmptyId = "'00000000-0000-0000-0000-000000000000'::uuid";

    public static void Configure(ModelBuilder model)
    {
        var person = Row<FinancePerson>(model, "finance_persons");
        person.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        Accounts(person, "CreatedBy");

        var link = Row<FinanceAccountLink>(model, "finance_account_links");
        Accounts(link, "UserId", "CreatedBy", "EndedBy");
        Person(link, "PersonId");
        link.HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique().HasFilter("ended_at_utc IS NULL");
        link.ToTable(t => t.HasCheckConstraint("ck_finance_account_link_end", """
            (ended_at_utc IS NULL AND ended_by IS NULL AND end_reason IS NULL) OR
            (ended_at_utc IS NOT NULL AND ended_by IS NOT NULL AND end_reason IS NOT NULL
             AND isfinite(ended_at_utc) AND ended_at_utc>created_at_utc AND length(btrim(end_reason)) BETWEEN 1 AND 2000)
            """));

        var revision = Scoped<PolicyRevision>(model, "field_advance_policy_revisions");
        revision.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        revision.HasAlternateKey("WorkspaceId", "ScopeProjectId", "Id");
        revision.Property<Guid>("CompanyScopeProjectId").HasDefaultValue(Guid.Empty);
        revision.HasOne<PolicyRevision>().WithMany()
            .HasForeignKey("WorkspaceId", "CompanyScopeProjectId", "BaseCompanyVersionId")
            .HasPrincipalKey("WorkspaceId", "ScopeProjectId", "Id").OnDelete(DeleteBehavior.Restrict);
        Accounts(revision, "CreatedBy");
        Person(revision, "CreatedByPersonId");
        revision.Property(x => x.State).HasMaxLength(30);
        revision.Property(x => x.ContentHash).HasMaxLength(64);
        revision.ToTable(t =>
        {
            t.HasCheckConstraint("ck_field_advance_policy_base", $"company_scope_project_id={EmptyId} AND ((project_id IS NULL AND base_company_version_id IS NULL) OR (project_id IS NOT NULL AND base_company_version_id IS NOT NULL))");
            t.HasCheckConstraint("ck_field_advance_policy_content", "clearing_days>=0 AND content_hash ~ '^[a-f0-9]{64}$'");
            t.HasCheckConstraint("ck_field_advance_policy_state", "state IN ('Draft','Pending','Approved','Active','Rejected','Retired')");
        });

        var band = model.Entity<PolicyApprovalBand>();
        band.ToTable("field_advance_approval_bands", t => t.HasCheckConstraint("ck_field_advance_band", "\"order\">0 AND length(btrim(level_code))>0 AND upper_inclusive>=0"));
        band.HasKey(x => new { x.PolicyRevisionId, x.Order });
        band.HasOne<PolicyRevision>().WithMany().HasForeignKey(x => x.PolicyRevisionId).OnDelete(DeleteBehavior.Restrict);
        band.Property(x => x.UpperInclusive).HasPrecision(18, 2);
        band.Property(x => x.LevelCode).HasMaxLength(64);

        var category = model.Entity<PolicyCategoryRule>();
        category.ToTable("field_advance_category_rules", t => t.HasCheckConstraint("ck_field_advance_category", "length(btrim(name))>0 AND (standard_rate IS NULL OR standard_rate>=0)"));
        category.HasKey(x => new { x.PolicyRevisionId, x.Id });
        category.HasOne<PolicyRevision>().WithMany().HasForeignKey(x => x.PolicyRevisionId).OnDelete(DeleteBehavior.Restrict);
        category.Property(x => x.StandardRate).HasPrecision(18, 2);
        category.Property(x => x.Name).HasMaxLength(200);
        category.Property(x => x.Unit).HasMaxLength(100);
        category.Property(x => x.EvidenceTypes).HasColumnType("text[]");

        var contributor = Row<PolicyContributor>(model, "field_advance_policy_contributors");
        Accounts(contributor, "UserId");
        Person(contributor, "PersonId");
        contributor.HasOne<PolicyRevision>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.PolicyRevisionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        contributor.HasIndex(x => new { x.PolicyRevisionId, x.PersonId });

        var policyReview = Row<PolicyReview>(model, "field_advance_policy_reviews");
        Accounts(policyReview, "ReviewerId");
        Person(policyReview, "ReviewerPersonId");
        policyReview.HasOne<PolicyRevision>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.PolicyRevisionId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        policyReview.Property(x => x.Outcome).HasMaxLength(30);

        var active = Scoped<ActivePolicy>(model, "field_advance_active_policies");
        Accounts(active, "CreatedBy");
        active.HasIndex("WorkspaceId", "ScopeProjectId").IsUnique();
        active.HasOne<PolicyRevision>().WithMany().HasForeignKey("WorkspaceId", "ScopeProjectId", "PolicyRevisionId")
            .HasPrincipalKey("WorkspaceId", "ScopeProjectId", "Id").OnDelete(DeleteBehavior.Restrict);

        var proposal = Scoped<GrantProposal>(model, "field_advance_grant_proposals");
        proposal.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        proposal.HasAlternateKey("WorkspaceId", "ScopeProjectId", "Id");
        Accounts(proposal, "RecipientId", "ProposedBy");
        Person(proposal, "RecipientPersonId");
        Person(proposal, "ProposedByPersonId");
        proposal.Property(x => x.State).HasMaxLength(30);
        proposal.Property(x => x.Capabilities).HasColumnType("text[]");
        proposal.Property(x => x.DelegableCapabilities).HasColumnType("text[]");
        proposal.Property(x => x.ApprovalLimit).HasPrecision(18, 2);
        proposal.ToTable(t =>
        {
            t.HasCheckConstraint("ck_field_advance_grant_amount", "approval_limit IS NULL OR approval_limit>=0");
            t.HasCheckConstraint("ck_field_advance_grant_period", "isfinite(starts_at_utc) AND (ends_at_utc IS NULL OR (isfinite(ends_at_utc) AND ends_at_utc>starts_at_utc))");
            t.HasCheckConstraint("ck_field_advance_grant_state", "state IN ('Draft','Pending','Approved','Rejected','Withdrawn')");
        });

        var grantReview = Row<GrantReview>(model, "field_advance_grant_reviews");
        Accounts(grantReview, "ReviewerId");
        Person(grantReview, "ReviewerPersonId");
        grantReview.Property(x => x.Outcome).HasMaxLength(30);
        grantReview.HasOne<GrantProposal>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProposalId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        grantReview.HasAlternateKey(x => new { x.WorkspaceId, x.ProposalId, x.Id });

        var assignment = Scoped<GrantAssignment>(model, "field_advance_grant_assignments");
        Accounts(assignment, "CreatedBy", "RevokedBy");
        assignment.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        assignment.HasIndex(x => x.ProposalId).IsUnique();
        assignment.HasOne<GrantProposal>().WithMany().HasForeignKey("WorkspaceId", "ScopeProjectId", "ProposalId")
            .HasPrincipalKey("WorkspaceId", "ScopeProjectId", "Id").OnDelete(DeleteBehavior.Restrict);
        assignment.HasOne<GrantReview>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProposalId, x.ReviewId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProposalId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        assignment.ToTable(t => t.HasCheckConstraint("ck_field_advance_grant_revocation", """
            (revoked_at_utc IS NULL AND revoked_by IS NULL AND revocation_reason IS NULL) OR
            (revoked_at_utc IS NOT NULL AND revoked_by IS NOT NULL AND revocation_reason IS NOT NULL
             AND isfinite(revoked_at_utc) AND revoked_at_utc>=created_at_utc AND length(btrim(revocation_reason)) BETWEEN 1 AND 2000)
            """));

        var receipt = Row<FinanceCommandReceipt>(model, "field_advance_command_receipts", "CommandId");
        Accounts(receipt, "ActorId");
        receipt.HasAlternateKey(x => new { x.WorkspaceId, x.CommandId });
        receipt.Property(x => x.Action).HasMaxLength(100);
        receipt.Property(x => x.PayloadSha256).HasMaxLength(64);
        receipt.Property(x => x.ResultJson).HasColumnType("jsonb");
        receipt.ToTable(t => t.HasCheckConstraint("ck_field_advance_receipt", $"command_id<>{EmptyId} AND length(btrim(action))>0 AND payload_sha256 ~ '^[a-f0-9]{{64}}$' AND status BETWEEN 200 AND 299 AND jsonb_typeof(result_json)='object'"));

        var bootstrap = Row<FinanceBootstrapReceipt>(model, "field_advance_bootstrap_receipts");
        Accounts(bootstrap, "CreatedBy");
        bootstrap.HasIndex(x => new { x.CommandId, x.GrantAssignmentId }).IsUnique();
        bootstrap.HasOne<FinanceCommandReceipt>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.CommandId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.CommandId }).OnDelete(DeleteBehavior.Restrict);
        bootstrap.HasOne<GrantAssignment>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.GrantAssignmentId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        foreach (var type in new[] { typeof(FinancePerson), typeof(FinanceAccountLink), typeof(PolicyRevision),
            typeof(PolicyApprovalBand), typeof(PolicyCategoryRule), typeof(PolicyContributor), typeof(PolicyReview),
            typeof(ActivePolicy), typeof(GrantProposal), typeof(GrantReview), typeof(GrantAssignment),
            typeof(FinanceCommandReceipt), typeof(FinanceBootstrapReceipt) })
            foreach (var property in model.Entity(type).Metadata.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }

    private static EntityTypeBuilder<T> Row<T>(ModelBuilder model, string table, string key = "Id") where T : class
    {
        var entity = model.Entity<T>();
        entity.ToTable(table);
        entity.HasKey(key);
        entity.HasOne<Workspace>().WithMany().HasForeignKey("WorkspaceId").OnDelete(DeleteBehavior.Restrict);
        entity.Property<DateTimeOffset>("CreatedAtUtc").HasDefaultValueSql("now()");
        entity.ToTable(t => t.HasCheckConstraint("ck_" + table + "_time", "isfinite(created_at_utc)"));
        if (typeof(T).GetProperty("Version") is not null)
        {
            entity.Property<long>("Version").HasDefaultValue(1L).IsConcurrencyToken();
            entity.ToTable(t => t.HasCheckConstraint("ck_" + table + "_version", "version>=1"));
        }
        if (typeof(T).GetProperty("Reason") is not null)
        {
            entity.Property<string>("Reason").HasMaxLength(2000);
            entity.ToTable(t => t.HasCheckConstraint("ck_" + table + "_reason", "length(btrim(reason)) BETWEEN 1 AND 2000"));
        }
        return entity;
    }

    private static EntityTypeBuilder<T> Scoped<T>(ModelBuilder model, string table) where T : class
    {
        var entity = Row<T>(model, table);
        entity.HasOne<Project>().WithMany().HasForeignKey("WorkspaceId", "ProjectId")
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        // NULL must participate in uniqueness and exact scope foreign keys, not bypass them.
        entity.Property<Guid>("ScopeProjectId").HasComputedColumnSql($"COALESCE(project_id,{EmptyId})", stored: true)
            .ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        entity.ToTable(t => t.HasCheckConstraint("ck_" + table + "_project", $"project_id IS NULL OR project_id<>{EmptyId}"));
        return entity;
    }

    private static void Accounts<T>(EntityTypeBuilder<T> entity, params string[] names) where T : class
    {
        foreach (var name in names)
            entity.HasOne<IdentityUser>().WithMany().HasForeignKey(name).OnDelete(DeleteBehavior.Restrict);
    }

    private static void Person<T>(EntityTypeBuilder<T> entity, string name) where T : class =>
        entity.HasOne<FinancePerson>().WithMany().HasForeignKey("WorkspaceId", name)
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
}
