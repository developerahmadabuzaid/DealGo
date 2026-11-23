using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AuctionPlatform.Application.Common;

namespace AuctionPlatform.Infrastructure.Diagnostics;

/// <summary>
/// What the server is doing right now, readable from the admin API while an auction runs.
///
/// Shared hosting gives no way to attach a profiler or a counters tool, so the process watches
/// itself: it listens to the metrics the database driver, ASP.NET Core, SignalR and the platform
/// already publish, and reports them together with CPU, memory and the thread pool. Everything is
/// a running total since the process started; a reader that wants one window (a load test, one
/// auction) takes a reading before and after and subtracts. Nothing here touches a request: the
/// cost is a few hundred nanoseconds per recorded measurement.
/// </summary>
public sealed class RuntimeMetrics : IDisposable
{
    private static readonly HashSet<string> Meters = new(StringComparer.Ordinal)
    {
        "Npgsql",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Http.Connections",
        AppMetrics.MeterName
    };

    /// <summary>Tags worth keeping apart. Every other tag is summed over.</summary>
    private static readonly HashSet<string> KeptTags = new(StringComparer.Ordinal)
    {
        "db.client.connection.state",
        "operation"
    };

    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, double> _totals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _peaks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LogHistogram> _histograms = new(StringComparer.Ordinal);
    private readonly object _observeLock = new();
    private Dictionary<string, double>? _observing;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    public RuntimeMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (Meters.Contains(instrument.Meter.Name)) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.SetMeasurementEventCallback<float>((i, v, t, s) => OnMeasurement(i, v, t, s));
        _listener.SetMeasurementEventCallback<long>((i, v, t, s) => OnMeasurement(i, v, t, s));
        _listener.SetMeasurementEventCallback<int>((i, v, t, s) => OnMeasurement(i, v, t, s));
        _listener.Start();
    }

    private void OnMeasurement(
        Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var isTime = string.Equals(instrument.Unit, "s", StringComparison.Ordinal);

        if (instrument.GetType().Name.StartsWith("Histogram", StringComparison.Ordinal))
        {
            var ms = isTime ? value * 1000d : value;

            if (instrument.Name == "http.server.request.duration")
            {
                // Per endpoint, named by its route template so every auction shares one entry.
                // Requests with no route — static files, the app's own pages — are left out.
                string? route = null, method = null;
                var status = 0;
                foreach (var tag in tags)
                {
                    switch (tag.Key)
                    {
                        case "http.route": route = tag.Value as string; break;
                        case "http.request.method": method = tag.Value as string; break;
                        case "http.response.status_code": status = Convert.ToInt32(tag.Value); break;
                    }
                }
                if (route is null) return;

                var endpoint = $"{method} {route}";
                _histograms.GetOrAdd($"http {endpoint}", _ => new LogHistogram()).Record(ms);
                _totals.AddOrUpdate($"http.responses {endpoint} {status / 100}xx", 1, (_, n) => n + 1);
                return;
            }

            _histograms.GetOrAdd(Key(instrument.Name, tags), _ => new LogHistogram()).Record(ms);
            return;
        }

        var key = Key(instrument.Name, tags);

        if (instrument.IsObservable)
        {
            // Only reached from RecordObservableInstruments, inside Snapshot's lock. Several pools
            // report the same instrument, so the readings of one round are added together.
            var round = _observing;
            if (round is not null) round[key] = round.GetValueOrDefault(key) + value;
            return;
        }

        var now = _totals.AddOrUpdate(key, value, (_, total) => total + value);

        // Up-down counters (requests waiting for a connection, commands running, requests in
        // flight) rise and fall between two readings; the highest level in between is what matters.
        if (instrument.GetType().Name.StartsWith("UpDownCounter", StringComparison.Ordinal))
            _peaks.AddOrUpdate(key, now, (_, peak) => Math.Max(peak, now));
    }

    private static readonly ConcurrentDictionary<string, string> PoolIds = new(StringComparer.Ordinal);

    private static string Key(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        // The application's pool and the health check's are separate pools of up to 100 each.
        // Their sizes and counts are kept apart under a short id, never the pool's name: that is
        // its connection string.
        var perPool = name is "db.client.connection.count" or "db.client.connection.max";

        string? kept = null;
        foreach (var tag in tags)
        {
            string part;
            if (perPool && tag.Key == "db.client.connection.pool.name")
                part = $"pool={PoolIds.GetOrAdd(tag.Value?.ToString() ?? string.Empty, ShortId)}";
            else if (KeptTags.Contains(tag.Key))
                part = $"{tag.Key.Split('.')[^1]}={tag.Value}";
            else
                continue;
            kept = kept is null ? part : $"{kept},{part}";
        }
        return kept is null ? name : $"{name}{{{kept}}}";
    }

    private static string ShortId(string poolName) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(poolName)))[..6].ToLowerInvariant();

    public RuntimeSnapshot Snapshot()
    {
        Dictionary<string, double> gauges;
        lock (_observeLock)
        {
            _observing = new Dictionary<string, double>(StringComparer.Ordinal);
            _listener.RecordObservableInstruments();
            gauges = _observing;
            _observing = null;
        }

        // Highest level since the previous reading, then start again from where things stand now.
        var peaks = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var key in _peaks.Keys)
        {
            var current = _totals.GetValueOrDefault(key);
            peaks[key] = Math.Max(_peaks.GetValueOrDefault(key), current);
            _peaks[key] = current;
        }

        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);

        return new RuntimeSnapshot(
            DateTime.UtcNow,
            (DateTime.UtcNow - _startedAt).TotalSeconds,
            Environment.ProcessorCount,
            new ProcessStats(
                process.TotalProcessorTime.TotalMilliseconds,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                process.Threads.Count),
            new GcStats(
                gc.HeapSizeBytes,
                GC.GetTotalAllocatedBytes(),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                GC.GetTotalPauseDuration().TotalMilliseconds),
            new ThreadPoolStats(
                ThreadPool.ThreadCount,
                ThreadPool.PendingWorkItemCount,
                ThreadPool.CompletedWorkItemCount,
                minWorkers,
                minIo),
            gauges,
            new SortedDictionary<string, double>(_totals, StringComparer.Ordinal),
            peaks,
            new SortedDictionary<string, HistogramSnapshot>(
                _histograms.ToDictionary(h => h.Key, h => h.Value.Snapshot()), StringComparer.Ordinal));
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// Durations in buckets that each cover 10% more than the one before, from 0.1 ms to several
/// minutes: within 10% at every scale, in a fixed few hundred longs, safe to record into from any
/// thread. Bucket i holds values up to <see cref="UpperBoundMs"/>(i).
/// </summary>
public sealed class LogHistogram
{
    public const double BaseMs = 0.1;
    public const double Growth = 1.1;
    public const int BucketCount = 170;

