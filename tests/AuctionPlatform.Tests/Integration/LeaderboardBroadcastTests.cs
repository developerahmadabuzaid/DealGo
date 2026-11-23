using System.Collections.Concurrent;
using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Realtime;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// A bid no longer queries and sends the leaderboard itself: it marks the auction, and the
/// broadcaster sends the list as it stands a few times a second.
/// </summary>
[Collection("database")]
public class LeaderboardBroadcastTests
{
    private readonly DatabaseFixture _fixture;

    public LeaderboardBroadcastTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Bids_are_sent_as_one_up_to_date_leaderboard()
    {
        Guid auctionId;
        Guid a, b;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db, currentPrice: 1000m, targetPrice: 2500m);
            a = (await TestData.CreateUserAsync(db, "Leader A")).Id;
            b = (await TestData.CreateUserAsync(db, "Leader B")).Id;
            await TestData.AddParticipantAsync(db, auction.Id, a);
            await TestData.AddParticipantAsync(db, auction.Id, b);
            auctionId = auction.Id;
        }

        for (var i = 0; i < 4; i++)
        {
            var user = i % 2 == 0 ? a : b;
            await using var scope = _fixture.CreateScope(user);
            await scope.ServiceProvider.GetRequiredService<IBidService>()
                .PlaceBidAsync(auctionId, user, $"lb-{i}");
        }

        // Each bid asked for a broadcast rather than sending one itself.
        _fixture.LeaderboardRequests.Requested.Count(id => id == auctionId).Should().Be(4);

        // The real broadcaster turns those four requests into one list, with both bidders on it.
        var notifier = new RecordingNotifier();
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(_fixture.ConnectionString));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddSingleton<IAuctionRealtimeNotifier>(notifier);
        await using var provider = services.BuildServiceProvider();

        using var broadcaster = new LeaderboardBroadcaster(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<LeaderboardBroadcaster>.Instance);
        for (var i = 0; i < 4; i++) broadcaster.Request(auctionId);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (notifier.Sent.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(50);
        await Task.Delay(LeaderboardBroadcaster.Interval * 3);

        notifier.Sent.Should().ContainSingle("four requests between two flushes make one broadcast");
        var (sentFor, board) = notifier.Sent.Single();
        sentFor.Should().Be(auctionId);
        board.Select(e => e.UserId).Should().BeEquivalentTo(new[] { a, b });
        board[0].HighestPrice.Should().Be(1080m, "it shows the leader after the fourth bid");
    }

    private sealed class RecordingNotifier : IAuctionRealtimeNotifier
    {
        public ConcurrentQueue<(Guid, IReadOnlyList<LeaderboardEntryDto>)> Sent { get; } = new();

        public Task LeaderboardUpdatedAsync(Guid a, IReadOnlyList<LeaderboardEntryDto> l, CancellationToken ct = default)
        {
            Sent.Enqueue((a, l));
            return Task.CompletedTask;
        }

        public Task ParticipantCountChangedAsync(Guid a, int c, int r, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionReadyAsync(Guid a, int c, DateTime t, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionStartScheduledAsync(Guid a, DateTime s, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionStartTimeChangedAsync(Guid a, DateTime? p, DateTime n, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionStartedAsync(Guid a, DateTime s, DateTime e, decimal p, CancellationToken ct = default) => Task.CompletedTask;
        public Task BidPlacedAsync(Guid a, BidBroadcast b, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionPriceChangedAsync(Guid a, decimal c, decimal t, decimal p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ParticipantRankChangedAsync(Guid a, Guid u, int r, decimal h, CancellationToken ct = default) => Task.CompletedTask;
        public Task FinalPhaseStartedAsync(Guid a, decimal c, decimal t, int? r, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionFinishedAsync(Guid a, decimal p, bool t, DateTime f, CancellationToken ct = default) => Task.CompletedTask;
        public Task WinnersDeclaredAsync(Guid a, IReadOnlyList<WinnerDto> w, CancellationToken ct = default) => Task.CompletedTask;
        public Task PrizeClaimAvailableAsync(Guid u, Guid c, Guid a, decimal p, string cur, CancellationToken ct = default) => Task.CompletedTask;
        public Task ShoppingCreditAddedAsync(Guid u, decimal amt, decimal bal, string cur, Guid? a, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionCancelledAsync(Guid a, string? r, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionPausedAsync(Guid a, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionResumedAsync(Guid a, DateTime? e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
