namespace AuctionLoadTest;

/// <summary>
/// `play`: a demo auction on a LOCAL server where a person bids alongside simulated bidders, to try
/// the live-auction screens with real activity. The bots join straight away; once someone else has
/// joined too, a short countdown runs and the auction starts, and the bots bid at a human pace
/// (rising in the closing seconds) until it ends. With --rounds 0 a fresh auction follows each one.
/// </summary>
static class Play
{
    public static async Task<int> RunAsync(Args a)
    {
        var baseUrl = new Uri(a.Get("base-url", "http://127.0.0.1:5199/"));
        if (!baseUrl.IsLoopback)
        {
            throw new ArgumentException($"{baseUrl} is not on this machine. 'play' creates accounts and bids; it runs against a local server only.");
        }

        var bots = a.Int("bots", 20);
        var rounds = a.Int("rounds", 1);
        var countdown = a.Int("countdown", 20);
        var duration = a.Int("duration", 180);
        var rate = a.Double("rate", 0.05);
        var burstRate = a.Double("burst-rate", 0.25);
        var burstSeconds = a.Int("burst-seconds", 15);
        var increment = (decimal)a.Double("increment", 1);
        var value = (decimal)a.Double("value", 1000);
        var waitForPeople = a.Int("wait-for", 1);

        var local = new LocalSetup(a.Require("db"), a.Require("jwt-secret"),
            a.Get("jwt-issuer", "auction-platform"), a.Get("jwt-audience", "auction-platform-clients"));
        using var api = new Api(baseUrl, TimeSpan.FromSeconds(30));
        var admin = await local.EnsureAdminAsync();
        var pool = await local.EnsurePlayBotsAsync(bots);
        Log($"{pool.Count} bots ready ({string.Join("، ", pool.Take(3).Select(p => p.Name))} …)");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); Log("stopping after this step…"); };

        for (var round = 1; rounds == 0 || round <= rounds; round++)
        {
            if (stop.IsCancellationRequested) break;
            await PlayRoundAsync(round);
        }
        return 0;

        async Task PlayRoundAsync(int round)
        {
            var tag = $"play-{DateTime.UtcNow:HHmmss}";
            var created = (await api.SendAsync<DetailDto>(HttpMethod.Post, "api/v1/admin/auctions", admin.Token, new
            {
                title = $"مزاد تجريبي {round}",
                description = "مزاد تجريبي محلي للاختبار",
                startingPercentage = 10m,
                targetPercentage = 50m,
                finalPhasePercentage = 40m,
                bidIncrement = increment,
                entryFee = 0m,
                currency = "SAR",
                requiredParticipants = 0,
                startDelayAfterParticipantsSeconds = 3600,
                durationSeconds = duration,
                expirationPolicy = "EndAndDistribute",
                prizes = Array.Empty<object>(),
                demoValue = value,
                isDemo = true
            })).Require("Creating the demo auction");
            var auctionId = created.Id;
            (await api.SendAsync<DetailDto>(HttpMethod.Post, $"api/v1/admin/auctions/{auctionId}/publish", admin.Token))
                .Require("Publishing the demo auction");

            var holder = new BiddingContextHolder();
            var participants = pool.Select((u, i) => new Participant { Index = i, User = u }).ToList();
            foreach (var p in participants)
            {
                await p.ConnectAsync(api.BaseUrl, auctionId, holder);
                var joined = await api.JoinAsync(p.User.Token, auctionId, $"join-{tag}-{p.Index}");
                p.ParticipantId = joined.Require($"{p.User.Name} joining").ParticipantId;
            }

            Console.WriteLine();
            Log($"{created.AuctionNumber} «{created.Title}» is open, with {bots} bots in it.");
            Log($"Open the app, find «{created.Title}» and join it. It starts {countdown} s after {waitForPeople} person(s) other than the bots have joined.");

            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                var detail = (await api.DetailAsync(auctionId)).Value;
                if (detail is not null && detail.ParticipantCount - bots >= waitForPeople) break;
                await Task.Delay(2000, stop.Token);
            }

            Log($"You're in. Starting in {countdown} s…");
            await Task.Delay(TimeSpan.FromSeconds(countdown), stop.Token);

            var started = (await api.SendAsync<DetailDto>(HttpMethod.Post, $"api/v1/admin/auctions/{auctionId}/start",
                admin.Token, new { reason = "Local play-along" })).Require("Starting the auction");
            var (offset, _) = await api.EstimateClockOffsetAsync(auctionId, 4);
            var endsAt = (started.EndsAt ?? DateTime.UtcNow.AddSeconds(duration)) - offset;
            Log($"LIVE until {endsAt.ToLocalTime():HH:mm:ss}. The bots are bidding.");

            var context = new BiddingContext
            {
                Api = api,
                AuctionId = auctionId,
                RunTag = tag,
                Rate = rate,
                BurstRate = burstRate,
                BurstFrom = endsAt - TimeSpan.FromSeconds(burstSeconds),
                StopAt = endsAt + TimeSpan.FromSeconds(30),
                MinGapMs = 1500,
                Seed = Environment.TickCount
            };
            holder.Context = context;

            var progress = Task.Run(async () =>
            {
                while (!context.Ended && DateTime.UtcNow < context.StopAt)
                {
                    await Task.Delay(10_000);
                    var top = context.Bids.Values.OrderByDescending(b => b.Sequence).FirstOrDefault();
                    if (top is not null) Log($"price {top.Price:0.00} after {top.Sequence} bids");
                }
            });

            await Task.WhenAll(participants.Select(p => p.BidLoopAsync(context)));
            await progress;

            var final = (await api.DetailAsync(auctionId)).Value;
            Log($"Finished ({context.EndedBy ?? "time"}): {final?.Status}, final price {final?.CurrentPrice:0.00}.");
            foreach (var p in participants) await p.DisconnectAsync();
        }
    }

    private static void Log(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
}
