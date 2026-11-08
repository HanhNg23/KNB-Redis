using System.Security.Cryptography;
using StackExchange.Redis;

namespace CacheAsideDemo;

public sealed class RedisCache
{
    private const string AcquireLockScript = @"
if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) then
    return 1
end
return 0";

    private const string ReleaseLockScript = @"
if redis.call('GET', KEYS[1]) == ARGV[1] then
    return redis.call('DEL', KEYS[1])
end
return 0";

    public sealed record Result(Dictionary<string, string>? Record, bool Hit, double RedisLatencyMs);

    private readonly IDatabase _db;
    private readonly int _ttl;
    private readonly int _lockTtlMs;
    private readonly int _waitPollMs;

    private long _hits;
    private long _misses;
    private long _stampedesSuppressed;

    public RedisCache(IDatabase db, int ttl = 30, int lockTtlMs = 5000, int waitPollMs = 20)
    {
        _db = db;
        _ttl = ttl;
        _lockTtlMs = lockTtlMs;
        _waitPollMs = waitPollMs;
    }

    public static string CacheKey(string id) => $"cache:product:{id}";
    private static string LockKey(string id) => $"cache:lock:product:{id}";

    public Result Get(string entityId, Func<string, Dictionary<string, string>?> loader)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var entries = _db.HashGetAll(CacheKey(entityId));
        sw.Stop();
        var redisLatencyMs = sw.Elapsed.TotalMilliseconds;

        if (entries.Length > 0)
        {
            Interlocked.Increment(ref _hits);
            return new Result(ToDict(entries), true, redisLatencyMs);
        }

        Interlocked.Increment(ref _misses);
        var record = LoadWithSingleFlight(entityId, loader);
        return new Result(record, false, redisLatencyMs);
    }

    public bool Invalidate(string entityId) => _db.KeyDelete(CacheKey(entityId));

    public bool UpdateField(string entityId, string field, string value)
    {
        var cacheKey = CacheKey(entityId);
        while (true)
        {
            var tx = _db.CreateTransaction();
            tx.AddCondition(Condition.KeyExists(cacheKey));
            _ = tx.HashSetAsync(cacheKey, field, value);
            _ = tx.KeyExpireAsync(cacheKey, TimeSpan.FromSeconds(_ttl));
            if (tx.Execute()) return true;
            if (!_db.KeyExists(cacheKey)) return false;
        }
    }

    // Seconds left on the cache key; null when the key is absent.
    public double? Ttl(string entityId) => _db.KeyTimeToLive(CacheKey(entityId))?.TotalSeconds;

    public void ResetStats()
    {
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _stampedesSuppressed, 0);
    }

    public Dictionary<string, object> Stats()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var stampedes = Interlocked.Read(ref _stampedesSuppressed);
        var total = hits + misses;
        return new Dictionary<string, object>
        {
            ["hits"] = hits,
            ["misses"] = misses,
            ["stampedes_suppressed"] = stampedes,
            ["hit_rate_pct"] = total == 0 ? 0.0 : Math.Round(100.0 * hits / total, 1),
        };
    }

    private Dictionary<string, string>? LoadWithSingleFlight(
        string entityId,
        Func<string, Dictionary<string, string>?> loader)
    {
        var cacheKey = CacheKey(entityId);
        var lockKey = LockKey(entityId);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        var acquired = (long)_db.ScriptEvaluate(
            AcquireLockScript,
            new RedisKey[] { lockKey },
            new RedisValue[] { token, _lockTtlMs });

        if (acquired == 1)
        {
            try
            {
                var record = loader(entityId);
                if (record is null) return null;

                var batch = _db.CreateBatch();
                _ = batch.KeyDeleteAsync(cacheKey);
                _ = batch.HashSetAsync(cacheKey,
                    record.Select(p => new HashEntry(p.Key, p.Value)).ToArray());
                _ = batch.KeyExpireAsync(cacheKey, TimeSpan.FromSeconds(_ttl));
                batch.Execute();
                return record;
            }
            finally
            {
                _db.ScriptEvaluate(ReleaseLockScript,
                    new RedisKey[] { lockKey },
                    new RedisValue[] { token });
            }
        }

        Interlocked.Increment(ref _stampedesSuppressed);
        var deadline = Environment.TickCount64 + _lockTtlMs;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(_waitPollMs);
            var entries = _db.HashGetAll(cacheKey);
            if (entries.Length > 0) return ToDict(entries);
        }
        return loader(entityId);
    }

    private static Dictionary<string, string> ToDict(HashEntry[] entries) =>
        entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
}
