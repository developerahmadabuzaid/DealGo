using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionLoadTest;

/// <summary>
/// Watches one real auction from start to finish without taking part: one live connection like a
/// spectator's phone, a snapshot every few seconds, and — signed in as a super administrator — the
/// server's runtime diagnostics. Read-only; it places no bids and joins nothing. Afterwards it
/// checks the close the same way the load test does, from what the public API shows.
/// </summary>
static class AuctionMonitor
{
    private sealed record Event(string Name, long Ts, long Sequence, decimal Price, DateTime? ServerTime, Guid? Participant, string? Extra);

    public static async Task<int> RunAsync(Args a)
    {
        var baseUrl = new Uri(a.Get("base-url", "https://dealgosa.com/"));
        var outDir = a.Get("out", Path.Combine("artifacts", "monitor", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(outDir);
        using var api = new Api(baseUrl, TimeSpan.FromSeconds(30));

        var session = await SignInAsync(api, a);
        var auctionId = a.Get("auction") is { } id
            ? Guid.Parse(id)
            : await PickAuctionAsync(api, TimeSpan.FromMinutes(a.Int("wait-minutes", 120)));
        Log($"Watching auction {auctionId} on {baseUrl}");

        var events = new List<Event>();
        var displayed = 0m;
        var highest = 0m;
        long maxSequence = 0, outOfOrder = 0, regressions = 0;
        double staleMs = 0;
        long staleSince = 0;
        long finishedTs = 0, winnersTs = 0, startedTs = 0;
        var gate = new object();

        void ApplyPrice(decimal price, long ts)
        {
            var wasStale = displayed < highest;
            if (price > highest) highest = price;
            displayed = price;
            var stale = displayed < highest;
            if (!wasStale && stale) { regressions++; staleSince = ts; }
            else if (wasStale && !stale) staleMs += Clock.Ms(staleSince, ts);
        }

        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUrl, "hubs/auction"), o => o.AccessTokenProvider = async () => await session.TokenAsync())
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            .Build();
        hub.ServerTimeout = TimeSpan.FromSeconds(60);

        hub.On<BidBroadcast>("BidPlaced", b =>
        {
            var ts = Clock.Ts;
            lock (gate)
            {
                events.Add(new Event("BidPlaced", ts, b.SequenceNumber, b.NewPrice, b.ServerTimestamp, b.ParticipantId, $"{b.PreviousPrice}"));
                if (b.SequenceNumber < maxSequence) outOfOrder++; else maxSequence = b.SequenceNumber;
                ApplyPrice(b.NewPrice, ts);
            }
        });
        hub.On<PriceChanged>("AuctionPriceChanged", p => { lock (gate) { events.Add(new Event("AuctionPriceChanged", Clock.Ts, 0, p.CurrentPrice, null, null, null)); ApplyPrice(p.CurrentPrice, Clock.Ts); } });
        hub.On<LeaderboardMessage>("LeaderboardUpdated", m =>
        {
            var top = m.Leaderboard.Count == 0 ? 0 : m.Leaderboard.Max(e => e.HighestPrice);
            lock (gate) events.Add(new Event("LeaderboardUpdated", Clock.Ts, 0, top, null, null, $"{m.Leaderboard.Count}"));
        });
        foreach (var name in new[] { "AuctionStarted", "AuctionFinished", "WinnersDeclared", "FinalPhaseStarted",
                     "AuctionParticipantCountChanged", "AuctionPaused", "AuctionResumed", "AuctionCancelled" })
        {
            hub.On<JsonElement>(name, payload =>
            {
                var ts = Clock.Ts;
                lock (gate)
                {
                    events.Add(new Event(name, ts, 0, 0, null, null, payload.GetRawText()));
                    if (name == "AuctionStarted" && startedTs == 0) startedTs = ts;
                    if (name == "AuctionFinished" && finishedTs == 0) finishedTs = ts;
                    if (name == "WinnersDeclared" && winnersTs == 0) winnersTs = ts;
                }
            });
        }
        var disconnects = 0;
        hub.Reconnecting += _ => { disconnects++; Log("live connection lost, reconnecting"); return Task.CompletedTask; };
        hub.Reconnected += async _ => { Log("reconnected"); await hub.InvokeAsync<SnapshotDto>("JoinAuction", auctionId); };

        await hub.StartAsync();
        var first = await hub.InvokeAsync<SnapshotDto>("JoinAuction", auctionId);
        lock (gate) { displayed = highest = first.CurrentPrice; maxSequence = first.LastSequenceNumber; }
        var firstSequence = first.LastSequenceNumber;
        Log($"connected: status {first.Status}, price {first.CurrentPrice}, {first.LastSequenceNumber} bids so far");

        var (offset, rtt) = await api.EstimateClockOffsetAsync(auctionId);
        Log($"server clock is {offset.TotalMilliseconds:+0;-0} ms from this machine (best round trip {rtt:0} ms)");

        DiagnosticsSampler? sampler = null;
        var probe = await api.DiagnosticsAsync(await session.TokenAsync());
        if (probe.Ok)
        {
            sampler = new DiagnosticsSampler(api, await session.TokenAsync(), TimeSpan.FromSeconds(a.Int("interval", 2)));
            sampler.Start();
        }
        else
        {
            Log($"server diagnostics not available ({probe.Status} {probe.Code}); sign in as a super administrator for CPU, memory and database figures");
        }

        // Snapshots: what a phone resyncing right now would get, against what the live feed says.
        var snapshotLags = new List<double>();
        var snapshotPriceMismatches = 0;
        var snapshots = 0;
        var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); Log("stopping…"); };
        var giveUp = Clock.Now.AddMinutes(a.Int("max-minutes", 180));
        var monitorStartTs = Clock.Ts;
        string lastStatus = first.Status;
        long endSeenTs = 0;

        while (!stop.IsCancellationRequested && Clock.Now < giveUp)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(a.Int("snapshot-seconds", 5)), stop.Token); }
            catch (OperationCanceledException) { break; }

            var s = await api.SnapshotAsync(auctionId);
            if (!s.Ok || s.Value is null) continue;
            snapshots++;
            lock (gate)
            {
                if (s.Value.Status is "Live" or "FinalPhase")
                {
                    snapshotLags.Add(s.Value.LastSequenceNumber - maxSequence);
                    if (s.Value.CurrentPrice != displayed) snapshotPriceMismatches++;
                }
            }
            if (s.Value.Status != lastStatus)
            {
                Log($"status {lastStatus} → {s.Value.Status} (price {s.Value.CurrentPrice}, {s.Value.LastSequenceNumber} bids)");
                lastStatus = s.Value.Status;
            }
            if (s.Value.Status is "PrizeClaim" or "Completed" or "Cancelled")
            {
                if (endSeenTs == 0) endSeenTs = Clock.Ts;
                if (Clock.Ms(endSeenTs, Clock.Ts) > 10_000) break;
            }
        }

        if (sampler is not null) await sampler.StopAsync();
        await hub.DisposeAsync();
        var monitorEndTs = Clock.Ts;

        // ------------------------------------------------------------ after the close
        var detail = (await api.DetailAsync(auctionId)).Value;
        var finalSnapshot = (await api.SnapshotAsync(auctionId)).Value;
        var winners = (await api.WinnersAsync(auctionId)).Value ?? new();
        var board = (await api.LeaderboardAsync(auctionId, 100)).Value ?? new();

        List<Event> bids;
        List<(long Ts, decimal Top)> boards;
        lock (gate)
        {
            bids = events.Where(e => e.Name == "BidPlaced").GroupBy(e => e.Sequence).Select(g => g.OrderBy(e => e.Ts).First()).OrderBy(e => e.Sequence).ToList();
            boards = events.Where(e => e.Name == "LeaderboardUpdated").Select(e => (e.Ts, e.Price)).ToList();
        }

        var checks = new List<Check>();
        var expectedCount = (finalSnapshot?.LastSequenceNumber ?? 0) - firstSequence;
        var gaps = expectedCount - bids.Count;
        checks.Add(new Check("Every bid reached this screen", gaps == 0,
            $"{bids.Count} of {expectedCount} bids placed while watching arrived"));

        var increment = detail?.BidIncrement ?? 0;
        var ladder = 0;
        for (var i = 1; i < bids.Count; i++)
        {
            if (bids[i].Sequence != bids[i - 1].Sequence + 1) continue;
            var expected = Math.Min(bids[i - 1].Price + increment, detail?.TargetPrice ?? decimal.MaxValue);
            if (bids[i].Price != expected || bids[i].Extra != $"{bids[i - 1].Price}") ladder++;
        }
        checks.Add(new Check("Every bid raised the price by exactly one increment", ladder == 0, $"{ladder} steps off the ladder"));

        var twice = bids.Zip(bids.Skip(1)).Count(p => p.Second.Sequence == p.First.Sequence + 1 && p.First.Participant == p.Second.Participant);
        checks.Add(new Check("Nobody bid twice in a row", twice == 0, $"{twice} back-to-back bids seen"));

        var lastBidPrice = bids.Count > 0 ? bids[^1].Price : first.CurrentPrice;
        checks.Add(new Check("Final price is the last bid heard", detail is not null && detail.CurrentPrice == lastBidPrice,
            $"auction {detail?.CurrentPrice}, last bid heard {lastBidPrice}"));

        if (detail?.EndsAt is { } endsAt && detail.Status != "Cancelled")
        {
            var late = bids.Count(b => b.ServerTime >= endsAt);
            checks.Add(new Check("No bid accepted at or after the closing time", late == 0, $"{late} late; closing time {endsAt:HH:mm:ss.fff} UTC"));
        }

        var expectedTop = board.Where(e => e.BidCount > 0).OrderByDescending(e => e.HighestPrice).ThenBy(e => e.HighestPriceAt)
            .Take(4).Select(e => e.UserId).ToList();
        var declared = winners.OrderBy(w => w.Rank).Select(w => w.UserId).ToList();
        checks.Add(new Check("Winners are the top four by highest price, earliest first",
            declared.Count == 0 ? detail?.Status is not ("WinnersDeclared" or "PrizeClaim" or "Completed") : expectedTop.Take(declared.Count).SequenceEqual(declared),
            $"{declared.Count} winners declared"));

        var propagation = new FineHistogram();
        foreach (var b in bids.Where(b => b.ServerTime.HasValue))
            propagation.Record((Clock.Utc(b.Ts) - (b.ServerTime!.Value - offset)).TotalMilliseconds);

        var boardLag = new FineHistogram();
        var running = 0m;
        var j = 0;
        foreach (var b in bids.Where(b => b.ServerTime.HasValue))
        {
            while (j < boards.Count && Math.Max(running, boards[j].Top) < b.Price) { running = Math.Max(running, boards[j].Top); j++; }
            if (j >= boards.Count) break;
            boardLag.Record((Clock.Utc(boards[j].Ts) - (b.ServerTime!.Value - offset)).TotalMilliseconds);
        }

        var close = detail?.EndsAt is { } e2 ? e2 - offset : (DateTime?)null;
        string Since(long ts) => ts == 0 || close is null ? "not seen" : Summary.F((Clock.Utc(ts) - close.Value).TotalMilliseconds);

        var liveFrom = startedTs != 0 ? startedTs : monitorStartTs;
        var liveTo = finishedTs != 0 ? finishedTs : monitorEndTs;
        var server = sampler?.Summarise(liveFrom, liveTo);

        var md = new StringBuilder();
        md.AppendLine($"# Live auction monitor — {detail?.AuctionNumber ?? auctionId.ToString()}");
        md.AppendLine();
        md.AppendLine($"- **Site:** {baseUrl}; watched {Clock.Utc(monitorStartTs):yyyy-MM-dd HH:mm:ss} to {Clock.Utc(monitorEndTs):HH:mm:ss} UTC");
        md.AppendLine($"- **Auction:** {detail?.Title} — {detail?.ParticipantCount} participants, final status {detail?.Status}, final price {detail?.CurrentPrice}, {finalSnapshot?.LastSequenceNumber} bids");
        md.AppendLine($"- **Clock:** server {offset.TotalMilliseconds:+0;-0} ms from this machine, best round trip {rtt:0} ms (delays below are ± half that)");
        md.AppendLine();
        md.AppendLine("## Closing accuracy");
        md.AppendLine();
        foreach (var c in checks) md.AppendLine($"- {(c.Passed ? "✅" : "❌")} **{c.Name}** — {c.Detail}");
        md.AppendLine($"- From the closing time to this screen hearing \"auction finished\": {Since(finishedTs)}; hearing the winners: {Since(winnersTs)}.");
        md.AppendLine();
        md.AppendLine("## Live updates on this screen");
        md.AppendLine();
        md.AppendLine($"- Bid to this screen (p50 / p95 / p99): {propagation.Summary().Text}");
        md.AppendLine($"- Arrived out of order: {outOfOrder}; the app would have shown an older price {regressions} times, {staleMs / 1000:0.0} s in total.");
        md.AppendLine($"- Leaderboard lag (p50 / p95 / p99): {boardLag.Summary().Text}");
        md.AppendLine($"- Snapshots taken: {snapshots}; while live, the snapshot was ahead of the live feed by {Summary.Of(snapshotLags).Text.Replace(" ms", "")} bids (p50 / p95 / p99), price differed {snapshotPriceMismatches} times.");
        md.AppendLine($"- Connection drops: {disconnects}.");
        md.AppendLine();
        md.AppendLine("## Server while live");
        md.AppendLine();
        if (server is { Available: true })
        {
            md.AppendLine($"- CPU: average {server.CpuAvgPercent:0.0}%, peak {server.CpuMaxPercent:0.0}% of {server.ProcessorCount} cores.");
            md.AppendLine($"- Memory peak {server.WorkingSetMaxMb:0} MB; GC heap {server.GcHeapMaxMb:0} MB; {server.Gen2Collections} full collections.");
            md.AppendLine($"- Threads {server.ThreadsMax}, thread pool queue peak {server.ThreadPoolQueueMax}.");
            md.AppendLine($"- Database pool: up to {server.PoolUsedMax:0} of {server.PoolMax:0} in use; up to {server.PoolWaitingMax:0} waiting; {server.PoolTimeouts:0} timeouts; {server.DbCommandFailures:0} failed commands.");
            md.AppendLine($"- Getting a connection: {server.ConnectionOpen.Text}; creating one: {server.ConnectionCreate.Text} ({server.ConnectionCreate.Count} created).");
            md.AppendLine($"- Database commands: {server.DbCommand.Text}.");
            md.AppendLine($"- Lock wait per bid: {server.BidLockWait.Text}; lock held per bid: {server.BidLockHeld.Text} (one auction tops out near {server.CapacityBidsPerSecond:0} bids/s).");
            md.AppendLine($"- Bid request inside the server: {server.ServerBidRequest.Text} over {server.ServerBidRequest.Count} bids; {server.ServerBid5xx:0} server errors.");
            md.AppendLine($"- SignalR connections peak {server.SignalRConnectionsMax:0}.");
        }
        else
        {
            md.AppendLine("- Not available (needs a super administrator sign-in and the diagnostics endpoint deployed).");
        }

        await File.WriteAllTextAsync(Path.Combine(outDir, "monitor-report.md"), md.ToString());
        await File.WriteAllLinesAsync(Path.Combine(outDir, "events.jsonl"), events.Select(e => JsonSerializer.Serialize(new
        {
            e.Name, At = Clock.Utc(e.Ts), e.Sequence, e.Price, e.ServerTime, e.Participant, e.Extra
        }, Json.Options)));
        if (sampler is not null)
            await File.WriteAllTextAsync(Path.Combine(outDir, "diagnostics.json"), JsonSerializer.Serialize(sampler.Samples.Select(s => new { At = Clock.Utc(s.Ts), s.Snapshot, s.RequestMs }), Json.Pretty));

        Log($"report: {Path.GetFullPath(Path.Combine(outDir, "monitor-report.md"))}");
        return checks.All(c => c.Passed) ? 0 : 1;
    }

    /// <summary>The live auction, else the next one to start; waits for one to be published.</summary>
    private static async Task<Guid> PickAuctionAsync(Api api, TimeSpan wait)
    {
        var giveUp = Clock.Now + wait;
        var told = false;
        while (true)
        {
            foreach (var group in new[] { "live", "upcoming" })
            {
                var r = await api.SendAsync<AuctionListDto>(HttpMethod.Get, $"api/v1/auctions?group={group}&pageSize=20", null);
                var item = r.Value?.Items.OrderBy(i => i.ScheduledStartAt ?? DateTime.MaxValue).FirstOrDefault();
                if (item is not null)
                {
                    Log($"picked {item.AuctionNumber} \"{item.Title}\" ({item.Status}); pass --auction to choose another");
                    return item.Id;
                }
            }

            if (Clock.Now > giveUp) throw new ArgumentException("No live or upcoming auction found; pass --auction <id>.");
            if (!told) { Log("no live or upcoming auction yet; waiting for one"); told = true; }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>A signed-in session that renews its access token before it runs out.</summary>
    private sealed class Session
    {
        private readonly Api _api;
        private string _access;
        private string? _refresh;
        private DateTime _expires;

        public Session(Api api, string access, string? refresh, DateTime expires)
        {
            _api = api;
            _access = access;
            _refresh = refresh;
            _expires = expires;
        }

        public async Task<string> TokenAsync()
        {
            if (_refresh is not null && DateTime.UtcNow > _expires.AddMinutes(-2))
            {
                var r = await _api.SendAsync<AuthResult>(HttpMethod.Post, "api/v1/auth/refresh", null, new { refreshToken = _refresh });
                if (r.Ok && r.Value is not null)
                {
                    _access = r.Value.AccessToken;
                    _refresh = r.Value.RefreshToken;
                    _expires = r.Value.AccessTokenExpiresAt;
                }
            }
            return _access;
        }
    }

    private static async Task<Session> SignInAsync(Api api, Args a)
    {
        if (a.Get("phone") is { } phone)
        {
            var request = await api.SendAsync<JsonElement>(HttpMethod.Post, "api/v1/auth/otp/request", null,
                new { destination = phone, language = "ar", staffOnly = true });
            if (!request.Ok) throw new ArgumentException($"Could not request a code: {request.Status} {request.Code} {request.Message}");
            Console.Write($"Code sent to {phone}. Enter it: ");
            var code = Console.ReadLine()?.Trim() ?? string.Empty;
            var verified = await api.SendAsync<AuthResult>(HttpMethod.Post, "api/v1/auth/otp/verify", null,
                new { destination = phone, code, staffOnly = true });
            var auth = verified.Require("Signing in");
            return new Session(api, auth.AccessToken, auth.RefreshToken, auth.AccessTokenExpiresAt);
        }

        if (a.Get("refresh-token") is { } refresh)
        {
            var r = await api.SendAsync<AuthResult>(HttpMethod.Post, "api/v1/auth/refresh", null, new { refreshToken = refresh });
            var auth = r.Require("Renewing the session");
            return new Session(api, auth.AccessToken, auth.RefreshToken, auth.AccessTokenExpiresAt);
        }

        if (a.Get("token") is { } token) return new Session(api, token, null, DateTime.MaxValue);

        throw new ArgumentException("Sign in with --phone 05XXXXXXXX (a code is sent), or pass --refresh-token or --token.");
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
