using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuctionLoadTest;

/// <summary>
/// One monotonic clock for every measurement in the run, read as UTC. Wall-clock readings can jump
/// when Windows adjusts the time; a latency must never come out negative because of that.
/// </summary>
static class Clock
{
    private static readonly long BaseTs = Stopwatch.GetTimestamp();
    private static readonly DateTime BaseUtc = DateTime.UtcNow;

    public static long Ts => Stopwatch.GetTimestamp();
    public static DateTime Utc(long ts) => BaseUtc + Stopwatch.GetElapsedTime(BaseTs, ts);
    public static DateTime Now => Utc(Ts);
    public static double Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
}

static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Pretty = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = indented };
        options.Converters.Add(new JsonStringEnumConverter());
        options.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
        return options;
    }
}

/// <summary>--name value pairs and bare --flags.</summary>
sealed class Args
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public Args(IEnumerable<string> args)
    {
        string? pending = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null) _values[pending] = "true";
                pending = arg[2..];
            }
            else if (pending is not null)
            {
                _values[pending] = arg;
                pending = null;
            }
        }
        if (pending is not null) _values[pending] = "true";
    }

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
    public string Require(string name) => Get(name) ?? throw new ArgumentException($"--{name} is required.");
    public string Get(string name, string fallback) => Get(name) ?? fallback;
    public int Int(string name, int fallback) => Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
    public double Double(string name, double fallback) => Get(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;
    public bool Flag(string name) => Get(name) is { } v && v != "false";
}

/// <summary>Percentiles of a set of exact samples, in milliseconds.</summary>
sealed record Summary(long Count, double Mean, double P50, double P95, double P99, double Max)
{
    public static readonly Summary Empty = new(0, 0, 0, 0, 0, 0);

    public static Summary Of(IEnumerable<double> values)
    {
        var sorted = values.ToArray();
        if (sorted.Length == 0) return Empty;
        Array.Sort(sorted);
        return new Summary(sorted.Length, sorted.Average(), At(sorted, 0.50), At(sorted, 0.95), At(sorted, 0.99), sorted[^1]);
    }

    private static double At(double[] sorted, double share) =>
        sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Length) - 1, 0, sorted.Length - 1)];

    public string Text => Count == 0 ? "—" : $"{F(P50)} / {F(P95)} / {F(P99)} (max {F(Max)})";

    public static string F(double ms) => ms >= 10_000 ? $"{ms / 1000:0.0} s" : ms >= 100 ? $"{ms:0} ms" : $"{ms:0.0} ms";
}

/// <summary>
/// For the millions of "bid seen by client" samples: 2% wide buckets, so memory stays fixed however
/// long the run. Values below zero (clock estimate error) are counted apart and kept as zero.
/// </summary>
sealed class FineHistogram
{
    private const double BaseMs = 0.01;
    private const double Growth = 1.02;
    private const int Buckets = 1100;
    private static readonly double LogGrowth = Math.Log(Growth);

    private readonly long[] _counts = new long[Buckets];
    private long _count;
    private double _sum;
    private double _max;
    public long Negative { get; private set; }

    public void Record(double ms)
    {
        if (ms < 0) { Negative++; ms = 0; }
        var index = ms <= BaseMs ? 0 : Math.Min(Buckets - 1, 1 + (int)Math.Floor(Math.Log(ms / BaseMs) / LogGrowth));
        _counts[index]++;
        _count++;
        _sum += ms;
        if (ms > _max) _max = ms;
    }

    public void Merge(FineHistogram other)
    {
        for (var i = 0; i < Buckets; i++) _counts[i] += other._counts[i];
        _count += other._count;
        _sum += other._sum;
        _max = Math.Max(_max, other._max);
        Negative += other.Negative;
    }

    public Summary Summary()
    {
        if (_count == 0) return AuctionLoadTest.Summary.Empty;
        return new Summary(_count, _sum / _count, At(0.50), At(0.95), At(0.99), _max);
    }

    private double At(double share)
    {
        var wanted = Math.Max(1, (long)Math.Ceiling(_count * share));
        long seen = 0;
        for (var i = 0; i < Buckets; i++)
        {
            seen += _counts[i];
            if (seen >= wanted) return Math.Min(_max, BaseMs * Math.Pow(Growth, i));
        }
        return _max;
    }
}

/// <summary>
/// One window of a server histogram from the diagnostics endpoint: the counts at the end minus the
/// counts at the start. Bucket i holds durations up to 0.1 ms × 1.1^i, the server's own layout.
/// </summary>
sealed record ServerWindow(long Count, double MeanMs, double P50, double P95, double P99, double MaxBound)
{
    public static readonly ServerWindow Empty = new(0, 0, 0, 0, 0, 0);

