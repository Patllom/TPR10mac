using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Storage;
using TPR10.Api.Identity.Data;

namespace TPR10.Api.Attendance.Evidence;

internal static class EvidenceModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var storage = Entity<StorageLocation>(model, "attendance_storage_locations");
        storage.Property(x => x.Alias).HasMaxLength(100);
        storage.Property(x => x.Kind).HasMaxLength(40);
        storage.Property(x => x.ConfigFingerprint).HasMaxLength(64);
        storage.Property(x => x.Health).HasMaxLength(20).HasDefaultValue("unknown");
        storage.HasIndex(x => x.Alias).IsUnique();
        storage.ToTable("attendance_storage_locations", t =>
        {
            t.HasCheckConstraint("ck_storage_alias", "alias ~ '^[a-z0-9][a-z0-9-]{0,99}$'");
            t.HasCheckConstraint("ck_storage_kind", "kind IN ('local-folder','nas-mounted-folder')");
            t.HasCheckConstraint("ck_storage_fingerprint", "config_fingerprint ~ '^[a-f0-9]{64}$'");
            t.HasCheckConstraint("ck_storage_health", "health IN ('unknown','ready','warning','unavailable')");
            t.HasCheckConstraint("ck_storage_capacity", "(free_bytes IS NULL OR free_bytes>=0) AND (total_bytes IS NULL OR total_bytes>=0) AND (free_bytes IS NULL OR total_bytes IS NULL OR free_bytes<=total_bytes)");
        });
        var target = Entity<StorageWriteTarget>(model, "attendance_storage_write_target");
        target.Property(x => x.Id).ValueGeneratedNever();
        target.HasOne<StorageLocation>().WithMany().HasForeignKey(x => x.StorageId).OnDelete(DeleteBehavior.Restrict);
        target.ToTable("attendance_storage_write_target", t => t.HasCheckConstraint("ck_storage_target_singleton", "id=1"));

        var evidence = Entity<EvidenceObject>(model, "evidence_objects");
        evidence.Property(x => x.State).HasConversion<string>().HasMaxLength(20).HasDefaultValue(EvidenceState.Reserved);
        evidence.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
        evidence.Property(x => x.ObjectKey).HasMaxLength(128);
        evidence.Property(x => x.Sha256).HasMaxLength(64);
        evidence.Property(x => x.InputSha256).HasMaxLength(64);
        evidence.Property(x => x.ThumbnailSha256).HasMaxLength(64);
        evidence.Property(x => x.FencingVersion).HasDefaultValue(1L);
        evidence.HasIndex(x => x.OperationId).IsUnique();
        evidence.HasIndex(x => new { x.OwnerId, x.OccurredAtUtc });
        evidence.HasOne<EmployeeMembership>().WithMany().HasForeignKey(x => new { x.MembershipId, x.OwnerId, x.WorkspaceId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.Id, x.UserId, x.WorkspaceId, x.DepartmentId }).OnDelete(DeleteBehavior.Restrict);
        evidence.HasOne<StorageLocation>().WithMany().HasForeignKey(x => x.StorageId).OnDelete(DeleteBehavior.Restrict);
        evidence.ToTable("evidence_objects", t =>
        {
            t.HasCheckConstraint("ck_evidence_action", "action IN ('CheckIn','CheckOut')");
            t.HasCheckConstraint("ck_evidence_input", "input_sha256 IS NULL OR input_sha256 ~ '^[a-f0-9]{64}$'");
            t.HasCheckConstraint("ck_evidence_state", "state IN ('Reserved','Prepared','Published','Orphan')");
            t.HasCheckConstraint("ck_evidence_pin", "storage_version>=1 AND fencing_version>=1 AND operation_id<>'00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_evidence_key", "object_key = 'objects/' || left(replace(id::text,'-',''),2) || '/' || replace(id::text,'-','')");
            t.HasCheckConstraint("ck_evidence_time", "snapshot_at_utc<=occurred_at_utc AND isfinite(snapshot_at_utc) AND isfinite(occurred_at_utc)");
            t.HasCheckConstraint("ck_evidence_image", ImageCheck(""));
            t.HasCheckConstraint("ck_evidence_thumbnail", ImageCheck("thumbnail_"));
            t.HasCheckConstraint("ck_evidence_prepared", "state NOT IN ('Prepared','Published') OR (sha256 IS NOT NULL AND thumbnail_sha256 IS NOT NULL)");
        });

        var location = Entity<EvidenceLocation>(model, "evidence_locations");
        location.Property(x => x.State).HasConversion<string>().HasMaxLength(20).HasDefaultValue(CopyState.Pending);
        location.Property(x => x.Variant).HasMaxLength(20);
        location.Property(x => x.Sha256).HasMaxLength(64);
        location.Property(x => x.ObjectKey).HasMaxLength(160);
        location.HasOne<EvidenceObject>().WithMany().HasForeignKey(x => x.EvidenceId).OnDelete(DeleteBehavior.Restrict);
        location.HasOne<StorageLocation>().WithMany().HasForeignKey(x => x.StorageId).OnDelete(DeleteBehavior.Restrict);
        location.HasIndex(x => new { x.EvidenceId, x.StorageId, x.Variant }).IsUnique();
        location.HasIndex(x => new { x.EvidenceId, x.Variant }).IsUnique().HasFilter("state = 'Active'");
        location.HasIndex(x => new { x.StorageId, x.ObjectKey }).IsUnique();
        location.ToTable("evidence_locations", t =>
        {
            t.HasCheckConstraint("ck_evidence_copy_variant", "variant IN ('full','thumbnail')");
            t.HasCheckConstraint("ck_evidence_copy_state", "state IN ('Pending','Verified','Active','Fallback','Quarantined')");
            t.HasCheckConstraint("ck_evidence_copy_bytes", "sha256 ~ '^[a-f0-9]{64}$' AND length BETWEEN 1 AND 10485760");
            t.HasCheckConstraint("ck_evidence_copy_verified", "state NOT IN ('Verified','Active','Fallback') OR verified_at_utc IS NOT NULL");
            t.HasCheckConstraint("ck_evidence_copy_key", "object_key = 'objects/' || left(replace(evidence_id::text,'-',''),2) || '/' || replace(evidence_id::text,'-','') || '/' || variant || '.jpg'");
        });
        var binding = model.Entity<EvidenceBinding>();
        binding.ToTable("evidence_bindings", t => t.HasCheckConstraint("ck_evidence_event", "event_id<>'00000000-0000-0000-0000-000000000000'::uuid AND isfinite(published_at_utc)"));
        binding.HasKey(x => x.EvidenceId);
        binding.HasIndex(x => x.EventId).IsUnique();
        binding.HasOne<EvidenceObject>().WithMany().HasForeignKey(x => x.EvidenceId).OnDelete(DeleteBehavior.Restrict);

        var job = Entity<MigrationJob>(model, "evidence_migration_jobs");
        job.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Pending");
        job.Property(x => x.Reason).HasMaxLength(500);
        job.HasOne<StorageLocation>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        job.HasOne<StorageLocation>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);
        job.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        job.HasAlternateKey(x => new { x.Id, x.SourceId, x.TargetId });
        job.HasIndex(x => x.RequestId).IsUnique();
        job.HasIndex(x => x.SourceId).IsUnique().HasFilter("status <> 'Completed'");
        job.ToTable("evidence_migration_jobs", t =>
        {
            t.HasCheckConstraint("ck_evidence_job_endpoints", "source_id<>target_id AND source_version>=1 AND target_version>=1");
            t.HasCheckConstraint("ck_evidence_job_status", "status IN ('Pending','Running','Blocked','Completed')");
            t.HasCheckConstraint("ck_evidence_job_reason", "length(btrim(reason)) BETWEEN 1 AND 500 AND reason !~ '[[:cntrl:]]'");
        });
        var item = Entity<MigrationItem>(model, "evidence_migration_items");
        item.Property(x => x.Variant).HasMaxLength(20);
        item.Property(x => x.ExpectedSha256).HasMaxLength(64);
        item.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Pending");
        item.Property(x => x.ErrorCode).HasMaxLength(80);
        item.Property(x => x.FencingVersion).HasDefaultValue(1L);
        item.HasOne<MigrationJob>().WithMany().HasForeignKey(x => new { x.JobId, x.SourceId, x.TargetId })
            .HasPrincipalKey(x => new { x.Id, x.SourceId, x.TargetId }).OnDelete(DeleteBehavior.Restrict);
        item.HasOne<EvidenceObject>().WithMany().HasForeignKey(x => x.EvidenceId).OnDelete(DeleteBehavior.Restrict);
        item.HasIndex(x => new { x.JobId, x.EvidenceId, x.Variant }).IsUnique();
        item.ToTable("evidence_migration_items", t =>
        {
            t.HasCheckConstraint("ck_evidence_item_variant", "variant IN ('full','thumbnail')");
            t.HasCheckConstraint("ck_evidence_item_hash", "expected_sha256 ~ '^[a-f0-9]{64}$' AND expected_length BETWEEN 1 AND 10485760");
            t.HasCheckConstraint("ck_evidence_item_status", "status IN ('Pending','Copying','Verified','Completed','Blocked')");
            t.HasCheckConstraint("ck_evidence_item_lease", "attempts>=0 AND fencing_version>=1 AND ((lease_owner IS NULL)=(lease_until_utc IS NULL))");
            t.HasCheckConstraint("ck_evidence_item_error", "error_code IS NULL OR error_code ~ '^[a-z0-9][a-z0-9-]{0,79}$'");
        });
        // Apply after all properties and relationships, including alternate keys, exist.
        foreach (var type in new[] { typeof(EvidenceObject), typeof(EvidenceLocation), typeof(EvidenceBinding), typeof(StorageLocation), typeof(StorageWriteTarget), typeof(MigrationJob), typeof(MigrationItem) })
            foreach (var property in model.Entity(type).Metadata.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }

    private static EntityTypeBuilder<T> Entity<T>(ModelBuilder model, string table) where T : class
    {
        var e = model.Entity<T>();
        e.HasKey("Id");
        e.Property<long>("Version").HasDefaultValue(1L).IsConcurrencyToken();
        e.ToTable(table, t => t.HasCheckConstraint("ck_" + table + "_version", "version>=1"));
        if (typeof(T) != typeof(StorageWriteTarget)) e.Property<DateTimeOffset>("CreatedAtUtc").HasDefaultValueSql("now()");
        return e;
    }

    private static string ImageCheck(string p) =>
        $"(({p}sha256 IS NULL AND {p}length IS NULL AND {p}width IS NULL AND {p}height IS NULL) OR " +
        $"({p}sha256 IS NOT NULL AND {p}length IS NOT NULL AND {p}width IS NOT NULL AND {p}height IS NOT NULL AND " +
        $"{p}sha256 ~ '^[a-f0-9]{{64}}$' AND {p}length BETWEEN 1 AND 10485760 AND {p}width>0 AND {p}height>0 AND {p}width::bigint*{p}height<=20000000))";
}
