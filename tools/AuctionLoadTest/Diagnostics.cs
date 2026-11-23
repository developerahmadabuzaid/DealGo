namespace AuctionLoadTest;

/// <summary>
/// Reads the server's runtime diagnostics once a second for as long as a scenario runs. Everything
/// server-side in the report — CPU, memory, connections, lock waits — comes from these readings.
/// </summary>
sealed class DiagnosticsSampler
{
    private readonly Api _api;
    private readonly string _token;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public List<(long Ts, RuntimeSnapshotDto Snapshot, double RequestMs)> Samples { get; } = new();
    public int Failures { get; private set; }
    public string? LastFailure { get; private set; }

    public DiagnosticsSampler(Api api, string token, TimeSpan interval)
    {
        _api = api;
        _token = token;
        _interval = interval;
    }

    /// <summary>A reading right now, for windows shorter than the sampling interval.</summary>
    public async Task<long> MarkAsync()
    {
        await SampleAsync();
        return Clock.Ts;
    }

    public void Start()
    {
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await SampleAsync();
                try { await Task.Delay(_interval, token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    private async Task SampleAsync()
    {
        var r = await _api.DiagnosticsAsync(_token);
        if (r.Ok && r.Value is not null)
        {
            lock (Samples) Samples.Add((r.EndTs, r.Value, r.Ms));
        }
        else
        {
            Failures++;
            LastFailure = $"{r.Status} {r.Code}";
        }
    }

    public async Task StopAsync()
    {
        _stop?.Cancel();
        if (_loop is not null) await _loop;
        await SampleAsync();   // the closing reading
    }

    public ServerSummary Summarise(long fromTs, long toTs)
    {
        List<(long Ts, RuntimeSnapshotDto S, double Ms)> window;
        lock (Samples)
        {
            // One reading before the window so the first second of it has something to subtract from.
            var before = Samples.LastOrDefault(s => s.Ts < fromTs);
            window = Samples.Where(s => s.Ts >= fromTs && s.Ts <= toTs).ToList();
            if (before.Snapshot is not null) window.Insert(0, before);
        }

        if (window.Count < 2) return ServerSummary.Unavailable(Failures, LastFailure);

        var first = window[0].S;
        var last = window[^1].S;

        var cpuPercents = new List<double>();
        var coresUsed = new List<double>();
        for (var i = 1; i < window.Count; i++)
        {
            var a = window[i - 1].S;
            var b = window[i].S;
            var wallMs = (b.ServerTime - a.ServerTime).TotalMilliseconds;
            if (wallMs <= 0) continue;
            var cores = (b.Process.CpuTimeMs - a.Process.CpuTimeMs) / wallMs;
            coresUsed.Add(cores);
            cpuPercents.Add(100 * cores / Math.Max(1, b.ProcessorCount));
        }

        double Max(Func<RuntimeSnapshotDto, double> pick) => window.Max(s => pick(s.S));
        double SumOf(RuntimeSnapshotDto s, string prefix, string has) =>
            s.Gauges.Where(g => g.Key.StartsWith(prefix, StringComparison.Ordinal) && g.Key.Contains(has, StringComparison.Ordinal)).Sum(g => g.Value);
        double LargestPool(RuntimeSnapshotDto s) =>
            s.Gauges.Where(g => g.Key.StartsWith("db.client.connection.max", StringComparison.Ordinal)).Select(g => g.Value).DefaultIfEmpty(0).Max();
        double Total(RuntimeSnapshotDto s, string key) => s.Totals.TryGetValue(key, out var v) ? v : 0;
        // The true highest level between readings where the server reports it, else the reading.
        double Peak(RuntimeSnapshotDto s, string key) =>
            s.Peaks is not null && s.Peaks.TryGetValue(key, out var p) ? Math.Max(p, Total(s, key)) : Total(s, key);
        ServerWindow Histogram(string key) => ServerWindow.Between(
            first.Histograms.GetValueOrDefault(key), last.Histograms.GetValueOrDefault(key));

        var seconds = Math.Max(0.001, (last.ServerTime - first.ServerTime).TotalSeconds);
        const string bids = "POST api/v1/auctions/{id:guid}/bids";
        const string join = "POST api/v1/auctions/{id:guid}/join";

        return new ServerSummary(
            Available: true,
            Samples: window.Count,
            ProcessorCount: last.ProcessorCount,
            CpuAvgPercent: cpuPercents.DefaultIfEmpty(0).Average(),
            CpuMaxPercent: cpuPercents.DefaultIfEmpty(0).Max(),
            CoresUsedMax: coresUsed.DefaultIfEmpty(0).Max(),
            WorkingSetMaxMb: Max(s => s.Process.WorkingSetBytes) / 1048576d,
            PrivateMaxMb: Max(s => s.Process.PrivateBytes) / 1048576d,
            GcHeapMaxMb: Max(s => s.Gc.HeapSizeBytes) / 1048576d,
            AllocatedMbPerSecond: (last.Gc.TotalAllocatedBytes - first.Gc.TotalAllocatedBytes) / 1048576d / seconds,
            Gen2Collections: last.Gc.Gen2Collections - first.Gc.Gen2Collections,
            GcPauseMs: last.Gc.TotalPauseMs - first.Gc.TotalPauseMs,
            ThreadsMax: (int)Max(s => s.Process.Threads),
            ThreadPoolThreadsMax: (int)Max(s => s.ThreadPool.Threads),
            ThreadPoolQueueMax: (long)Max(s => s.ThreadPool.PendingWorkItems),
            PoolUsedMax: Max(s => SumOf(s, "db.client.connection.count", "state=used")),
            PoolIdleAtEnd: SumOf(last, "db.client.connection.count", "state=idle"),
            PoolMax: LargestPool(last),
            PoolWaitingMax: Max(s => Peak(s, "db.client.connection.npgsql.pending_requests")),
            CommandsRunningMax: Max(s => Peak(s, "db.client.operation.npgsql.executing")),
            PoolTimeouts: Total(last, "db.client.connection.npgsql.timeouts") - Total(first, "db.client.connection.npgsql.timeouts"),
            DbCommandFailures: Total(last, "db.client.operation.failed") - Total(first, "db.client.operation.failed"),
            SignalRConnectionsMax: Max(s => Peak(s, "signalr.server.active_connections")),
            ActiveRequestsMax: Max(s => Peak(s, "http.server.active_requests")),
            ConnectionOpen: Histogram("app.db.connection.open_time"),
            ConnectionCreate: Histogram("db.client.connection.npgsql.create_time"),
            DbCommand: Histogram("db.client.operation.duration"),
            BidLockWait: Histogram("app.auction.lock_wait{operation=bid}"),
            JoinLockWait: Histogram("app.auction.lock_wait{operation=join}"),
            BidLockHeld: Histogram("app.bid.lock_held"),
            ServerBidRequest: Histogram($"http {bids}"),
            ServerJoinRequest: Histogram($"http {join}"),
            ServerBid5xx: Total(last, $"http.responses {bids} 5xx") - Total(first, $"http.responses {bids} 5xx"),
            ServerJoin5xx: Total(last, $"http.responses {join} 5xx") - Total(first, $"http.responses {join} 5xx"),
            DiagnosticsRequest: Summary.Of(window.Select(w => w.Ms)),
            SamplerFailures: Failures,
            SamplerLastFailure: LastFailure);
    }
}

sealed record ServerSummary(
    bool Available, int Samples, int ProcessorCount,
    double CpuAvgPercent, double CpuMaxPercent, double CoresUsedMax,
    double WorkingSetMaxMb, double PrivateMaxMb, double GcHeapMaxMb, double AllocatedMbPerSecond,
    int Gen2Collections, double GcPauseMs,
    int ThreadsMax, int ThreadPoolThreadsMax, long ThreadPoolQueueMax,
    double PoolUsedMax, double PoolIdleAtEnd, double PoolMax, double PoolWaitingMax, double CommandsRunningMax, double PoolTimeouts,
    double DbCommandFailures, double SignalRConnectionsMax, double ActiveRequestsMax,
    ServerWindow ConnectionOpen, ServerWindow ConnectionCreate, ServerWindow DbCommand,
    ServerWindow BidLockWait, ServerWindow JoinLockWait, ServerWindow BidLockHeld,
    ServerWindow ServerBidRequest, ServerWindow ServerJoinRequest, double ServerBid5xx, double ServerJoin5xx,
    Summary DiagnosticsRequest, int SamplerFailures, string? SamplerLastFailure)
{
    /// <summary>
    /// Bids on one auction take turns on its row lock, so it can accept at most this many a second:
    /// one second divided by the average time a bid keeps the lock.
    /// </summary>
    public double CapacityBidsPerSecond => BidLockHeld.Count == 0 || BidLockHeld.MeanMs <= 0 ? 0 : 1000 / BidLockHeld.MeanMs;

    public static ServerSummary Unavailable(int failures, string? lastFailure) => new(
        false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        ServerWindow.Empty, ServerWindow.Empty, ServerWindow.Empty, ServerWindow.Empty, ServerWindow.Empty,
        ServerWindow.Empty, ServerWindow.Empty, ServerWindow.Empty, 0, 0, Summary.Empty, failures, lastFailure);
}
