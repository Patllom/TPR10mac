using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Directory;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.Attendance.Corrections;

internal static class CorrectionModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        foreach (var type in new[] { typeof(AttendanceDay), typeof(AttendanceEvent), typeof(AttendanceAdjustment) })
            model.Entity(type).Property<long>("WrittenTransactionId").HasColumnName("written_transaction_id").HasDefaultValue(0L);
        var request = model.Entity<CorrectionRequest>();
        request.HasKey(x => x.Id);
        request.Property(x => x.State).HasConversion<string>().HasMaxLength(30).HasDefaultValue(CorrectionState.Draft);
        request.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
        request.Property(x => x.CurrentRevision).HasDefaultValue(0);
        request.HasIndex(x => new { x.EmployeeId, x.OriginalWorkDate }).IsUnique().HasFilter("state NOT IN ('Approved','Expired')");
        request.HasOne<EmployeeMembership>().WithMany().HasForeignKey(x => new { x.MembershipId, x.EmployeeId, x.WorkspaceId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.Id, x.UserId, x.WorkspaceId, x.DepartmentId }).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<AttendanceDay>().WithMany().HasForeignKey(x => new { x.DayId, x.EmployeeId })
            .HasPrincipalKey(x => new { x.Id, x.EmployeeId }).OnDelete(DeleteBehavior.Restrict);
        request.ToTable("correction_requests", t =>
        {
            t.HasCheckConstraint("ck_correction_state", "state IN ('Draft','PendingSupervisor','PendingHr','Returned','Approved','Expired')");
            t.HasCheckConstraint("ck_correction_version", "version>=1 AND current_revision>=0");
            t.HasCheckConstraint("ck_correction_deadline", "isfinite(original_work_date) AND isfinite(snapshot_at_utc) AND isfinite(deadline_anchor_utc) AND isfinite(deadline_utc) AND deadline_anchor_utc=((original_work_date+8)::timestamp AT TIME ZONE 'Asia/Bangkok')");
        });
        var revision = model.Entity<CorrectionRevision>();
        revision.HasKey(x => new { x.RequestId, x.Revision });
        revision.HasOne<CorrectionRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        revision.HasOne<Site>().WithMany().HasForeignKey(x => new { x.InWorkspaceId, x.InProjectId, x.InSiteId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        revision.HasOne<Site>().WithMany().HasForeignKey(x => new { x.OutWorkspaceId, x.OutProjectId, x.OutSiteId })
            .HasPrincipalKey(x => new { x.WorkspaceId, x.ProjectId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        revision.Property(x => x.Reason).HasMaxLength(2000);
        revision.ToTable("correction_revisions", t =>
        {
            t.HasCheckConstraint("ck_correction_revision", "revision>=1 AND (baseline_day_version IS NULL OR baseline_day_version>=1)");
            t.HasCheckConstraint("ck_correction_proposal_time", "isfinite(work_date) AND isfinite(in_utc) AND isfinite(created_at_utc) AND work_date=(in_utc AT TIME ZONE 'Asia/Bangkok')::date AND in_utc<=created_at_utc AND (out_utc IS NULL OR (isfinite(out_utc) AND out_utc>in_utc AND out_utc<=created_at_utc))");
            t.HasCheckConstraint("ck_correction_proposal_site", "(out_utc IS NULL AND out_workspace_id IS NULL AND out_project_id IS NULL AND out_site_id IS NULL) OR (out_utc IS NOT NULL AND out_workspace_id IS NOT NULL AND out_project_id IS NOT NULL AND out_site_id IS NOT NULL)");
            t.HasCheckConstraint("ck_correction_reason", "length(btrim(reason)) BETWEEN 1 AND 2000 AND reason=btrim(reason)");
        });
        var route = model.Entity<CorrectionRouteSnapshot>();
        route.HasKey(x => new { x.RequestId, x.Revision });
        route.HasOne<CorrectionRevision>().WithMany().HasForeignKey(x => new { x.RequestId, x.Revision }).OnDelete(DeleteBehavior.Restrict);
        route.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.SupervisorId).OnDelete(DeleteBehavior.Restrict);
        route.ToTable("correction_route_snapshots", t => t.HasCheckConstraint("ck_correction_route", "membership_version>=1 AND reporting_version>=1 AND cardinality(hr_candidate_ids)>0 AND isfinite(created_at_utc)"));
        var approval = model.Entity<CorrectionApprovalAction>();
        approval.HasKey(x => new { x.RequestId, x.Revision, x.Step });
        approval.HasOne<CorrectionRouteSnapshot>().WithMany().HasForeignKey(x => new { x.RequestId, x.Revision }).OnDelete(DeleteBehavior.Restrict);
        approval.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        approval.Property(x => x.Step).HasMaxLength(20);
        approval.Property(x => x.Outcome).HasMaxLength(20);
        approval.Property(x => x.Reason).HasMaxLength(2000);
        approval.ToTable("correction_approval_actions", t =>
        {
            t.HasCheckConstraint("ck_correction_approval", "step IN ('Supervisor','Hr') AND outcome IN ('Approved','Returned') AND isfinite(occurred_at_utc)");
            t.HasCheckConstraint("ck_correction_return_reason", "outcome<>'Returned' OR (reason IS NOT NULL AND length(btrim(reason)) BETWEEN 1 AND 2000 AND reason=btrim(reason))");
        });
        var adjustment = model.Entity<AttendanceAdjustment>();
        adjustment.HasKey(x => x.Id);
        adjustment.HasIndex(x => new { x.RequestId, x.Revision }).IsUnique();
        adjustment.HasIndex(x => new { x.DayId, x.AfterVersion }).IsUnique();
        adjustment.HasOne<CorrectionRevision>().WithMany().HasForeignKey(x => new { x.RequestId, x.Revision }).OnDelete(DeleteBehavior.Restrict);
        adjustment.HasOne<AttendanceDay>().WithMany().HasForeignKey(x => x.DayId).OnDelete(DeleteBehavior.Restrict);
        adjustment.ToTable("attendance_adjustments", t =>
        {
            t.HasCheckConstraint("ck_adjustment_version", "(before_version IS NULL AND before_work_date IS NULL AND before_in_utc IS NULL AND before_out_utc IS NULL AND after_version=1) OR (before_version IS NOT NULL AND before_version>=1 AND before_work_date IS NOT NULL AND before_in_utc IS NOT NULL AND after_version=before_version+1)");
            t.HasCheckConstraint("ck_adjustment_time", "isfinite(after_work_date) AND isfinite(after_in_utc) AND isfinite(applied_at_utc) AND (after_out_utc IS NULL OR (isfinite(after_out_utc) AND after_out_utc>after_in_utc))");
        });
        var receipt = model.Entity<CorrectionCommandReceipt>();
        receipt.HasKey(x => new { x.ActorId, x.CommandId });
        receipt.HasOne<IdentityUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<CorrectionRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        receipt.Property(x => x.Action).HasMaxLength(40);
        receipt.Property(x => x.PayloadSha256).HasMaxLength(64);
        receipt.Property(x => x.ResultJson).HasColumnType("jsonb");
        receipt.ToTable("correction_command_receipts", t =>
        {
            t.HasCheckConstraint("ck_correction_receipt", "command_id<>'00000000-0000-0000-0000-000000000000'::uuid AND payload_sha256 ~ '^[a-f0-9]{64}$' AND status IN (200,201) AND isfinite(created_at_utc) AND length(action)>0 AND jsonb_typeof(result_json)='object' AND octet_length(result_json::text)<=65536");
        });
        foreach (var type in new[] { typeof(CorrectionRequest), typeof(CorrectionRevision), typeof(CorrectionRouteSnapshot), typeof(CorrectionApprovalAction), typeof(AttendanceAdjustment), typeof(CorrectionCommandReceipt) })
            foreach (var property in model.Entity(type).Metadata.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }
}
