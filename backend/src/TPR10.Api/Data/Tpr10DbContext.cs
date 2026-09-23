using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data.Entities;

namespace TPR10.Api.Data;

public sealed class Tpr10DbContext(DbContextOptions<Tpr10DbContext> options) : DbContext(options)
{
    public DbSet<TechnicalProbe> TechnicalProbes => Set<TechnicalProbe>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AuditEventMetadata> AuditMetadata => Set<AuditEventMetadata>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        TPR10.Api.Identity.Data.IdentityModelConfiguration.Configure(model);
        TPR10.Api.Organization.Data.OrganizationModelConfiguration.Configure(model);
        TPR10.Api.Scopes.Data.ScopeModelConfiguration.Configure(model);
        var probe = model.Entity<TechnicalProbe>();
        probe.ToTable("technical_probes");
        probe.HasKey(x => x.Id);
        probe.Property(x => x.Id).HasColumnName("id");
        probe.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
        probe.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        probe.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(36);
        var audit = model.Entity<AuditEvent>();
        audit.ToTable("audit_events");
        audit.HasKey(x => x.Id);
        audit.Property(x => x.Id).HasColumnName("id");
        audit.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(120);
        audit.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
        audit.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(36);
        audit.Property(x => x.ActorId).HasColumnName("actor_id");
        audit.Property(x => x.ActingRoleId).HasColumnName("acting_role_id");
        audit.Property(x => x.TargetType).HasColumnName("target_type").HasMaxLength(120);
        audit.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(40);
        audit.Property(x => x.WorkspaceId).HasColumnName("workspace_id");
        audit.Property(x => x.ProjectId).HasColumnName("project_id");
        audit.Property(x => x.SiteId).HasColumnName("site_id");
        audit.Property(x => x.TargetId).HasColumnName("target_id");
        audit.HasIndex(x => x.CorrelationId);
        audit.HasIndex(x => x.OccurredAtUtc);
        var metadata = model.Entity<AuditEventMetadata>();
        metadata.ToTable("audit_event_metadata");
        metadata.HasKey(x => x.Id);
        metadata.Property(x => x.Id).HasColumnName("id");
        metadata.Property(x => x.AuditEventId).HasColumnName("audit_event_id");
        metadata.Property(x => x.Key).HasColumnName("key").HasMaxLength(100);
        metadata.Property(x => x.Value).HasColumnName("value").HasMaxLength(1000);
        metadata.HasOne<AuditEvent>().WithMany().HasForeignKey(x => x.AuditEventId).OnDelete(DeleteBehavior.Restrict);
        metadata.HasIndex(x => new { x.AuditEventId, x.Key }).IsUnique();
    }
}