    private static readonly double LogGrowth = Math.Log(Growth);

    private readonly long[] _buckets = new long[BucketCount];
    private long _count;
    private long _sumMicros;
    private long _maxMicros;

    public static int IndexOf(double ms) =>
        ms <= BaseMs ? 0 : Math.Min(BucketCount - 1, 1 + (int)Math.Floor(Math.Log(ms / BaseMs) / LogGrowth));

    public static double UpperBoundMs(int index) => BaseMs * Math.Pow(Growth, index);

    public void Record(double ms)
    {
        if (double.IsNaN(ms) || ms < 0) return;

        Interlocked.Increment(ref _buckets[IndexOf(ms)]);
        Interlocked.Increment(ref _count);

        var micros = (long)(ms * 1000d);
        Interlocked.Add(ref _sumMicros, micros);

        long seen;
        while (micros > (seen = Interlocked.Read(ref _maxMicros)) &&
               Interlocked.CompareExchange(ref _maxMicros, micros, seen) != seen)
        {
        }
    }

    public HistogramSnapshot Snapshot()
    {
        var buckets = new SortedDictionary<int, long>();
        for (var i = 0; i < BucketCount; i++)
        {
            var n = Interlocked.Read(ref _buckets[i]);
            if (n > 0) buckets[i] = n;
        }

        var count = Interlocked.Read(ref _count);
        return new HistogramSnapshot(
            count,
            Interlocked.Read(ref _sumMicros) / 1000d,
            Interlocked.Read(ref _maxMicros) / 1000d,
            Percentile(buckets, count, 0.50),
            Percentile(buckets, count, 0.95),
            Percentile(buckets, count, 0.99),
            buckets);
    }

    /// <summary>The upper bound of the bucket holding the given share of the values.</summary>
    public static double Percentile(IReadOnlyDictionary<int, long> buckets, long count, double share)
    {
        if (count <= 0) return 0;

        var wanted = Math.Max(1, (long)Math.Ceiling(count * share));
        long seen = 0;
        foreach (var (index, n) in buckets.OrderBy(b => b.Key))
        {
            seen += n;
            if (seen >= wanted) return Math.Round(UpperBoundMs(index), 3);
        }
        return 0;
    }
}

public sealed record RuntimeSnapshot(
    DateTime ServerTime,
    double UptimeSeconds,
    int ProcessorCount,
    ProcessStats Process,
    GcStats Gc,
    ThreadPoolStats ThreadPool,
    // Readings taken just now: connections in use and idle, the pool's size.
    IReadOnlyDictionary<string, double> Gauges,
    // Running totals since start. Up-down counters (requests waiting for a connection, active
    // requests, SignalR connections) are current values; plain counters only ever grow.
    IReadOnlyDictionary<string, double> Totals,
    // For the up-down counters among the totals: the highest level since the previous reading.
    // Each reading starts the next window, so these belong to whoever reads every second.
    IReadOnlyDictionary<string, double> Peaks,
    IReadOnlyDictionary<string, HistogramSnapshot> Histograms);

public sealed record ProcessStats(double CpuTimeMs, long WorkingSetBytes, long PrivateBytes, int Threads);

public sealed record GcStats(
    long HeapSizeBytes, long TotalAllocatedBytes, int Gen0Collections, int Gen1Collections,
    int Gen2Collections, double TotalPauseMs);

public sealed record ThreadPoolStats(
    int Threads, long PendingWorkItems, long CompletedWorkItems, int MinWorkerThreads, int MinIoThreads);

/// <summary>
/// Durations in milliseconds since the process started. Percentiles are bucket upper bounds, so
/// they read up to 10% high, never low. <see cref="Buckets"/> maps bucket index to count; see
/// <see cref="LogHistogram"/> for the bucket edges.
/// </summary>
public sealed record HistogramSnapshot(
    long Count, double SumMs, double MaxMs, double P50Ms, double P95Ms, double P99Ms,
    IReadOnlyDictionary<int, long> Buckets);
