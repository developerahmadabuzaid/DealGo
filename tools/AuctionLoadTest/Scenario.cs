using System.Collections.Concurrent;
using System.Diagnostics;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;
using Microsoft.EntityFrameworkCore;

namespace AuctionLoadTest;

sealed record RunSettings(
    Uri BaseUrl, int DurationSeconds, double Rate, double BurstRate, int BurstSeconds, double MinGapMs,
    string Mode, string Close, int TargetBids, decimal EntryFee, int ConnectConcurrency, int Seed,
    // Bidders in a small second auction running at the same time, to see whether a busy auction
    // slows the others down. 0 = none.
    int Neighbors = 0);

sealed record Check(string Name, bool Passed, string Detail);

sealed record OutcomeCount(string Outcome, string Code, int Count);

sealed record ScenarioResult(
    int Participants, string Mode, string Close, Guid AuctionId, string AuctionNumber,
    DateTime StartedAtUtc, double ClockOffsetMs, double ClockRttMs,
    // connecting and joining
    Summary HubConnect, int HubConnectFailures, Summary Join, int JoinFailures, int ParticipantCountAfterJoin,
    // bidding
    double BiddingSeconds, int Attempts, int Accepted, int Rejected, int Failed, int SkippedAsLastBidder,
    double AttemptsPerSecond, double AcceptedPerSecond,
    Summary BidLatency, Summary AcceptedBidLatency, Summary SteadyBidLatency, Summary BurstBidLatency,
    List<OutcomeCount> Outcomes,
    // price synchronisation
    long ExpectedDeliveries, long MissingDeliveries, int ClientsMissingBids, long OutOfOrderDeliveries,
    long PriceRegressions, int ClientsWithRegressions, double StaleSecondsTotal, Summary StaleEpisode,
    int ClientsShowingWrongPriceAtEnd, Summary Propagation, long NegativePropagation,
    int Disconnects, int Reconnects,
    // leaderboard
    Summary LeaderboardLag, double LeaderboardUpdatesPerSecond, int ClientsWithoutFinalLeaderboard,
    // closing
    long FinalSequence, decimal FinalPrice, string FinalStatus, string EndedBy,
    Summary DeadlineToFinishedEvent, Summary DeadlineToWinnersEvent, double DeadlineToFinalStatusMs,
    List<Check> Checks,
    // server
    ServerSummary Server, ServerSummary JoinServer, double GeneratorCpuPercent,
    // the small auction running beside it
    int NeighborParticipants = 0, int NeighborAttempts = 0, int NeighborAccepted = 0, int NeighborFailed = 0,
    Summary? NeighborBidLatency = null, int Busy = 0, int JoinsBusy = 0)
{
    public bool AllChecksPassed => Checks.All(c => c.Passed);
}

sealed class ScenarioRunner
{
    private readonly RunSettings _s;
    private readonly Api _api;
    private readonly LocalSetup _local;
    private readonly TestUser _admin;
    private readonly List<TestUser> _pool;

    public ScenarioRunner(RunSettings settings, Api api, LocalSetup local, TestUser admin, List<TestUser> pool)
    {
        _s = settings;
        _api = api;
        _local = local;
        _admin = admin;
        _pool = pool;
    }

    public string AttemptsCsv { get; private set; } = string.Empty;

