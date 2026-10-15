using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// The front page's featured row: an administrator marks an auction, and only marked auctions come
/// back when the app asks for them.
/// </summary>
[Collection("database")]
public class FeaturedAuctionTests
{
    private readonly DatabaseFixture _fixture;

    public FeaturedAuctionTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Only_the_auctions_an_administrator_marked_are_returned()
    {
        Guid marked;
        Guid plain;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            marked = (await TestData.CreateLiveAuctionAsync(db)).Id;
            plain = (await TestData.CreateLiveAuctionAsync(db)).Id;
        }

        await using (var scope = _fixture.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();

            (await admin.SetFeaturedAsync(marked, true)).IsFeatured.Should().BeTrue();

            var reader = scope.ServiceProvider.GetRequiredService<IAuctionService>();
            var featured = await reader.ListAsync(new AuctionListQuery { Featured = true }, null);

            featured.Items.Select(a => a.Id).Should().Contain(marked).And.NotContain(plain);
        }

        // Unmarking takes it straight back out of the row.
        await using (var scope = _fixture.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();
            await admin.SetFeaturedAsync(marked, false);

            var reader = scope.ServiceProvider.GetRequiredService<IAuctionService>();
            var featured = await reader.ListAsync(new AuctionListQuery { Featured = true }, null);
            featured.Items.Select(a => a.Id).Should().NotContain(marked);
        }

        await using var verify = _fixture.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await check.Auctions.AsNoTracking().FirstAsync(a => a.Id == marked)).IsFeatured
            .Should().BeFalse();
        (await check.AuditLogs.AsNoTracking()
                .CountAsync(l => l.Action == AuditActionType.AuctionUpdated && l.EntityId == marked))
            .Should().Be(2, "marking and unmarking are both recorded");
    }

    /// <summary>
    /// The row comes back in the order an administrator dragged it into, not the order the
    /// auctions were created in.
    /// </summary>
    [Fact]
    public async Task The_featured_row_comes_back_in_the_arranged_order()
    {
        Guid first, second, third;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Created oldest to newest, so "newest first" would give the reverse of this.
            first = (await TestData.CreateLiveAuctionAsync(db)).Id;
            second = (await TestData.CreateLiveAuctionAsync(db)).Id;
            third = (await TestData.CreateLiveAuctionAsync(db)).Id;
        }

        await using (var scope = _fixture.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();

            foreach (var id in new[] { first, second, third })
            {
                await admin.SetFeaturedAsync(id, true);
            }

            await admin.ReorderFeaturedAsync(new[] { second, third, first });
        }

        await using (var scope = _fixture.CreateScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<IAuctionService>();
            var featured = await reader.ListAsync(new AuctionListQuery { Featured = true }, null);

            featured.Items.Select(a => a.Id).Take(3)
                .Should().Equal(second, third, first);
        }
    }

    /// <summary>
    /// An auction featured from somewhere else while the row was being dragged must not land in
    /// front of the arrangement just because its position is still zero.
    /// </summary>
    [Fact]
    public async Task An_auction_left_out_of_the_arrangement_goes_behind_it()
    {
        Guid arranged, latecomer;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            arranged = (await TestData.CreateLiveAuctionAsync(db)).Id;
            latecomer = (await TestData.CreateLiveAuctionAsync(db)).Id;
        }

        await using (var scope = _fixture.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();

            await admin.SetFeaturedAsync(arranged, true);
            await admin.SetFeaturedAsync(latecomer, true);

            // Only the first is named, as a browser that loaded the list before the other was
            // featured would send it.
            await admin.ReorderFeaturedAsync(new[] { arranged });
        }

        await using (var scope = _fixture.CreateScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<IAuctionService>();
            var featured = await reader.ListAsync(new AuctionListQuery { Featured = true }, null);

            var ids = featured.Items.Select(a => a.Id).ToList();
            ids.IndexOf(arranged).Should().BeLessThan(ids.IndexOf(latecomer));
        }
    }

    [Fact]
    public async Task An_unknown_auction_is_refused_rather_than_silently_skipped()
    {
        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();

        var act = () => admin.ReorderFeaturedAsync(new[] { Guid.NewGuid() });

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(ErrorCodes.AuctionNotFound);
    }

    /// <summary>
    /// The row is a place to act. An auction nobody can join or bid on has no business being
    /// advertised there, so the mark is refused rather than quietly accepted.
    /// </summary>
    [Fact]
    public async Task An_auction_that_is_not_open_cannot_be_featured()
    {
        Guid draft;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await TestData.CreateLiveAuctionAsync(db);

            auction.Status = AuctionStatus.Draft;
            await db.SaveChangesAsync();
            draft = auction.Id;
        }

        await using var check = _fixture.CreateScope();
        var admin = check.ServiceProvider.GetRequiredService<IAuctionAdminService>();

        var act = () => admin.SetFeaturedAsync(draft, true);

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(ErrorCodes.InvalidAuctionState);
    }

    /// <summary>
    /// Removing is always allowed, whatever the auction has become since — that is how a mistake
    /// gets undone.
    /// </summary>
    [Fact]
    public async Task A_finished_auction_can_still_be_unmarked()
    {
        Guid id;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            id = (await TestData.CreateLiveAuctionAsync(db)).Id;
        }

        await using (var scope = _fixture.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();
            await admin.SetFeaturedAsync(id, true);

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await db.Auctions.FirstAsync(a => a.Id == id);
            auction.Status = AuctionStatus.Completed;
            await db.SaveChangesAsync();
        }

        await using var check = _fixture.CreateScope();
        var admin2 = check.ServiceProvider.GetRequiredService<IAuctionAdminService>();

        var act = () => admin2.SetFeaturedAsync(id, false);
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// The flag is not enough on its own. An auction ends on a worker's clock, so there is always
    /// a moment where a finished auction still carries the mark; the row must not show it even
    /// then.
    /// </summary>
    [Fact]
    public async Task An_auction_that_has_ended_leaves_the_row_even_while_still_marked()
    {
        Guid id;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            id = (await TestData.CreateLiveAuctionAsync(db)).Id;
        }

        await using (var scope = _fixture.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<IAuctionAdminService>();
            await admin.SetFeaturedAsync(id, true);

            var reader = scope.ServiceProvider.GetRequiredService<IAuctionService>();
            var before = await reader.ListAsync(new AuctionListQuery { Featured = true }, null);
            before.Items.Select(a => a.Id).Should().Contain(id);
        }

        // The auction finishes, and nothing has cleared the mark yet.
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var auction = await db.Auctions.FirstAsync(a => a.Id == id);
            auction.Status = AuctionStatus.Finished;
            await db.SaveChangesAsync();

            auction.IsFeatured.Should().BeTrue("this test is about a flag that is still set");
        }

        await using (var scope = _fixture.CreateScope())
        {
            var reader = scope.ServiceProvider.GetRequiredService<IAuctionService>();
            var after = await reader.ListAsync(new AuctionListQuery { Featured = true }, null);

            after.Items.Select(a => a.Id).Should().NotContain(id);
        }
    }

    [Fact]
    public async Task The_same_auction_twice_is_refused()
    {
        Guid id;

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            id = (await TestData.CreateLiveAuctionAsync(db)).Id;
        }

        await using var check = _fixture.CreateScope();
        var admin = check.ServiceProvider.GetRequiredService<IAuctionAdminService>();

        var act = () => admin.ReorderFeaturedAsync(new[] { id, id });

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }
}
