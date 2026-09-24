using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.Attendance.Directory;

internal static class DirectoryModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var membership = Temporal<EmployeeMembership>(model, "employee_memberships");
        membership.HasAlternateKey(x => new { x.Id, x.UserId });
        membership.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        membership.HasOne<Department>().WithMany().HasForeignKey(x => new { x.DepartmentId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        membership.HasIndex(x => new { x.UserId, x.ValidFromUtc, x.ValidToUtc });

        var reporting = Temporal<ReportingLine>(model, "reporting_lines");
        reporting.HasOne<EmployeeMembership>().WithMany().HasForeignKey(x => new { x.EmployeeMembershipId, x.EmployeeUserId })
            .HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        reporting.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.SupervisorUserId).OnDelete(DeleteBehavior.Restrict);
        reporting.HasIndex(x => new { x.EmployeeUserId, x.ValidFromUtc, x.ValidToUtc });
        reporting.ToTable("reporting_lines", t => t.HasCheckConstraint("ck_reporting_lines_not_self", "employee_user_id <> supervisor_user_id"));

        var hr = Temporal<HrAssignment>(model, "hr_assignments");
        hr.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        hr.HasOne<Department>().WithMany().HasForeignKey(x => new { x.DepartmentId, x.WorkspaceId })
            .HasPrincipalKey(x => new { x.Id, x.WorkspaceId }).OnDelete(DeleteBehavior.Restrict);
        hr.HasIndex(x => new { x.UserId, x.WorkspaceId, x.DepartmentId, x.ValidFromUtc, x.ValidToUtc });
    }

    private static EntityTypeBuilder<T> Temporal<T>(ModelBuilder model, string table) where T : class
    {
        var entity = model.Entity<T>();
        entity.ToTable(table, t =>
        {
            t.HasCheckConstraint("ck_" + table + "_version", "version >= 1");
            t.HasCheckConstraint("ck_" + table + "_period", "valid_to_utc IS NULL OR valid_to_utc > valid_from_utc");
            t.HasCheckConstraint("ck_" + table + "_reason", "length(btrim(reason)) BETWEEN 1 AND 500 AND reason !~ '[[:cntrl:]]'");
        });
        entity.HasKey("Id");
        entity.Property<long>("Version").HasDefaultValue(1L).IsConcurrencyToken();
        entity.Property<DateTimeOffset>("CreatedAtUtc").HasDefaultValueSql("now()");
        entity.Property<string>("Reason").HasMaxLength(500).IsRequired();
        entity.HasOne<IdentityUser>().WithMany().HasForeignKey("CreatedBy").OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<IdentityUser>().WithMany().HasForeignKey("EndedBy").OnDelete(DeleteBehavior.Restrict);
        foreach (var property in entity.Metadata.GetProperties())
            property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
        return entity;
    }
}
