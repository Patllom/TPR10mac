using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.Organization.Data;

internal static class OrganizationModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var workspace = Organization<Workspace>(model, "workspaces");
        workspace.HasIndex(x => x.Code).IsUnique();
        var department = Organization<Department>(model, "departments");
        department.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        department.HasIndex(x => new { x.WorkspaceId, x.Code }).IsUnique();
        var project = Organization<Project>(model, "projects");
        project.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        project.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        project.HasIndex(x => new { x.WorkspaceId, x.Code }).IsUnique();
        var site = Organization<Site>(model, "sites");
        site.HasOne<Project>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        site.HasAlternateKey(x => new { x.WorkspaceId, x.ProjectId, x.Id });
        site.HasIndex(x => new { x.ProjectId, x.Code }).IsUnique();
    }

    private static EntityTypeBuilder<T> Organization<T>(ModelBuilder model, string table) where T : class
    {
        var entity = Audited<T>(model, table);
        entity.Property<string>("Code").HasMaxLength(64).IsRequired();
        entity.Property<string>("Name").HasMaxLength(200).IsRequired();
        entity.Property<bool>("IsActive").HasDefaultValue(true);
        entity.ToTable(table, t =>
        {
            t.HasCheckConstraint("ck_" + table + "_code", "code ~ '^[A-Z0-9_-]{1,64}$'");
            t.HasCheckConstraint("ck_" + table + "_name", "length(name) BETWEEN 1 AND 200 AND name !~ '[[:cntrl:]]'");
        });
        return entity;
    }

    internal static EntityTypeBuilder<T> Audited<T>(ModelBuilder model, string table) where T : class
    {
        var entity = model.Entity<T>();
        entity.ToTable(table, t => t.HasCheckConstraint("ck_" + table + "_version", "version >= 1"));
        entity.HasKey("Id");
        entity.Property<long>("Version").HasDefaultValue(1L).IsConcurrencyToken();
        entity.Property<DateTimeOffset>("CreatedAtUtc").HasDefaultValueSql("now()");
        entity.HasOne<IdentityUser>().WithMany().HasForeignKey("CreatedBy").OnDelete(DeleteBehavior.Restrict);
        // Assignments have revocation metadata rather than general update metadata.
        if (typeof(T).GetProperty("UpdatedBy") is not null)
            entity.HasOne<IdentityUser>().WithMany().HasForeignKey("UpdatedBy").OnDelete(DeleteBehavior.Restrict);
        foreach (var property in entity.Metadata.GetProperties())
            property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
        return entity;
    }
}