    public static ServerWindow Between(ServerHistogram? before, ServerHistogram? after)
    {
        if (after is null) return Empty;

        var buckets = new SortedDictionary<int, long>();
        foreach (var (key, n) in after.Buckets)
        {
            var index = int.Parse(key, CultureInfo.InvariantCulture);
            var previous = before?.Buckets.TryGetValue(key, out var b) == true ? b : 0;
            if (n - previous > 0) buckets[index] = n - previous;
        }

        var count = buckets.Values.Sum();
        if (count == 0) return Empty;
        var sum = after.SumMs - (before?.SumMs ?? 0);

        double At(double share)
        {
            var wanted = Math.Max(1, (long)Math.Ceiling(count * share));
            long seen = 0;
            foreach (var (index, n) in buckets)
            {
                seen += n;
                if (seen >= wanted) return Upper(index);
            }
            return 0;
        }

        return new ServerWindow(count, sum / count, At(0.50), At(0.95), At(0.99), Upper(buckets.Keys.Max()));
    }

    private static double Upper(int index) => 0.1 * Math.Pow(1.1, index);

    public string Text => Count == 0 ? "—" : $"{Summary.F(P50)} / {Summary.F(P95)} / {Summary.F(P99)}";
}

// ------------------------------------------------------------------ API shapes (camelCase JSON)

sealed record ApiError(string? Code, string? Message);

sealed record BidResultDto(
    Guid BidId, Guid AuctionId, long SequenceNumber, decimal PreviousPrice, decimal NewPrice,
    decimal TargetPrice, bool ReachedTarget, bool FinalPhase, string AuctionStatus, int MyRank,
    decimal MyHighestPrice, DateTime ServerTimestamp, bool WasDuplicate);

sealed record ParticipationDto(Guid ParticipantId, Guid AuctionId, decimal EntryFeePaid, string Status);

sealed record SnapshotDto(
    Guid Id, string Status, decimal CurrentPrice, decimal StartingPrice, decimal TargetPrice,
    decimal FinalPhasePrice, decimal BidIncrement, int ParticipantCount, int RequiredParticipants,
    int? RemainingSeconds, DateTime? EndsAt, bool TargetReached, long LastSequenceNumber,
    DateTime ServerTime, Guid? MyParticipantId, Guid? LastBidParticipantId, bool IsDemo);

sealed record DetailDto(
    Guid Id, string AuctionNumber, string Title, decimal OriginalPrice, decimal StartingPrice,
    decimal CurrentPrice, decimal TargetPercentage, decimal TargetPrice, decimal BidIncrement,
    decimal EntryFee, int ParticipantCount, int RequiredParticipants, string Status,
    DateTime? ActualStartedAt, DateTime? EndsAt, DateTime? FinishedAt, int DurationSeconds, bool IsDemo);

sealed record AuctionListDto(List<AuctionListItem> Items);
sealed record AuctionListItem(Guid Id, string AuctionNumber, string Title, string Status, DateTime? ScheduledStartAt, int ParticipantCount);

sealed record BidBroadcast(
    Guid BidId, long SequenceNumber, Guid ParticipantId, string DisplayName, decimal PreviousPrice,
    decimal NewPrice, decimal TargetPrice, decimal Progress, bool ReachedTarget, DateTime ServerTimestamp);

sealed record PriceChanged(Guid AuctionId, decimal CurrentPrice);

sealed record LeaderboardMessage(Guid AuctionId, List<LeaderboardEntry> Leaderboard);

sealed record LeaderboardEntry(
    int Rank, Guid ParticipantId, Guid UserId, string DisplayName, decimal HighestPrice,
    DateTime? HighestPriceAt, int BidCount);

sealed record WinnerDto(int Rank, Guid UserId, string DisplayName, decimal HighestPrice, DateTime? HighestPriceAt, decimal PaymentAmount);

sealed record AuthResult(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt);

// The diagnostics endpoint (AdminDiagnosticsController), only the parts the report reads.
sealed record RuntimeSnapshotDto(
    DateTime ServerTime, double UptimeSeconds, int ProcessorCount, ProcessDto Process, GcDto Gc,
    ThreadPoolDto ThreadPool, Dictionary<string, double> Gauges, Dictionary<string, double> Totals,
    Dictionary<string, double>? Peaks, Dictionary<string, ServerHistogram> Histograms);

sealed record ProcessDto(double CpuTimeMs, long WorkingSetBytes, long PrivateBytes, int Threads);
sealed record GcDto(long HeapSizeBytes, long TotalAllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, double TotalPauseMs);
sealed record ThreadPoolDto(int Threads, long PendingWorkItems, long CompletedWorkItems, int MinWorkerThreads, int MinIoThreads);
sealed record ServerHistogram(long Count, double SumMs, double MaxMs, Dictionary<string, long> Buckets);
