using TPR10.Api.Scopes;

namespace TPR10.Api.Attendance.Access;

public static class AttendanceCatalog
{
    public const string Domain = "attendance";
    public static IReadOnlyList<(Guid Id, string Capability, string Domain)> Permissions { get; } =
        Array.AsReadOnly<(Guid, string, string)>([
            (Guid.Parse("20000000-0000-0000-0000-000000000013"), "attendance:record", ScopeCatalog.BusinessDomain),
            (Guid.Parse("20000000-0000-0000-0000-000000000014"), "attendance:team-read", Domain),
            (Guid.Parse("20000000-0000-0000-0000-000000000015"), "attendance:hr-read", Domain),
            (Guid.Parse("20000000-0000-0000-0000-000000000016"), "attendance:approve-supervisor", Domain),
            (Guid.Parse("20000000-0000-0000-0000-000000000017"), "attendance:approve-hr", Domain),
            (Guid.Parse("20000000-0000-0000-0000-000000000018"), "attendance:directory-manage", ScopeCatalog.SystemDomain),
            (Guid.Parse("20000000-0000-0000-0000-000000000019"), "attendance:storage-manage", ScopeCatalog.SystemDomain)
        ]);
}
