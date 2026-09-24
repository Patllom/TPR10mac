namespace TPR10.Api.Attendance.Directory;

public sealed class ReportingLine
{
    public Guid Id { get; set; }
    public Guid EmployeeMembershipId { get; set; }
    public Guid EmployeeUserId { get; set; }
    public Guid SupervisorUserId { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset ValidFromUtc { get; set; }
    public DateTimeOffset? ValidToUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? EndedBy { get; set; }
    public required string Reason { get; set; }
}
