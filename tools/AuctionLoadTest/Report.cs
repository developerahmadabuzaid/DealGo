using System.Globalization;
using System.Text;

namespace AuctionLoadTest;

static class Report
{
    public static string Markdown(RunSettings s, IReadOnlyList<ScenarioResult> results, string environment)
    {
        var md = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        md.AppendLine("# Bidding load test");
        md.AppendLine();
        md.AppendLine($"- **When:** {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
        md.AppendLine($"- **Target:** {s.BaseUrl} — {environment}");
        md.AppendLine($"- **Auctions:** {s.Mode}, close by {s.Close}, {s.DurationSeconds} s, increment 1 SAR" +
                      (s.Mode == "paid" ? $", entry fee {s.EntryFee} SAR" : ""));
        md.AppendLine($"- **Bidding:** each participant bids at random moments, about {s.Rate.ToString(inv)} bids/s, " +
                      $"rising to {s.BurstRate.ToString(inv)} bids/s in the last {s.BurstSeconds} s; at most one bid in flight, " +
                      "never right after their own bid (the app's button rules).");
        md.AppendLine();

        md.AppendLine("## Summary");
        md.AppendLine();
        md.AppendLine("| Participants | Bids tried/s | Accepted | Bid latency p50 / p95 / p99 | Rejected (rules) | Failed | Price reaches clients p50 / p95 / p99 | Missed | Shown out of order | Leaderboard lag p50 / p95 / p99 | Lock wait p95 | Lock held avg → ceiling | DB connections in use / waiting (peak) | Server CPU avg / max | Memory max | Closing checks |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            var srv = r.Server;
            md.AppendLine(
                $"| {r.Participants} | {r.AttemptsPerSecond:0.0} | {r.Accepted} ({r.AcceptedPerSecond:0.0}/s) | {Trio(r.BidLatency)} | " +
                $"{r.Rejected} | {Bad(r.Failed)} | {Trio(r.Propagation)} | {Bad(r.MissingDeliveries)} | {r.PriceRegressions} | " +
                $"{Trio(r.LeaderboardLag)} | {Srv(srv, Summary.F(srv.BidLockWait.P95))} | " +
                $"{Srv(srv, srv.BidLockHeld.Count == 0 ? "—" : $"{srv.BidLockHeld.MeanMs:0.0} ms → {srv.CapacityBidsPerSecond:0}/s")} | " +
                $"{Srv(srv, $"{srv.PoolUsedMax:0} of {srv.PoolMax:0} / {srv.PoolWaitingMax:0}")} | " +
                $"{Srv(srv, $"{srv.CpuAvgPercent:0}% / {srv.CpuMaxPercent:0}% ({srv.CoresUsedMax:0.0} cores)")} | " +
                $"{Srv(srv, $"{srv.WorkingSetMaxMb:0} MB")} | {(r.AllChecksPassed ? "✅ all passed" : $"❌ {r.Checks.Count(c => !c.Passed)} failed")} |");
        }
        md.AppendLine();
        md.AppendLine("Latencies are milliseconds unless marked. *Bid latency* is from sending the bid to having the answer. " +
                      "*Price reaches clients* is from the server accepting a bid to each connected client receiving it. " +
                      "*Leaderboard lag* is from a bid being accepted to a client receiving a top-ten list that includes it. " +
                      "*Shown out of order* counts times a client would briefly show an older, lower price because two updates arrived swapped. " +
                      "*Lock held → ceiling*: bids on one auction take turns on its row lock, so one second divided by the average hold is the most bids a second one auction can accept.");
        md.AppendLine();

        foreach (var r in results)
        {
            var srv = r.Server;
            md.AppendLine($"## {r.Participants} participants — {r.AuctionNumber}");
            md.AppendLine();
            md.AppendLine("### Closing accuracy");
            md.AppendLine();
            foreach (var c in r.Checks) md.AppendLine($"- {(c.Passed ? "✅" : "❌")} **{c.Name}** — {c.Detail}");
            md.AppendLine($"- Ended by: {r.EndedBy}. Final price {r.FinalPrice} after {r.FinalSequence} bids, status {r.FinalStatus}.");
            md.AppendLine($"- From the close to clients hearing \"auction finished\": {r.DeadlineToFinishedEvent.Text}; " +
                          $"to hearing the winners: {r.DeadlineToWinnersEvent.Text}; to the auction's final state: " +
                          (double.IsNaN(r.DeadlineToFinalStatusMs) ? "not reached" : Summary.F(r.DeadlineToFinalStatusMs)) + ".");
            md.AppendLine();

            md.AppendLine("### Bids");
            md.AppendLine();
            md.AppendLine($"- {r.Attempts} tried in {r.BiddingSeconds:0.0} s ({r.AttemptsPerSecond:0.0}/s): " +
                          $"{r.Accepted} accepted, {r.Rejected} rejected by a rule, {r.Failed} failed. " +
                          $"{r.SkippedAsLastBidder} taps skipped because that person had the last bid.");
            md.AppendLine($"- Latency, all attempts (p50 / p95 / p99): {r.BidLatency.Text}");
            md.AppendLine($"- Latency, accepted bids: {r.AcceptedBidLatency.Text}");
            md.AppendLine($"- Latency, steady part: {r.SteadyBidLatency.Text}; closing burst: {r.BurstBidLatency.Text}");
            md.AppendLine("- Answers:");
            foreach (var o in r.Outcomes)
                md.AppendLine($"  - {o.Outcome}{(o.Code.Length > 0 ? $" `{o.Code}`" : "")}: {o.Count}");
            md.AppendLine();

            md.AppendLine("### Live price on every screen");
            md.AppendLine();
            md.AppendLine($"- Time for an accepted bid to reach each client (p50 / p95 / p99): {r.Propagation.Text}" +
                          (r.NegativePropagation > 0 ? $" ({r.NegativePropagation} readings below zero from clock estimate, counted as 0)" : ""));
            md.AppendLine($"- Deliveries missing: {r.MissingDeliveries} of {r.ExpectedDeliveries} ({r.ClientsMissingBids} clients affected); " +
                          $"disconnects {r.Disconnects}, reconnects {r.Reconnects}.");
            md.AppendLine($"- Updates arriving out of order: {r.OutOfOrderDeliveries}. The app would have shown an older price " +
                          $"{r.PriceRegressions} times on {r.ClientsWithRegressions} screens, {r.StaleSecondsTotal:0.0} s in total " +
                          $"(typical episode {r.StaleEpisode.Text}).");
            md.AppendLine($"- Screens showing the wrong price once bidding stopped, before any refresh: {r.ClientsShowingWrongPriceAtEnd}.");
            md.AppendLine($"- Leaderboard: {r.LeaderboardUpdatesPerSecond:0.0} updates/s per client; lag {r.LeaderboardLag.Text}; " +
                          $"clients that never got the final list: {r.ClientsWithoutFinalLeaderboard}.");
            md.AppendLine();

            md.AppendLine("### Joining");
            md.AppendLine();
            md.AppendLine($"- Live connections: {r.HubConnect.Text}, {r.HubConnectFailures} failed.");
            md.AppendLine($"- Joins, all at once: {r.Join.Text}; {r.JoinFailures} failed; participant count afterwards {r.ParticipantCountAfterJoin}.");
            if (r.JoinServer.Available)
                md.AppendLine($"- Server side while joining: lock wait {r.JoinServer.JoinLockWait.Text}, join request {r.JoinServer.ServerJoinRequest.Text}, " +
                              $"DB pool in use max {r.JoinServer.PoolUsedMax:0}, waiting max {r.JoinServer.PoolWaitingMax:0}.");
            md.AppendLine();

            md.AppendLine("### Server while bidding");
            md.AppendLine();
            if (!srv.Available)
            {
                md.AppendLine($"- Diagnostics unavailable ({srv.SamplerFailures} failed readings, last: {srv.SamplerLastFailure}).");
            }
            else
            {
                md.AppendLine($"- CPU: average {srv.CpuAvgPercent:0.0}%, peak {srv.CpuMaxPercent:0.0}% of {srv.ProcessorCount} cores (peak {srv.CoresUsedMax:0.00} cores busy).");
                md.AppendLine($"- Memory: working set peak {srv.WorkingSetMaxMb:0} MB, private {srv.PrivateMaxMb:0} MB, GC heap {srv.GcHeapMaxMb:0} MB; " +
                              $"allocating {srv.AllocatedMbPerSecond:0} MB/s; {srv.Gen2Collections} full collections, {srv.GcPauseMs:0} ms paused.");
                md.AppendLine($"- Threads: {srv.ThreadsMax} (thread pool {srv.ThreadPoolThreadsMax}), work queue peak {srv.ThreadPoolQueueMax}.");
                md.AppendLine($"- Database pool: {srv.PoolUsedMax:0} connections in use at the busiest reading (pool size {srv.PoolMax:0}), {srv.PoolIdleAtEnd:0} idle at the end; " +
                              $"at the peak {srv.CommandsRunningMax:0} commands running at once and {srv.PoolWaitingMax:0} requests waiting for a connection; " +
                              $"{srv.PoolTimeouts:0} timeouts; {srv.DbCommandFailures:0} failed commands.");
                md.AppendLine($"- Getting a connection (p50 / p95 / p99): {srv.ConnectionOpen.Text}; creating a new one: {srv.ConnectionCreate.Text} ({srv.ConnectionCreate.Count} created).");
                md.AppendLine($"- Database commands: {srv.DbCommand.Text} over {srv.DbCommand.Count} commands.");
                md.AppendLine($"- Waiting for the auction's lock per bid: {srv.BidLockWait.Text}; then holding it until COMMIT: {srv.BidLockHeld.Text} " +
                              $"(average {srv.BidLockHeld.MeanMs:0.0} ms, so at most about {srv.CapacityBidsPerSecond:0} bids a second on one auction).");
                md.AppendLine($"- Bid request inside the server: {srv.ServerBidRequest.Text}; {srv.ServerBid5xx:0} server errors.");
                md.AppendLine($"- SignalR connections at peak: {srv.SignalRConnectionsMax:0}; requests in progress at peak: {srv.ActiveRequestsMax:0}.");
                md.AppendLine($"- Diagnostics reads took {srv.DiagnosticsRequest.Text} ({srv.Samples} readings).");
            }
            md.AppendLine($"- Load generator CPU: {r.GeneratorCpuPercent:0.0}% of this machine's {Environment.ProcessorCount} cores.");
            md.AppendLine();
        }

        return md.ToString();
    }

    private static string Trio(Summary s) => s.Count == 0 ? "—" : $"{s.P50:0} / {s.P95:0} / {s.P99:0}";
    private static string Bad(long n) => n == 0 ? "0" : $"**{n}**";
    private static string Srv(ServerSummary s, string text) => s.Available ? text : "n/a";
}
