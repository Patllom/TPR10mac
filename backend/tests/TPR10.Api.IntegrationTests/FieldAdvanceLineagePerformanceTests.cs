using System.Collections;
using TPR10.Api.FieldAdvances.Policies;

namespace TPR10.Api.IntegrationTests;

public sealed class FieldAdvanceLineagePerformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deep_and_wide_reverse_order_lineage_walks_each_edge_once(bool wide)
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToArray();
        var pairs = ids.Select((id, i) => new KeyValuePair<Guid, Guid?>(id, i == 0 ? null : ids[wide ? 0 : i - 1])).Reverse().ToDictionary();
        var unrelated = Guid.NewGuid(); pairs.Add(unrelated, null);
        var measured = new CountedParents(pairs);
        var result = PolicyLineage.Members(ids[500], measured);
        Assert.Equal(1000, result.Count);
        Assert.All(ids, id => Assert.Contains(id, result));
        Assert.DoesNotContain(unrelated, result);
        Assert.True(measured.Visited <= 2 * pairs.Count, $"Enumerated {measured.Visited} edges for {pairs.Count} nodes");
    }

    [Fact]
    public void Cycles_and_missing_ancestors_remain_invalid()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => PolicyLineage.Members(a, new Dictionary<Guid, Guid?> { [a] = b, [b] = a }));
        Assert.Throws<InvalidOperationException>(() => PolicyLineage.Members(a, new Dictionary<Guid, Guid?> { [a] = b }));
    }

    private sealed class CountedParents(Dictionary<Guid, Guid?> rows) : IReadOnlyDictionary<Guid, Guid?>
    {
        internal int Visited { get; private set; }
        public Guid? this[Guid key] => rows[key];
        public IEnumerable<Guid> Keys => rows.Keys;
        public IEnumerable<Guid?> Values => rows.Values;
        public int Count => rows.Count;
        public bool ContainsKey(Guid key) => rows.ContainsKey(key);
        public bool TryGetValue(Guid key, out Guid? value) => rows.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<Guid, Guid?>> GetEnumerator()
        {
            foreach (var pair in rows) { Visited++; yield return pair; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
