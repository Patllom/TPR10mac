using System.Buffers;
using System.Text;

namespace TPR10.Api.Attendance.Directory;

public static class DirectoryRules
{
    public static bool Contains(DateTimeOffset from, DateTimeOffset? to, DateTimeOffset at) =>
        from <= at && (to is null || at < to);

    public static bool CreatesCycle(Guid employee, Guid proposedSupervisor, IReadOnlyDictionary<Guid, Guid> currentManagers)
    {
        if (employee == Guid.Empty) return true;
        var visited = new HashSet<Guid>();
        var cursor = proposedSupervisor;
        while (true)
        {
            if (cursor == Guid.Empty || cursor == employee || !visited.Add(cursor)) return true;
            if (!currentManagers.TryGetValue(cursor, out cursor)) return false;
        }
    }

    public static bool ValidReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Any(char.IsControl)) return false;
        var remaining = reason.AsSpan().Trim();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done || ++count > 500)
                return false;
            remaining = remaining[consumed..];
        }
        return count > 0;
    }
}
