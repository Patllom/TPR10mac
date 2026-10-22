namespace TPR10.Api.FieldAdvances.Policies;

// Topology of one exact-scope lineage; company bases are not parent edges.
public static class PolicyLineage
{
    public static HashSet<Guid> Members(Guid start, IReadOnlyDictionary<Guid, Guid?> parents)
    {
        var visited = new HashSet<Guid>();
        var root = start;
        while (true)
        {
            if (!visited.Add(root) || !parents.TryGetValue(root, out var parent))
                throw new InvalidOperationException("Invalid policy lineage.");
            if (parent is null) break;
            root = parent.Value;
        }
        var lineage = new HashSet<Guid> { root };
        var children = new Dictionary<Guid, List<Guid>>();
        foreach (var pair in parents)
        {
            if (pair.Value is not { } parent) continue;
            if (!children.TryGetValue(parent, out var siblings)) children[parent] = siblings = [];
            siblings.Add(pair.Key);
        }
        var queue = new Queue<Guid>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var current))
            if (children.TryGetValue(current, out var descendants))
                foreach (var child in descendants)
                    if (lineage.Add(child)) queue.Enqueue(child);
        return lineage;
    }
}
