using Microsoft.EntityFrameworkCore;
using TPR10.Api.Attendance.Access;
using TPR10.Api.Attendance.Evidence;
using TPR10.Api.Attendance.Recording;
using TPR10.Api.Data;
using TPR10.Api.Organization.Data;

namespace TPR10.Api.IntegrationTests;

internal static class AttendanceEvidenceTestData
{
    // Synthetic GPS only. Call inside the publication transaction; never relax production guards.
    internal static async Task<AttendanceEvent> AddEventAsync(Tpr10DbContext db, Guid eventId,
        Guid evidenceId, EmploymentSnapshot subject, EvidenceAction action = EvidenceAction.CheckIn, Guid? dayId = null)
    {
        var at = subject.OccurredAtUtc;
        var day = dayId ?? Guid.NewGuid();
        if (dayId is null)
            db.Add(new AttendanceDay
            {
                Id = day,
                EmployeeId = subject.EmployeeId,
                MembershipId = subject.MembershipId,
                WorkspaceId = subject.WorkspaceId,
                DepartmentId = subject.DepartmentId,
                SnapshotAtUtc = at,
                EffectiveInUtc = at,
                WorkDate = DateOnly.FromDateTime(at.UtcDateTime.AddHours(7))
            });
        var project = new Project
        {
            Id = Guid.NewGuid(),
            WorkspaceId = subject.WorkspaceId,
            Code = "P" + Guid.NewGuid().ToString("N").ToUpperInvariant(),
            Name = "โครงการทดสอบ",
            CreatedBy = subject.EmployeeId,
            CreatedAtUtc = at
        };
        var site = new Site
        {
            Id = Guid.NewGuid(),
            WorkspaceId = subject.WorkspaceId,
            ProjectId = project.Id,
            Code = "S" + Guid.NewGuid().ToString("N").ToUpperInvariant(),
            Name = "จุดทดสอบ",
            CreatedBy = subject.EmployeeId,
            CreatedAtUtc = at
        };
        db.Add(project); db.Add(site);
        var row = new AttendanceEvent
        {
            Id = eventId,
            DayId = day,
            ActorId = subject.EmployeeId,
            Action = action,
            OccurredAtUtc = at,
            DeviceCapturedAtUtc = at,
            WorkspaceId = subject.WorkspaceId,
            ProjectId = project.Id,
            SiteId = site.Id,
            Latitude = 13,
            Longitude = 100,
            AccuracyMeters = 10,
            GpsSampledAtUtc = at,
            EvidenceId = evidenceId
        };
        db.Add(row);
        await db.SaveChangesAsync();
        return row;
    }
}
