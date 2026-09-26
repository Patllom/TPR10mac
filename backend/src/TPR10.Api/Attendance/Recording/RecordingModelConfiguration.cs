using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.Attendance.Recording;

internal static class RecordingModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var day = model.Entity<AttendanceDay>();
        day.HasKey(x => x.Id);
        day.HasAlternateKey(x => new { x.Id, x.EmployeeId });
        day.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        day.HasIndex(x => new { x.EmployeeId, x.WorkDate }).IsUnique();
        day.HasIndex(x => x.EmployeeId).IsUnique().HasFilter("effective_out_utc IS NULL");
        day.HasOne<EmployeeMembership>().WithMany().HasForeignKey(x => new { x.MembershipId, x.EmployeeId, x.WorkspaceId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.Id, x.UserId, x.WorkspaceId, x.DepartmentId }).OnDelete(DeleteBehavior.Restrict);
        day.ToTable("attendance_days", t =>
        {
            t.HasCheckConstraint("ck_attendance_day_version", "version>=1");
            t.HasCheckConstraint("ck_attendance_day_time", "isfinite(work_date) AND isfinite(snapshot_at_utc) AND isfinite(effective_in_utc) AND (effective_out_utc IS NULL OR (isfinite(effective_out_utc) AND effective_out_utc>effective_in_utc))");
            t.HasCheckConstraint("ck_attendance_day_date", "work_date=(snapshot_at_utc AT TIME ZONE 'Asia/Bangkok')::date");
        });

        var e = model.Entity<AttendanceEvent>();
        e.HasKey(x => x.Id);
        e.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
        e.Property(x => x.Source).HasMaxLength(30);
        e.HasIndex(x => new { x.DayId, x.Action }).IsUnique();
        e.HasIndex(x => x.EvidenceId).IsUnique();
        e.HasOne<AttendanceDay>().WithMany().HasForeignKey(x => new { x.DayId, x.ActorId })
            .HasPrincipalKey(x => new { x.Id, x.EmployeeId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<EvidenceObject>().WithMany().HasForeignKey(x => x.EvidenceId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Site>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.SiteId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.ToTable("attendance_events", t =>
        {
            t.HasCheckConstraint("ck_attendance_event_action", "action IN ('CheckIn','CheckOut')");
            t.HasCheckConstraint("ck_attendance_event_source", "source='live'");
            t.HasCheckConstraint("ck_attendance_event_time", "isfinite(occurred_at_utc) AND isfinite(device_captured_at_utc) AND isfinite(gps_sampled_at_utc)");
            t.HasCheckConstraint("ck_attendance_event_gps", "latitude BETWEEN -90 AND 90 AND longitude BETWEEN -180 AND 180 AND accuracy_meters>0 AND accuracy_meters<'Infinity'::float8");
        });

        var c = model.Entity<CaptureChallenge>();
        c.HasKey(x => x.Id);
        c.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
        c.HasIndex(x => new { x.ActorId, x.Action, x.IdempotencyKey }).IsUnique();
        c.HasAlternateKey(x => new { x.Id, x.ActorId, x.SessionId, x.Action, x.WorkspaceId, x.ProjectId, x.SiteId, x.IdempotencyKey });
        c.HasOne<IdentitySession>().WithMany().HasForeignKey(x => new { x.SessionId, x.ActorId })
            .HasPrincipalKey(x => new { x.Id, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        c.HasOne<Site>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ProjectId, x.SiteId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        c.HasOne<AttendanceOperation>().WithMany().HasForeignKey(x => new { x.ConsumedOperationId, x.Id })
            .HasPrincipalKey(x => new { x.Id, x.ChallengeId }).OnDelete(DeleteBehavior.Restrict);
        c.ToTable("capture_challenges", t =>
        {
            t.HasCheckConstraint("ck_capture_action", "action IN ('CheckIn','CheckOut')");
            t.HasCheckConstraint("ck_capture_key", "idempotency_key<>'00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_capture_time", "isfinite(issued_at_utc) AND isfinite(expires_at_utc) AND expires_at_utc=issued_at_utc+interval '120 seconds'");
        });

        var op = model.Entity<AttendanceOperation>();
        op.HasKey(x => x.Id);
        op.HasAlternateKey(x => new { x.Id, x.ChallengeId });
        op.HasIndex(x => x.ChallengeId).IsUnique();
        op.HasIndex(x => new { x.ActorId, x.Action, x.IdempotencyKey }).IsUnique();
        op.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
        op.Property(x => x.State).HasConversion<string>().HasMaxLength(20).HasDefaultValue(AttendanceOperationState.Processing);
        op.Property(x => x.PayloadSha256).HasMaxLength(64);
        op.Property(x => x.ResultJson).HasColumnType("jsonb");
        op.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        op.Property(x => x.FencingVersion).HasDefaultValue(1L);
        op.HasOne<CaptureChallenge>().WithMany().HasForeignKey(x => new { x.ChallengeId, x.ActorId, x.SessionId, x.Action, x.WorkspaceId, x.ProjectId, x.SiteId, x.IdempotencyKey })
            .HasPrincipalKey(x => new { x.Id, x.ActorId, x.SessionId, x.Action, x.WorkspaceId, x.ProjectId, x.SiteId, x.IdempotencyKey }).OnDelete(DeleteBehavior.Restrict);
        op.HasOne<EmployeeMembership>().WithMany().HasForeignKey(x => new { x.MembershipId, x.ActorId, x.MembershipWorkspaceId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.Id, x.UserId, x.WorkspaceId, x.DepartmentId }).OnDelete(DeleteBehavior.Restrict);
        op.HasOne<AttendanceEvent>().WithMany().HasForeignKey(x => x.ResultEventId).OnDelete(DeleteBehavior.Restrict);
        op.ToTable("attendance_operations", t =>
        {
            t.HasCheckConstraint("ck_attendance_operation_state", "state IN ('Processing','Committed','Expired','Failed')");
            t.HasCheckConstraint("ck_attendance_operation_hash", "payload_sha256 ~ '^[a-f0-9]{64}$'");
            t.HasCheckConstraint("ck_attendance_operation_version", "version>=1 AND fencing_version>=1 AND (expected_day_version IS NULL OR expected_day_version>=1)");
            t.HasCheckConstraint("ck_attendance_operation_time", "isfinite(occurred_at_utc) AND isfinite(lease_until_utc) AND lease_until_utc>occurred_at_utc");
            t.HasCheckConstraint("ck_attendance_operation_day", "target_day_id<>'00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_attendance_operation_result", "(state='Committed' AND result_event_id IS NOT NULL AND result_json IS NOT NULL AND jsonb_typeof(result_json)='object' AND octet_length(result_json::text)<=4096) OR (state<>'Committed' AND result_event_id IS NULL AND result_json IS NULL)");
        });
        // Session alternate-key properties already have snake-case names from identity configuration.
        foreach (var type in new[] { typeof(AttendanceDay), typeof(AttendanceEvent), typeof(CaptureChallenge), typeof(AttendanceOperation) })
            foreach (var property in model.Entity(type).Metadata.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }
}
