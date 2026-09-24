using TPR10.Api.Attendance.Directory;

namespace TPR10.Api.Attendance;

public static class AttendanceRegistration
{
    public static IServiceCollection AddAttendance(this IServiceCollection services)
    {
        services.AddScoped<DirectoryService>();
        return services;
    }
}
