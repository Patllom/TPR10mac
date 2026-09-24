using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.Scopes.Data;

internal static class ScopeModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var assignment = Scoped<ScopeAssignment>(model, "user_scope_assignments");
        assignment.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        assignment.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.RevokedBy).OnDelete(DeleteBehavior.Restrict);
        assignment.HasOne<IdentityRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        assignment.Property(x => x.Reason).HasMaxLength(500);
        assignment.Property(x => x.RevocationReason).HasMaxLength(500);
        assignment.ToTable(t =>
        {
            t.HasCheckConstraint("ck_assignment_reason", "length(reason) BETWEEN 1 AND 500 AND reason !~ '[[:cntrl:]]'");
            t.HasCheckConstraint("ck_assignment_revocation", """
                (revoked_at_utc IS NULL AND revoked_by IS NULL AND revocation_reason IS NULL)
                OR (revoked_at_utc IS NOT NULL AND revoked_by IS NOT NULL AND revocation_reason IS NOT NULL
                    AND length(revocation_reason) BETWEEN 1 AND 500 AND revocation_reason !~ '[[:cntrl:]]')
                """);
        });
        assignment.HasIndex(x => new { x.UserId, x.WorkspaceId, x.RoleId }).IsUnique()
            .HasDatabaseName("ux_assignment_workspace")
            .HasFilter("revoked_at_utc IS NULL AND project_id IS NULL AND site_id IS NULL");
        assignment.HasIndex(x => new { x.UserId, x.WorkspaceId, x.ProjectId, x.RoleId }).IsUnique()
            .HasDatabaseName("ux_assignment_project")
            .HasFilter("revoked_at_utc IS NULL AND project_id IS NOT NULL AND site_id IS NULL");
        assignment.HasIndex(x => new { x.UserId, x.WorkspaceId, x.ProjectId, x.SiteId, x.RoleId }).IsUnique()
            .HasDatabaseName("ux_assignment_site").HasFilter("revoked_at_utc IS NULL AND site_id IS NOT NULL");

        var record = Scoped<ScopeProbeRecord>(model, "scope_probe_records");
        record.Property(x => x.Note).HasMaxLength(500);
        record.Property(x => x.RestrictedNote).HasMaxLength(500);
        record.ToTable(t =>
        {
            t.HasCheckConstraint("ck_scope_record_note", "note !~ '[[:cntrl:]]'");
            t.HasCheckConstraint("ck_scope_record_restricted_note", "restricted_note IS NULL OR restricted_note !~ '[[:cntrl:]]'");
        });
        record.HasIndex(x => new { x.WorkspaceId, x.ProjectId, x.SiteId, x.CreatedAtUtc, x.Id });
    }

    private static EntityTypeBuilder<T> Scoped<T>(ModelBuilder model, string table) where T : class
    {
        var entity = OrganizationModelConfiguration.Audited<T>(model, table);
        entity.ToTable(table, t => t.HasCheckConstraint("ck_" + table + "_shape", "site_id IS NULL OR project_id IS NOT NULL"));
        entity.HasOne<Workspace>().WithMany().HasForeignKey("WorkspaceId").OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Project>().WithMany().HasForeignKey("WorkspaceId", "ProjectId")
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Site>().WithMany().HasForeignKey("WorkspaceId", "ProjectId", "SiteId")
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        return entity;
    }
}
