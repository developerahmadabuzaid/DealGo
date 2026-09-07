using System.Collections.Concurrent;
using System.Text.Json;
using AuctionPlatform.Application.Common;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// Redis-backed distributed lock. Used to keep scheduled work (auction start, completion, expiry)
/// from running twice when several API instances wake up at the same moment.
///
/// This is a coordination aid only: correctness of money and bids rests on PostgreSQL row locks
/// and unique constraints, so losing Redis degrades efficiency, not integrity.
/// </summary>
public sealed class RedisDistributedLockService : IDistributedLockService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisDistributedLockService> _logger;

    public RedisDistributedLockService(IConnectionMultiplexer redis, ILogger<RedisDistributedLockService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> AcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var token = Guid.NewGuid().ToString("N");

            var acquired = await db.StringSetAsync($"lock:{key}", token, ttl, When.NotExists);
            return acquired ? new RedisLockHandle(db, $"lock:{key}", token) : null;
        }
        catch (Exception ex)
        {
            // Failing open keeps the platform running; the database guards still prevent duplicates.
            _logger.LogWarning(ex, "Distributed lock {Key} could not be acquired; proceeding without it.", key);
            return new NoopLockHandle();
        }
    }

    private sealed class RedisLockHandle : IAsyncDisposable
    {
        private const string ReleaseScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

        private readonly IDatabase _db;
        private readonly string _key;
        private readonly string _token;

        public RedisLockHandle(IDatabase db, string key, string token)
        {
            _db = db;
            _key = key;
            _token = token;
        }

        /// <summary>Releases only if we still own the lock, so an expired lock is never stolen back.</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await _db.ScriptEvaluateAsync(ReleaseScript, new RedisKey[] { _key }, new RedisValue[] { _token });
            }
            catch
            {
                // The TTL will clear it.
            }
        }
    }

    private sealed class NoopLockHandle : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Fixed-window counter in Redis, used for per-user bid throttling.</summary>
public sealed class RedisRateLimiterService : IRateLimiterService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisRateLimiterService> _logger;

    public RedisRateLimiterService(IConnectionMultiplexer redis, ILogger<RedisRateLimiterService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var redisKey = $"rate:{key}";

            var count = await db.StringIncrementAsync(redisKey);
            if (count == 1)
            {
                await db.KeyExpireAsync(redisKey, window);
            }

            return count <= limit;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rate limiter unavailable for {Key}; allowing the request.", key);
            return true;
        }
    }
}

public sealed class RedisCacheService : ICacheService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _prefix;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(IConnectionMultiplexer redis, string prefix, ILogger<RedisCacheService> logger)
    {
        _redis = redis;
        _prefix = prefix;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await _redis.GetDatabase().StringGetAsync(_prefix + key);
            return value.IsNullOrEmpty ? default : JsonSerializer.Deserialize<T>((string)value!);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache read failed for {Key}.", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase().StringSetAsync(_prefix + key, JsonSerializer.Serialize(value), ttl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache write failed for {Key}.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase().KeyDeleteAsync(_prefix + key);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache delete failed for {Key}.", key);
        }
    }

    public async Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var endpoint in _redis.GetEndPoints())
            {
                var server = _redis.GetServer(endpoint);
                if (!server.IsConnected || server.IsReplica) continue;

                var keys = server.Keys(pattern: $"{_prefix}{prefix}*").ToArray();
                if (keys.Length > 0)
                {
                    await _redis.GetDatabase().KeyDeleteAsync(keys);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache prefix invalidation failed for {Prefix}.", prefix);
        }
    }
}

// ---------------------------------------------------------------------------
// In-process fallbacks. These let a developer run the API without Redis; they are
// explicitly single-instance and are never selected when Redis is configured.
// ---------------------------------------------------------------------------

public sealed class InMemoryDistributedLockService : IDistributedLockService
{
    private static readonly ConcurrentDictionary<string, DateTime> Locks = new();

    public Task<IAsyncDisposable?> AcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var expiry = now.Add(ttl);

        var acquired = Locks.AddOrUpdate(key, expiry, (_, existing) => existing < now ? expiry : existing) == expiry;

        return Task.FromResult<IAsyncDisposable?>(acquired ? new Handle(key) : null);
    }

    private sealed class Handle : IAsyncDisposable
    {
        private readonly string _key;
        public Handle(string key) => _key = key;

        public ValueTask DisposeAsync()
        {
            Locks.TryRemove(_key, out _);
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class InMemoryRateLimiterService : IRateLimiterService
{
    private static readonly ConcurrentDictionary<string, (DateTime WindowStart, int Count)> Counters = new();

    public Task<bool> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var entry = Counters.AddOrUpdate(key,
            _ => (now, 1),
            (_, existing) => now - existing.WindowStart > window ? (now, 1) : (existing.WindowStart, existing.Count + 1));

        return Task.FromResult(entry.Count <= limit);
    }
}

public sealed class InMemoryCacheService : ICacheService
{
    private static readonly ConcurrentDictionary<string, (DateTime Expiry, string Json)> Entries = new();

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (Entries.TryGetValue(key, out var entry) && entry.Expiry > DateTime.UtcNow)
        {
            return Task.FromResult(JsonSerializer.Deserialize<T>(entry.Json));
        }
        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        Entries[key] = (DateTime.UtcNow.Add(ttl), JsonSerializer.Serialize(value));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        Entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RemoveByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        foreach (var key in Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            Entries.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }
}
