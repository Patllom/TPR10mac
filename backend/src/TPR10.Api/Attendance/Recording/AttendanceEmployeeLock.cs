using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Data;

namespace TPR10.Api.Attendance.Recording;

public static class AttendanceEmployeeLock
{
    public static async Task AcquireAsync(Tpr10DbContext db, Guid employeeId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Attendance lock requires a caller-owned identity transaction.");
        if (employeeId == Guid.Empty) throw new ArgumentException("Employee ID is required.", nameof(employeeId));
        var identityHeld = await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid=pg_backend_pid() AND locktype='advisory'
                AND classid=0 AND objid=7241002 AND objsubid=1 AND mode='ExclusiveLock' AND granted) AS "Value"
            """).SingleAsync(ct);
        if (!identityHeld) throw new InvalidOperationException("Acquire the identity lock before the attendance employee lock.");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(employeeId.ToString("D")));
        var key = BinaryPrimitives.ReadInt32BigEndian(digest);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(7241003,{key})", ct);
    }
}
