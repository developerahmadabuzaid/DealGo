using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Domain.Common;
using FluentAssertions;

namespace AuctionPlatform.Tests.Unit;

public class AuctionQueueTests
{
    [Fact]
    public async Task One_auction_serves_one_request_at_a_time()
    {
        var queue = new AuctionQueue();
        var auction = Guid.NewGuid();

        var first = await queue.EnterAsync(auction, "bid", CancellationToken.None);
        var second = queue.EnterAsync(auction, "bid", CancellationToken.None).AsTask();

        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse("the first request still has the turn");

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Different_auctions_do_not_wait_for_each_other()
    {
        var queue = new AuctionQueue();

        using var a = await queue.EnterAsync(Guid.NewGuid(), "bid", CancellationToken.None);
        var b = queue.EnterAsync(Guid.NewGuid(), "bid", CancellationToken.None).AsTask();

        (await b.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Turns_go_first_come_first_served()
    {
        var queue = new AuctionQueue();
        var auction = Guid.NewGuid();
        var served = new List<int>();

        var holder = await queue.EnterAsync(auction, "bid", CancellationToken.None);
        var waiting = Enumerable.Range(1, 8).Select(async i =>
        {
            using (await queue.EnterAsync(auction, "bid", CancellationToken.None))
            {
                lock (served) served.Add(i);
            }
        }).ToList();

        holder.Dispose();
        await Task.WhenAll(waiting).WaitAsync(TimeSpan.FromSeconds(5));

        served.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
    }

    [Fact]
    public async Task Giving_up_while_waiting_leaves_the_queue_without_a_turn()
    {
        var queue = new AuctionQueue();
        var auction = Guid.NewGuid();
        using var cancel = new CancellationTokenSource();

        var holder = await queue.EnterAsync(auction, "bid", CancellationToken.None);
        var quitter = queue.EnterAsync(auction, "bid", cancel.Token).AsTask();
        var next = queue.EnterAsync(auction, "bid", CancellationToken.None).AsTask();

        cancel.Cancel();
        var gaveUp = async () => await quitter;
        await gaveUp.Should().ThrowAsync<OperationCanceledException>();

        holder.Dispose();
        (await next.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();

        queue.ActiveLines.Should().Be(0, "nobody is waiting or served, so nothing is kept");
    }

    [Fact]
    public async Task An_idle_auction_leaves_nothing_behind()
    {
        var queue = new AuctionQueue();

        for (var i = 0; i < 100; i++)
        {
            using (await queue.EnterAsync(Guid.NewGuid(), "bid", CancellationToken.None)) { }
        }

        queue.ActiveLines.Should().Be(0);
    }

    [Fact]
    public async Task A_full_line_refuses_at_once_and_says_the_bid_was_not_placed()
    {
        var queue = new AuctionQueue(maxWaitingPerAuction: 2);
        var auction = Guid.NewGuid();

        var served = await queue.EnterAsync(auction, "bid", CancellationToken.None);
        var first = queue.EnterAsync(auction, "bid", CancellationToken.None).AsTask();
        var second = queue.EnterAsync(auction, "bid", CancellationToken.None).AsTask();

        var third = async () => await queue.EnterAsync(auction, "bid", CancellationToken.None);
        (await third.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AuctionBusy);

        // Another auction is not affected by this one being full.
        (await queue.EnterAsync(Guid.NewGuid(), "bid", CancellationToken.None)).Dispose();

        served.Dispose();
        (await first).Dispose();
        (await second).Dispose();
        queue.ActiveLines.Should().Be(0);
    }

    [Fact]
    public async Task Waiting_too_long_is_refused_before_the_app_gives_up()
    {
        var queue = new AuctionQueue(maxWait: TimeSpan.FromMilliseconds(200));
        var auction = Guid.NewGuid();

        var stuck = await queue.EnterAsync(auction, "bid", CancellationToken.None);
        var waited = async () => await queue.EnterAsync(auction, "bid", CancellationToken.None);

        (await waited.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AuctionBusy);

        stuck.Dispose();
        queue.ActiveLines.Should().Be(0, "the request that gave up left the line");
    }

    [Fact]
    public async Task Shutting_down_releases_everyone_waiting_and_admits_nobody_new()
    {
        var queue = new AuctionQueue();
        var auction = Guid.NewGuid();

        var serving = await queue.EnterAsync(auction, "bid", CancellationToken.None);
        var waiting = queue.EnterAsync(auction, "bid", CancellationToken.None).AsTask();

        queue.Close();

        var released = async () => await waiting;
        (await released.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ServiceUnavailable);

        var late = async () => await queue.EnterAsync(Guid.NewGuid(), "bid", CancellationToken.None);
        (await late.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ServiceUnavailable);

        // The one already being served finishes normally.
        serving.Dispose();
        queue.ActiveLines.Should().Be(0);
    }

    [Fact]
    public void A_closed_auction_is_refused_with_what_the_database_said()
    {
        var queue = new AuctionQueue();
        var auction = Guid.NewGuid();

        queue.RefusalFor(auction).Should().BeNull();

        queue.MarkClosed(auction, ErrorCodes.AuctionFinished, "The auction duration has elapsed.");

        var refusal = queue.RefusalFor(auction);
        refusal.Should().NotBeNull();
        refusal!.Code.Should().Be(ErrorCodes.AuctionFinished);
        refusal.Message.Should().Be("The auction duration has elapsed.");
        queue.RefusalFor(Guid.NewGuid()).Should().BeNull("only that auction is closed");
    }
}