    public async Task<ScenarioResult> RunAsync(int n)
    {
        var tag = $"{n}-{DateTime.UtcNow:HHmmss}";
        var paid = _s.Mode == "paid";
        var users = _pool.Take(n).ToList();
        var userIds = users.Select(u => u.Id).ToList();
        Log($"── {n} participants ({_s.Mode}, closes by {_s.Close}) ──");

        await _local.ResetWalletsAsync(userIds, paid ? _s.EntryFee : 0m);

        // The auction, built and published through the admin API like the dashboard does.
        var created = (await _api.SendAsync<DetailDto>(HttpMethod.Post, "api/v1/admin/auctions", _admin.Token,
            AuctionRequest(n, tag))).Require("Creating the auction");
        var auctionId = created.Id;
        (await _api.SendAsync<DetailDto>(HttpMethod.Post, $"api/v1/admin/auctions/{auctionId}/publish", _admin.Token))
            .Require("Publishing the auction");
        Log($"auction {created.AuctionNumber} published");

        var sampler = new DiagnosticsSampler(_api, _admin.Token, TimeSpan.FromSeconds(1));
        sampler.Start();
        var generatorCpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var scenarioStartTs = Clock.Ts;

        // Everyone opens the auction page first (the live connection), then joins — as the app does.
        var holder = new BiddingContextHolder();
        var participants = users.Select((u, i) => new Participant { Index = i, User = u }).ToList();
        var connectTimes = new ConcurrentBag<double>();
        var connectFailures = new ConcurrentBag<string>();
        using (var gate = new SemaphoreSlim(_s.ConnectConcurrency))
        {
            await Task.WhenAll(participants.Select(async p =>
            {
                await gate.WaitAsync();
                try { connectTimes.Add(await p.ConnectAsync(_api.BaseUrl, auctionId, holder)); }
                catch (Exception e) { connectFailures.Add(e.GetBaseException().Message); }
                finally { gate.Release(); }
            }));
        }
        Log($"{connectTimes.Count} live connections ({connectFailures.Count} failed)");

        var (offset, rtt) = await _api.EstimateClockOffsetAsync(auctionId);

        // All at once: the auction row is locked per join, so this is its own contention test.
        var joinStartTs = await sampler.MarkAsync();
        var joins = await Task.WhenAll(participants.Select(async p =>
        {
            var r = await _api.JoinAsync(p.User.Token, auctionId, $"join-{tag}-{p.Index}");
            if (r.Ok && r.Value is not null) p.ParticipantId = r.Value.ParticipantId;
            return r;
        }));
        var joinEndTs = await sampler.MarkAsync();
        var joinFailures = joins.Count(j => !j.Ok);
        Log($"{joins.Length - joinFailures} joined ({joinFailures} failed), slowest {joins.Max(j => j.Ms):0} ms");

        // The start delay is long on purpose, so the scheduler never starts it under the test.
        var started = (await _api.SendAsync<DetailDto>(HttpMethod.Post, $"api/v1/admin/auctions/{auctionId}/start",
            _admin.Token, new { reason = "Load test" })).Require("Starting the auction");
        var endsAtServer = started.EndsAt ?? throw new InvalidOperationException("The started auction has no end time.");
        var endsAtLocal = endsAtServer - offset;
        var participantCount = started.ParticipantCount;
        Log($"live, ends {endsAtServer:HH:mm:ss} UTC");

        // A small auction beside this one, bidding at the steady rate the whole time.
        var neighbor = _s.Neighbors > 0 ? await StartNeighborAsync(n, tag, offset) : null;

        var byTarget = _s.Close == "target";
        var context = new BiddingContext
        {
            Api = _api,
            AuctionId = auctionId,
            RunTag = tag,
            Rate = byTarget ? _s.BurstRate : _s.Rate,
            BurstRate = _s.BurstRate,
            BurstFrom = byTarget ? DateTime.MinValue : endsAtLocal - TimeSpan.FromSeconds(_s.BurstSeconds),
            StopAt = endsAtLocal + TimeSpan.FromSeconds(20),
            MinGapMs = _s.MinGapMs,
            Seed = _s.Seed + n
        };
        holder.Context = context;

        var biddingStartTs = await sampler.MarkAsync();
        var neighborBidding = neighbor is null
            ? Task.CompletedTask
            : Task.WhenAll(neighbor.Participants.Select(p => p.BidLoopAsync(neighbor.Context)));
        await Task.WhenAll(participants.Select(p => p.BidLoopAsync(context)));
        var biddingEndTs = context.EndedTs != 0 ? context.EndedTs : Clock.Ts;
        neighbor?.Context.End("the busy auction is over");
        await neighborBidding;
        await sampler.MarkAsync();
        Log($"bidding over ({context.EndedBy ?? "time limit"})");

        var (finalDetail, statusTimes) = await WaitForEndAsync(auctionId, paid ? "PrizeClaim" : "Completed");
        await Task.Delay(TimeSpan.FromSeconds(3));   // last leaderboard flush and stragglers
        await sampler.StopAsync();
        var scenarioEndTs = Clock.Ts;
        var generatorCpu = (Process.GetCurrentProcess().TotalProcessorTime - generatorCpuStart).TotalMilliseconds
                           / Clock.Ms(scenarioStartTs, scenarioEndTs) / Environment.ProcessorCount * 100;
        await Task.WhenAll(participants.Select(p => p.DisconnectAsync()));

        if (neighbor is not null)
        {
            // Called off now that it has served its purpose, so it does not close during the next run.
            await _api.SendAsync<DetailDto>(HttpMethod.Post, $"api/v1/admin/auctions/{neighbor.AuctionId}/cancel",
                _admin.Token, new { reason = "Load test over" });
            await Task.WhenAll(neighbor.Participants.Select(p => p.DisconnectAsync()));
        }
        var neighborAttempts = neighbor?.Participants.SelectMany(p => p.Attempts).ToList() ?? new List<BidAttempt>();

        // ------------------------------------------------------------ what happened
        await using var db = _local.Db();

        // Only the columns every version of the schema has, so this tool can check older builds too.
        var auction = await db.Auctions.AsNoTracking().Where(a => a.Id == auctionId).Select(a => new
        {
            a.CurrentPrice, a.LastSequenceNumber, a.StartingPrice, a.BidIncrement, a.TargetPrice, a.ActualStartedAt,
            a.DurationSeconds, a.TargetReached, a.Status, a.WinnersDeclared, a.RefundsProcessed, a.OriginalPrice,
            a.TargetPercentage
        }).SingleAsync();
        var dbBids = await db.Bids.AsNoTracking().Where(b => b.AuctionId == auctionId)
            .OrderBy(b => b.SequenceNumber)
            .Select(b => new { b.SequenceNumber, b.ParticipantId, b.PreviousPrice, b.NewPrice, b.ReachedTarget, b.ServerTimestamp, b.IdempotencyKey })
            .ToListAsync();
        var dbParticipants = await db.AuctionParticipants.AsNoTracking().Where(p => p.AuctionId == auctionId).ToListAsync();
        var winners = await db.Winners.AsNoTracking().Where(w => w.AuctionId == auctionId).OrderBy(w => w.Rank).ToListAsync();
        var claims = await db.PrizeClaims.AsNoTracking().Where(c => c.AuctionId == auctionId).ToListAsync();
        var prizes = await db.AuctionPrizes.AsNoTracking().Where(p => p.AuctionId == auctionId).ToListAsync();
        var refunds = await db.AuctionEntryFeeRefunds.AsNoTracking().Where(r => r.AuctionId == auctionId).ToListAsync();
        var wallets = await db.Wallets.AsNoTracking().Where(w => userIds.Contains(w.UserId)).ToListAsync();

        var attempts = participants.SelectMany(p => p.Attempts).ToList();
        var accepted = attempts.Where(a => a.Accepted).ToList();
        var biddingSeconds = Math.Max(0.001, Clock.Ms(biddingStartTs, biddingEndTs) / 1000);

        var lastSequence = dbBids.Count == 0 ? 0 : dbBids[^1].SequenceNumber;
        var priceBySeq = new decimal[lastSequence + 1];
        var localTimeBySeq = new DateTime[lastSequence + 1];
        foreach (var b in dbBids)
        {
            if (b.SequenceNumber > lastSequence || b.SequenceNumber < 1) continue;
            priceBySeq[b.SequenceNumber] = b.NewPrice;
            localTimeBySeq[b.SequenceNumber] = b.ServerTimestamp - offset;
        }
        var finalPrice = auction.CurrentPrice;

        // ------------------------------------------------------------ price synchronisation
        var propagation = new FineHistogram();
        var leaderboardLag = new FineHistogram();
        long missing = 0, outOfOrder = 0, regressions = 0;
        int clientsMissing = 0, clientsRegressed = 0, wrongAtEnd = 0, withoutFinalBoard = 0;
        double staleMs = 0;
        var staleEpisodes = new List<double>();
        long boardEvents = 0;

        foreach (var p in participants.Where(p => p.Hub is not null))
        {
            var seen = new HashSet<long>();
            for (var i = 0; i < p.BidSequences.Count; i++)
            {
                var seq = p.BidSequences[i];
                if (seq < 1 || seq > lastSequence || !seen.Add(seq)) continue;
                propagation.Record((Clock.Utc(p.BidReceivedTs[i]) - localTimeBySeq[seq]).TotalMilliseconds);
            }
            var lost = lastSequence - seen.Count;
            missing += lost;
            if (lost > 0) clientsMissing++;
            outOfOrder += p.OutOfOrder;
            regressions += p.Regressions;
            if (p.Regressions > 0)
            {
                clientsRegressed++;
                staleEpisodes.Add(p.StaleMs / p.Regressions);
            }
            staleMs += p.StaleMs;
            if (p.StillStaleAtEnd(out var stillMs)) staleMs += stillMs;
            if (lastSequence > 0 && p.Displayed != finalPrice) wrongAtEnd++;

            // Leaderboard: for each bid, the first list this client got whose top price includes it.
            var boards = p.Leaderboards;
            boardEvents += boards.Count;
            var runningTop = 0m;
            var j = 0;
            for (long seq = 1; seq <= lastSequence; seq++)
            {
                while (j < boards.Count && Math.Max(runningTop, boards[j].Top) < priceBySeq[seq])
                {
                    runningTop = Math.Max(runningTop, boards[j].Top);
                    j++;
                }
                if (j >= boards.Count) break;
                leaderboardLag.Record((Clock.Utc(boards[j].Ts) - localTimeBySeq[seq]).TotalMilliseconds);
            }
            if (lastSequence > 0 && (boards.Count == 0 || boards.Max(b => b.Top) < finalPrice)) withoutFinalBoard++;
        }

        // ------------------------------------------------------------ closing accuracy
        var checks = new List<Check>();
        var acceptedSeqs = accepted.Select(a => a.Sequence).ToHashSet();
        var dbSeqs = dbBids.Select(b => b.SequenceNumber).ToHashSet();
        var attemptsByKey = attempts.ToDictionary(a => a.Key);
        var phantom = acceptedSeqs.Count(s => !dbSeqs.Contains(s));
        var unacknowledged = dbBids.Count(b => attemptsByKey.TryGetValue(b.IdempotencyKey, out var a) && !a.Accepted);
        var foreign = dbBids.Count(b => !attemptsByKey.ContainsKey(b.IdempotencyKey));
        checks.Add(new Check("Every bid the server confirmed is stored, and only those",
            phantom == 0 && unacknowledged == 0 && foreign == 0 && accepted.Count == dbBids.Count,
            $"{accepted.Count} confirmed, {dbBids.Count} stored; {phantom} confirmed but missing, " +
            $"{unacknowledged} stored though the bidder was told it failed, {foreign} from nowhere"));

        var gaps = dbBids.Select((b, i) => b.SequenceNumber != i + 1).Count(x => x);
        checks.Add(new Check("Bid sequence runs 1, 2, 3… with no gaps or repeats",
            gaps == 0 && auction.LastSequenceNumber == lastSequence,
            $"last {lastSequence}, auction says {auction.LastSequenceNumber}, {gaps} out of place"));

        var ladderBreaks = 0;
        var expectedPrevious = auction.StartingPrice;
        foreach (var b in dbBids)
        {
            var expectedNew = Math.Min(expectedPrevious + auction.BidIncrement, auction.TargetPrice);
            if (b.PreviousPrice != expectedPrevious || b.NewPrice != expectedNew) ladderBreaks++;
            expectedPrevious = b.NewPrice;
        }
        checks.Add(new Check("Every bid raised the price by exactly one increment",
            ladderBreaks == 0, $"{ladderBreaks} of {dbBids.Count} bids off the ladder"));

        var twiceInARow = dbBids.Zip(dbBids.Skip(1)).Count(pair => pair.First.ParticipantId == pair.Second.ParticipantId);
        checks.Add(new Check("Nobody bid twice in a row", twiceInARow == 0, $"{twiceInARow} back-to-back bids"));

        var lastPrice = dbBids.Count == 0 ? auction.StartingPrice : dbBids[^1].NewPrice;
        checks.Add(new Check("Final price is the last accepted bid", auction.CurrentPrice == lastPrice,
            $"auction {auction.CurrentPrice}, last bid {lastPrice}"));

        var deadline = auction.ActualStartedAt!.Value.AddSeconds(auction.DurationSeconds);
        if (byTarget)
        {
            var reached = dbBids.Where(b => b.ReachedTarget).ToList();
            checks.Add(new Check("Exactly one bid reached the target, and it was the last",
                reached.Count == 1 && reached[0].SequenceNumber == lastSequence && auction.TargetReached,
                $"{reached.Count} reaching bids, target reached = {auction.TargetReached}"));
        }
        else
        {
            var late = dbBids.Count(b => b.ServerTimestamp >= deadline);
            checks.Add(new Check("No bid accepted at or after the closing time", late == 0,
                $"{late} late bids; closing time {deadline:HH:mm:ss.fff} UTC, last bid " +
                (dbBids.Count == 0 ? "none" : $"{(deadline - dbBids[^1].ServerTimestamp).TotalMilliseconds:0} ms before it")));
        }

        // Ranking from the bids themselves: highest price, then who reached it first.
        var expectedRanking = dbBids.GroupBy(b => b.ParticipantId)
            .Select(g => g.OrderByDescending(b => b.NewPrice).First())
            .OrderByDescending(b => b.NewPrice).ThenBy(b => b.ServerTimestamp)
            .Select(b => b.ParticipantId).ToList();
        var bidsByParticipant = dbBids.GroupBy(b => b.ParticipantId)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Top: g.Max(b => b.NewPrice)));
        var recordMismatches = dbParticipants.Count(p =>
        {
            var mine = bidsByParticipant.TryGetValue(p.Id, out var m) ? m : (Count: 0, Top: 0m);
            return p.BidCount != mine.Count || p.HighestPrice != mine.Top;
        });
        checks.Add(new Check("Each participant's highest price and bid count match their bids",
            recordMismatches == 0, $"{recordMismatches} of {dbParticipants.Count} participants differ"));

        var prizeRanks = prizes.Select(p => p.Rank).ToHashSet();
        var expectedWinners = expectedRanking.Take(4).Select((id, i) => (Rank: i + 1, Id: id))
            .Where(w => prizeRanks.Contains(w.Rank)).ToList();
        var actualWinners = winners.Select(w => (w.Rank, Id: w.ParticipantId)).ToList();
        checks.Add(new Check("Winners are the top four by highest price, earliest first",
            expectedWinners.SequenceEqual(actualWinners),
            $"expected {string.Join(", ", expectedWinners.Select(w => $"#{w.Rank}"))}, " +
            $"declared {winners.Count}{(expectedWinners.SequenceEqual(actualWinners) ? "" : " — different people or order")}"));

        if (paid)
        {
            var wrongClaims = 0;
            foreach (var w in winners)
            {
                var claim = claims.SingleOrDefault(c => c.WinnerId == w.Id);
                var prize = prizes.Single(p => p.Id == w.PrizeId);
                var participant = dbParticipants.Single(p => p.Id == w.ParticipantId);
                var price = AuctionRules.PrizePaymentAmount(w.Rank, prize.OriginalPrice, auction.CurrentPrice,
                    auction.OriginalPrice, auction.TargetPercentage);
                var fee = Math.Min(Math.Max(participant.EntryFeePaid, 0m), price);
                if (claim is null || claim.PaymentAmount != price - fee || claim.EntryFeeDeducted != fee) wrongClaims++;
            }
            checks.Add(new Check("Each winner owes the right amount for their prize",
                wrongClaims == 0 && claims.Count == winners.Count, $"{claims.Count} claims, {wrongClaims} wrong"));

            var winnerIds = winners.Select(w => w.ParticipantId).ToHashSet();
            var badRefunds = dbParticipants.Count(p =>
            {
                var mine = refunds.Where(r => r.ParticipantId == p.Id).ToList();
                return winnerIds.Contains(p.Id)
                    ? mine.Count != 0
                    : mine.Count != 1 || mine[0].RefundAmount != p.EntryFeePaid || mine[0].Status != RefundStatus.Completed
                      || mine[0].DestinationWallet != WalletType.Shopping;
            });
            checks.Add(new Check("Every other participant got the entry fee back exactly once",
                badRefunds == 0, $"{refunds.Count} refunds for {dbParticipants.Count - winners.Count} losers; {badRefunds} wrong"));

            var badWallets = dbParticipants.Count(p =>
            {
                var main = wallets.Single(w => w.UserId == p.UserId && w.WalletType == WalletType.Main).Balance;
                var shopping = wallets.Single(w => w.UserId == p.UserId && w.WalletType == WalletType.Shopping).Balance;
                return main != _s.EntryFee - p.EntryFeePaid || shopping != (winnerIds.Contains(p.Id) ? 0 : p.EntryFeePaid);
            });
            checks.Add(new Check("Wallets add up: fee charged once, refunds credited once", badWallets == 0,
                $"{badWallets} of {dbParticipants.Count} wallets off"));
        }
        else
        {
            var moved = wallets.Count(w => w.Balance != 0);
            checks.Add(new Check("Demo auction moved no money and created no claims",
                claims.Count == 0 && refunds.Count == 0 && moved == 0,
                $"{claims.Count} claims, {refunds.Count} refunds, {moved} wallets changed"));
        }

        var expectedStatus = paid ? AuctionStatus.PrizeClaim : AuctionStatus.Completed;
        checks.Add(new Check("Auction closed all the way", auction.Status == expectedStatus && auction.WinnersDeclared && auction.RefundsProcessed,
            $"status {auction.Status}, winners declared {auction.WinnersDeclared}, refunds processed {auction.RefundsProcessed}"));

        checks.Add(new Check("Every connected client heard every bid", missing == 0,
            $"{missing} deliveries missing across {clientsMissing} clients"));

        // ------------------------------------------------------------ closing times
        var closeLocal = byTarget && dbBids.Count > 0 ? localTimeBySeq[lastSequence] : deadline - offset;
        var finishedEvent = Summary.Of(participants.Where(p => p.FinishedReceivedTs != 0)
            .Select(p => (Clock.Utc(p.FinishedReceivedTs) - closeLocal).TotalMilliseconds));
        var winnersEvent = Summary.Of(participants.Where(p => p.WinnersReceivedTs != 0)
            .Select(p => (Clock.Utc(p.WinnersReceivedTs) - closeLocal).TotalMilliseconds));
        var finalStatusMs = statusTimes.TryGetValue(expectedStatus.ToString(), out var reachedTs)
            ? (Clock.Utc(reachedTs) - closeLocal).TotalMilliseconds : double.NaN;

        var outcomes = attempts.GroupBy(a => (a.Outcome, Code: a.Accepted ? "" : a.Code ?? $"HTTP_{a.Status}"))
            .Select(g => new OutcomeCount(g.Key.Outcome, g.Key.Code, g.Count()))
            .OrderBy(o => o.Outcome).ThenByDescending(o => o.Count).ToList();

        var result = new ScenarioResult(
            n, _s.Mode, _s.Close, auctionId, created.AuctionNumber, Clock.Utc(scenarioStartTs),
            offset.TotalMilliseconds, rtt,
            Summary.Of(connectTimes), connectFailures.Count, Summary.Of(joins.Select(j => j.Ms)), joinFailures, participantCount,
            biddingSeconds, attempts.Count, accepted.Count, attempts.Count(a => a.Outcome == "rejected"),
            attempts.Count(a => a.Outcome == "failed"), participants.Sum(p => p.SkippedAsLastBidder),
            attempts.Count / biddingSeconds, accepted.Count / biddingSeconds,
            Summary.Of(attempts.Select(a => a.Ms)), Summary.Of(accepted.Select(a => a.Ms)),
            Summary.Of(attempts.Where(a => !a.InBurst).Select(a => a.Ms)),
            Summary.Of(attempts.Where(a => a.InBurst).Select(a => a.Ms)),
            outcomes,
            lastSequence * participants.Count(p => p.Hub is not null), missing, clientsMissing, outOfOrder,
            regressions, clientsRegressed, staleMs / 1000, Summary.Of(staleEpisodes), wrongAtEnd,
            propagation.Summary(), propagation.Negative,
            participants.Sum(p => p.Disconnects), participants.Sum(p => p.Reconnects),
            leaderboardLag.Summary(), boardEvents / Math.Max(1, participants.Count) / biddingSeconds, withoutFinalBoard,
            lastSequence, finalPrice, auction.Status.ToString(), context.EndedBy ?? "time limit",
            finishedEvent, winnersEvent, finalStatusMs,
            checks,
            sampler.Summarise(biddingStartTs, biddingEndTs), sampler.Summarise(joinStartTs, joinEndTs), generatorCpu,
            neighbor?.Participants.Count ?? 0, neighborAttempts.Count, neighborAttempts.Count(a => a.Accepted),
            neighborAttempts.Count(a => a.Outcome == "failed"), Summary.Of(neighborAttempts.Select(a => a.Ms)),
            attempts.Count(a => a.Outcome == "busy"), joins.Count(j => j.Code == "AUCTION_BUSY"));

        Log($"{accepted.Count} bids accepted, {result.Failed} failed; checks {(result.AllChecksPassed ? "all passed" : "FAILED")}");

        // Every attempt, for looking into the slow or failed ones afterwards.
        var closeAt = deadline - offset;
        AttemptsCsv = string.Join(Environment.NewLine, attempts.OrderBy(a => a.StartTs).Select(a => string.Join(',',
                a.Participant, Clock.Utc(a.StartTs).ToString("O"), a.Ms.ToString("0.0"), a.Status, a.Code ?? "",
                a.Sequence, a.Price, a.ServerTimestamp?.ToString("O") ?? "", a.InBurst ? 1 : 0,
                (Clock.Utc(a.StartTs) - closeAt).TotalMilliseconds.ToString("0")))
            .Prepend("participant,sent_utc,latency_ms,status,code,sequence,price,server_time_utc,in_burst,sent_ms_from_close"));
        return result;
    }

    private sealed record NeighborRun(Guid AuctionId, List<Participant> Participants, BiddingContext Context);

    /// <summary>
    /// A small, quiet auction run at the same time as the busy one: its own bidders (taken after the
    /// busy auction's), its own live connections, the steady bidding rate throughout. If a busy
    /// auction could hold up others, these bids would show it.
    /// </summary>
    private async Task<NeighborRun> StartNeighborAsync(int n, string tag, TimeSpan offset)
    {
        var paid = _s.Mode == "paid";
        var users = _pool.Skip(n).Take(_s.Neighbors).ToList();
        await _local.ResetWalletsAsync(users.Select(u => u.Id).ToList(), paid ? _s.EntryFee : 0m);

        var created = (await _api.SendAsync<DetailDto>(HttpMethod.Post, "api/v1/admin/auctions", _admin.Token,
            AuctionRequest(users.Count, $"{tag}-side"))).Require("Creating the neighbour auction");
        (await _api.SendAsync<DetailDto>(HttpMethod.Post, $"api/v1/admin/auctions/{created.Id}/publish", _admin.Token))
            .Require("Publishing the neighbour auction");

        var holder = new BiddingContextHolder();
        var participants = users.Select((u, i) => new Participant { Index = 100_000 + i, User = u }).ToList();
        foreach (var p in participants)
        {
            await p.ConnectAsync(_api.BaseUrl, created.Id, holder);
            p.ParticipantId = (await _api.JoinAsync(p.User.Token, created.Id, $"join-{tag}-side-{p.Index}"))
                .Require("Joining the neighbour auction").ParticipantId;
        }

        var started = (await _api.SendAsync<DetailDto>(HttpMethod.Post, $"api/v1/admin/auctions/{created.Id}/start",
            _admin.Token, new { reason = "Load test neighbour" })).Require("Starting the neighbour auction");
        var endsAtLocal = (started.EndsAt ?? DateTime.UtcNow.AddSeconds(_s.DurationSeconds)) - offset;

        var context = new BiddingContext
        {
            Api = _api,
            AuctionId = created.Id,
            RunTag = $"{tag}-side",
            Rate = _s.Rate,
            BurstRate = _s.Rate,
            BurstFrom = DateTime.MaxValue,
            StopAt = endsAtLocal + TimeSpan.FromSeconds(20),
            MinGapMs = _s.MinGapMs,
            Seed = _s.Seed + 7
        };
        holder.Context = context;
        Log($"neighbour auction {created.AuctionNumber} live with {participants.Count} bidders");
        return new NeighborRun(created.Id, participants, context);
    }

    private object AuctionRequest(int n, string tag)
    {
        var demo = _s.Mode == "demo";
        // Closing by time, the target is far out of reach. Closing by target, it is TargetBids bids up.
        decimal increment = 1m;
        var main = _s.Close == "target"
            ? Math.Round(_s.TargetBids * increment / 0.45m, 2)
            : 1_000_000m;

        return new
        {
            title = $"Load test {tag}",
            description = "Created by tools/AuctionLoadTest",
            startingPercentage = 10m,
            targetPercentage = 50m,
            finalPhasePercentage = 40m,
            bidIncrement = increment,
            entryFee = demo ? 0m : _s.EntryFee,
            currency = "SAR",
            requiredParticipants = demo ? 0 : n,
            startDelayAfterParticipantsSeconds = 3600,
            durationSeconds = _s.Close == "target" ? Math.Max(_s.DurationSeconds, 300) : _s.DurationSeconds,
            expirationPolicy = "EndAndDistribute",
            prizes = new[]
            {
                new { rank = 1, name = "Load test prize 1", price = main, imageUrl = (string?)null, description = (string?)null },
                new { rank = 2, name = "Load test prize 2", price = Math.Round(main * 0.2m, 2), imageUrl = (string?)null, description = (string?)null },
                new { rank = 3, name = "Load test prize 3", price = Math.Round(main * 0.1m, 2), imageUrl = (string?)null, description = (string?)null },
                new { rank = 4, name = "Load test prize 4", price = Math.Round(main * 0.05m, 2), imageUrl = (string?)null, description = (string?)null }
            },
            isDemo = demo
        };
    }

    /// <summary>Polls until the auction reaches its last state, noting when each state was first seen.</summary>
    private async Task<(DetailDto? Detail, Dictionary<string, long> StatusTimes)> WaitForEndAsync(Guid auctionId, string finalStatus)
    {
        var times = new Dictionary<string, long>();
        DetailDto? detail = null;
        var giveUp = Clock.Now.AddSeconds(120);
        while (Clock.Now < giveUp)
        {
            var r = await _api.DetailAsync(auctionId);
            if (r.Ok && r.Value is not null)
            {
                detail = r.Value;
                times.TryAdd(detail.Status, r.EndTs);
                if (detail.Status == finalStatus || detail.Status == "Cancelled") break;
            }
            await Task.Delay(250);
        }
        return (detail, times);
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
