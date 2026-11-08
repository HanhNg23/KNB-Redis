using CacheAsideDemo;
using StackExchange.Redis;

// The synchronous demo blocks threads while polling; a higher floor avoids slow ThreadPool ramp-up.
ThreadPool.SetMinThreads(64, 64);

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var port = config["port"] ?? "8080";
var redisHost = config["redis-host"] ?? config["REDIS_HOST"] ?? "localhost";
var redisPort = config["redis-port"] ?? config["REDIS_PORT"] ?? "6379";

builder.WebHost.UseUrls($"http://localhost:{port}");

var redis = ConnectionMultiplexer.Connect($"{redisHost}:{redisPort}");
var primary = new MockPrimaryStore(readLatencyMs: 150);
var cache = new RedisCache(redis.GetDatabase(), ttl: 30);

var app = builder.Build();

app.MapGet("/", () => Results.Content(Html.Page, "text/html"));

app.MapGet("/api/products", () => primary.Ids);

app.MapGet("/api/read/{id}", (string id) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = cache.Get(id, primary.Read);
    sw.Stop();
    if (result.Record is null) return Results.NotFound(new { error = $"Unknown product '{id}'" });

    return Results.Ok(new
    {
        record = result.Record,
        hit = result.Hit,
        redis_latency_ms = Math.Round(result.RedisLatencyMs, 2),
        total_latency_ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2),
        ttl_seconds = cache.Ttl(id),
        primary_reads = primary.Reads,
    });
});

app.MapGet("/api/ttl/{id}", (string id) => new { ttl_seconds = cache.Ttl(id) });

// Write to the primary first, then invalidate the cache key.
app.MapPost("/api/update/{id}", (string id, string field, string value) =>
{
    if (!primary.UpdateField(id, field, value)) return Results.NotFound(new { error = $"Unknown product '{id}'" });
    var invalidated = cache.Invalidate(id);
    return Results.Ok(new { updated = true, cache_invalidated = invalidated });
});

app.MapPost("/api/invalidate/{id}", (string id) => new { invalidated = cache.Invalidate(id) });

app.MapPost("/api/stampede/{id}", async (string id, int? readers) =>
{
    var n = Math.Clamp(readers ?? 50, 1, 200);
    cache.Invalidate(id);
    primary.ResetReads();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var results = await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(() => cache.Get(id, primary.Read))));
    sw.Stop();

    return new
    {
        readers = n,
        primary_reads = primary.Reads,
        elapsed_ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
        all_returned_record = results.All(r => r.Record is not null),
    };
});

app.MapGet("/api/stats", () => new { cache = cache.Stats(), primary_reads = primary.Reads });

app.MapPost("/api/reset", () =>
{
    cache.ResetStats();
    primary.ResetReads();
    return new { reset = true };
});

app.Run();
