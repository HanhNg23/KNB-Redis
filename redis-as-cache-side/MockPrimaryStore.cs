using System.Collections.Concurrent;

namespace CacheAsideDemo;

// Stand-in for a slow database; every Read sleeps to make hit vs miss visible.
public sealed class MockPrimaryStore
{
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _data = new();
    private long _reads;

    public int ReadLatencyMs { get; }
    public long Reads => Interlocked.Read(ref _reads);

    public MockPrimaryStore(int readLatencyMs = 150)
    {
        ReadLatencyMs = readLatencyMs;
        Seed(id: "p-001", name: "Sourdough Loaf", priceCents: 650, stock: 42);
        Seed(id: "p-002", name: "Butter Croissant", priceCents: 375, stock: 120);
        Seed(id: "p-003", name: "Espresso Beans 1kg", priceCents: 2400, stock: 18);
        Seed(id: "p-004", name: "Oat Milk 1L", priceCents: 299, stock: 75);
        Seed(id: "p-005", name: "Dark Chocolate Bar", priceCents: 450, stock: 200);
    }

    public IEnumerable<string> Ids => _data.Keys.OrderBy(k => k);

    public Dictionary<string, string>? Read(string id)
    {
        Thread.Sleep(ReadLatencyMs);
        Interlocked.Increment(ref _reads);
        return _data.TryGetValue(id, out var record) ? Snapshot(record) : null;
    }

    public bool UpdateField(string id, string field, string value)
    {
        if (!_data.TryGetValue(id, out var record)) return false;
        lock (record) record[field] = value;
        return true;
    }

    public void ResetReads() => Interlocked.Exchange(ref _reads, 0);

    private void Seed(string id, string name, int priceCents, int stock) =>
        _data[id] = new Dictionary<string, string>
        {
            ["id"] = id,
            ["name"] = name,
            ["price_cents"] = priceCents.ToString(),
            ["stock"] = stock.ToString(),
        };

    private static Dictionary<string, string> Snapshot(Dictionary<string, string> record)
    {
        lock (record) return new Dictionary<string, string>(record);
    }
}
