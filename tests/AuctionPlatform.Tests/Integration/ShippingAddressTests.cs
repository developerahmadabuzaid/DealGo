using AuctionPlatform.Application.Prizes;
using AuctionPlatform.Application.Store;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// Delivery addresses belong to one account. Nobody else can read, change or ship to them, and
/// what an order or a prize was sent to never changes after the fact.
/// </summary>
[Collection("database")]
public class ShippingAddressTests
{
    private readonly DatabaseFixture _fixture;

    public ShippingAddressTests(DatabaseFixture fixture) => _fixture = fixture;

    private static SaveShippingAddressRequest Riyadh(
        string name = "أحمد علي", bool isDefault = false, double? lat = 24.7136, double? lng = 46.6753) =>
        new("  " + name + " ", "0551234567", "الرياض", "العليا", "طريق الملك فهد", "1234",
            "بجانب المسجد", "rrrd 2929", lat, lng, isDefault);

    private async Task<Guid> NewUserAsync(decimal shopping = 0m)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await TestData.CreateUserAsync(db, "Customer", shoppingBalance: shopping)).Id;
    }

    private async Task<T> WithAddressesAsync<T>(Guid userId, Func<IShippingAddressService, Task<T>> act)
    {
        await using var scope = _fixture.CreateScope(userId);
        return await act(scope.ServiceProvider.GetRequiredService<IShippingAddressService>());
    }

    [Fact]
    public async Task An_address_is_cleaned_up_and_the_first_one_becomes_the_default()
    {
        var userId = await NewUserAsync();

        var saved = await WithAddressesAsync(userId, a => a.CreateAsync(userId, Riyadh()));

        saved.FullName.Should().Be("أحمد علي");
        saved.Phone.Should().Be("+966551234567");
        saved.Country.Should().Be("SA");
        saved.ShortAddress.Should().Be("RRRD2929");
        saved.Latitude.Should().Be(24.7136);
        saved.Longitude.Should().Be(46.6753);
        saved.IsDefault.Should().BeTrue("there was no other address");

        var second = await WithAddressesAsync(userId, a => a.CreateAsync(userId, Riyadh("سارة")));
        second.IsDefault.Should().BeFalse();

        var promoted = await WithAddressesAsync(userId, a => a.SetDefaultAsync(userId, second.Id));
        promoted.IsDefault.Should().BeTrue();

        var list = await WithAddressesAsync(userId, a => a.GetAsync(userId));
        list.Should().HaveCount(2);
        list[0].Id.Should().Be(second.Id, "the default comes first");
        list.Count(x => x.IsDefault).Should().Be(1);
    }

    [Fact]
    public async Task Another_account_cannot_see_change_or_remove_an_address()
    {
        var owner = await NewUserAsync();
        var stranger = await NewUserAsync();
        var address = await WithAddressesAsync(owner, a => a.CreateAsync(owner, Riyadh()));

        (await WithAddressesAsync(stranger, a => a.GetAsync(stranger))).Should().BeEmpty();

        var update = () => WithAddressesAsync(stranger, a => a.UpdateAsync(stranger, address.Id, Riyadh("لص")));
        var makeDefault = () => WithAddressesAsync(stranger, a => a.SetDefaultAsync(stranger, address.Id));
        var delete = () => WithAddressesAsync(stranger, async a =>
        {
            await a.DeleteAsync(stranger, address.Id);
            return 0;
        });

        (await update.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AddressNotFound);
        (await makeDefault.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AddressNotFound);
        (await delete.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AddressNotFound);

        var untouched = (await WithAddressesAsync(owner, a => a.GetAsync(owner))).Single();
        untouched.FullName.Should().Be("أحمد علي");
    }

    [Theory]
    [InlineData("", "0551234567", "الرياض", "العليا", "طريق", null, null, null)]
    [InlineData("أحمد", "12345", "الرياض", "العليا", "طريق", null, null, null)]
    [InlineData("أحمد", "0551234567", "", "العليا", "طريق", null, null, null)]
    [InlineData("أحمد", "0551234567", "الرياض", "", "طريق", null, null, null)]
    [InlineData("أحمد", "0551234567", "الرياض", "العليا", "", null, null, null)]
    [InlineData("أحمد", "0551234567", "الرياض", "العليا", "طريق", "ABC123", null, null)]
    [InlineData("أحمد", "0551234567", "الرياض", "العليا", "طريق", null, 51.5, -0.12)]
    [InlineData("أحمد", "0551234567", "الرياض", "العليا", "طريق", null, 24.7, null)]
    public async Task An_incomplete_or_impossible_address_is_refused(
        string name, string phone, string city, string district, string street,
        string? shortAddress, double? lat, double? lng)
    {
        var userId = await NewUserAsync();

        var save = () => WithAddressesAsync(userId, a => a.CreateAsync(userId,
            new SaveShippingAddressRequest(name, phone, city, district, street,
                ShortAddress: shortAddress, Latitude: lat, Longitude: lng)));

        var error = (await save.Should().ThrowAsync<DomainException>()).Which;
        error.Code.Should().BeOneOf(ErrorCodes.ValidationFailed, ErrorCodes.PhoneNumberInvalid);
        (await WithAddressesAsync(userId, a => a.GetAsync(userId))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_delivery_outside_the_kingdom_is_refused()
    {
        var userId = await NewUserAsync();

        var save = () => WithAddressesAsync(userId, a => a.CreateAsync(userId,
            Riyadh() with { Country = "AE" }));

        (await save.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Checkout_refuses_someone_elses_address_and_accepts_your_own()
    {
        var buyer = await NewUserAsync(shopping: 500m);
        var other = await NewUserAsync();
        var theirs = await WithAddressesAsync(other, a => a.CreateAsync(other, Riyadh()));
        var mine = await WithAddressesAsync(buyer, a => a.CreateAsync(buyer, Riyadh()));

        Guid productId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            productId = (await TestData.CreateStoreProductAsync(db, 50m, 10)).Id;
        }

        await using (var scope = _fixture.CreateScope(buyer))
        {
            var orders = scope.ServiceProvider.GetRequiredService<IStoreOrderService>();

            var stolen = () => orders.CheckoutAsync(buyer, new CheckoutRequest(
                new[] { new CheckoutItemRequest(productId, 1) },
                StorePaymentMethod.ShoppingWallet, theirs.Id, $"steal-{Guid.NewGuid():N}"));

            (await stolen.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AddressNotFound);
        }

        await using (var scope = _fixture.CreateScope(buyer))
        {
            var orders = scope.ServiceProvider.GetRequiredService<IStoreOrderService>();
            var order = await orders.CheckoutAsync(buyer, new CheckoutRequest(
                new[] { new CheckoutItemRequest(productId, 1) },
                StorePaymentMethod.ShoppingWallet, mine.Id, $"own-{Guid.NewGuid():N}"));

            order.ShippingAddress!.Id.Should().Be(mine.Id);
        }
    }

    [Fact]
    public async Task A_delivered_product_cannot_be_ordered_without_an_address()
    {
        var buyer = await NewUserAsync(shopping: 500m);

        Guid productId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await TestData.CreateStoreProductAsync(db, 50m, 10);
            product.RequiresShipping = true;
            await db.SaveChangesAsync();
            productId = product.Id;
        }

        await using var buy = _fixture.CreateScope(buyer);
        var orders = buy.ServiceProvider.GetRequiredService<IStoreOrderService>();
        var request = new CheckoutRequest(
            new[] { new CheckoutItemRequest(productId, 1) },
            StorePaymentMethod.ShoppingWallet, null, $"no-address-{Guid.NewGuid():N}");

        (await orders.QuoteAsync(buyer, request)).RequiresShippingAddress.Should().BeTrue();

        var checkout = () => orders.CheckoutAsync(buyer, request);
        (await checkout.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AddressRequired);
    }

    [Fact]
    public async Task Changing_or_removing_an_address_an_order_went_to_leaves_the_order_as_it_was()
    {
        var buyer = await NewUserAsync(shopping: 500m);
        var address = await WithAddressesAsync(buyer, a => a.CreateAsync(buyer, Riyadh()));

        Guid productId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            productId = (await TestData.CreateStoreProductAsync(db, 20m, 10)).Id;
        }

        Guid orderId;
        await using (var scope = _fixture.CreateScope(buyer))
        {
            orderId = (await scope.ServiceProvider.GetRequiredService<IStoreOrderService>().CheckoutAsync(
                buyer, new CheckoutRequest(new[] { new CheckoutItemRequest(productId, 1) },
                    StorePaymentMethod.ShoppingWallet, address.Id, $"keep-{Guid.NewGuid():N}"))).Id;
        }

        var edited = await WithAddressesAsync(buyer, a => a.UpdateAsync(buyer, address.Id, Riyadh("خالد")));
        edited.Id.Should().NotBe(address.Id, "the address the order used is kept as it was");
        edited.FullName.Should().Be("خالد");
        edited.IsDefault.Should().BeTrue("it replaces the default");

        await using (var scope = _fixture.CreateScope(buyer))
        {
            var order = await scope.ServiceProvider.GetRequiredService<IStoreOrderService>()
                .GetOrderAsync(buyer, orderId);
            order.ShippingAddress!.FullName.Should().Be("أحمد علي");
        }

        var list = await WithAddressesAsync(buyer, a => a.GetAsync(buyer));
        list.Should().ContainSingle().Which.Id.Should().Be(edited.Id);

        // The retired one cannot be used again, by id, for a new order.
        await using (var scope = _fixture.CreateScope(buyer))
        {
            var reuse = () => scope.ServiceProvider.GetRequiredService<IStoreOrderService>().CheckoutAsync(
                buyer, new CheckoutRequest(new[] { new CheckoutItemRequest(productId, 1) },
                    StorePaymentMethod.ShoppingWallet, address.Id, $"reuse-{Guid.NewGuid():N}"));
            (await reuse.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AddressNotFound);
        }
    }

    [Fact]
    public async Task Removing_the_default_hands_the_default_to_another_address()
    {
        var userId = await NewUserAsync();
        var first = await WithAddressesAsync(userId, a => a.CreateAsync(userId, Riyadh()));
        var second = await WithAddressesAsync(userId, a => a.CreateAsync(userId, Riyadh("سارة")));

        await WithAddressesAsync(userId, async a =>
        {
            await a.DeleteAsync(userId, first.Id);
            return 0;
        });

        var list = await WithAddressesAsync(userId, a => a.GetAsync(userId));
        list.Should().ContainSingle().Which.Should().Match<ShippingAddressDto>(x => x.Id == second.Id && x.IsDefault);
    }

    [Fact]
    public async Task A_prize_is_only_delivered_to_the_winners_own_address()
    {
        var winner = await NewUserAsync();
        var other = await NewUserAsync();
        var theirs = await WithAddressesAsync(other, a => a.CreateAsync(other, Riyadh()));
        var mine = await WithAddressesAsync(winner, a => a.CreateAsync(winner, Riyadh()));
        var claimId = await CreatePaidClaimAsync(winner);

        await using (var scope = _fixture.CreateScope(winner))
        {
            var claims = scope.ServiceProvider.GetRequiredService<IPrizeClaimService>();

            var stolen = () => claims.ClaimAsync(winner, claimId, theirs.Id);
            (await stolen.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.AddressNotFound);

            var nowhere = () => claims.ClaimAsync(winner, claimId, null);
            (await nowhere.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
        }

        await using (var scope = _fixture.CreateScope(winner))
        {
            var claim = await scope.ServiceProvider.GetRequiredService<IPrizeClaimService>()
                .ClaimAsync(winner, claimId, mine.Id);

            claim.Status.Should().Be(PrizeClaimStatus.Claimed);
            claim.ShippingAddress!.Id.Should().Be(mine.Id);
        }

        // Nor can a stranger point the winner's prize at their own address.
        await using (var scope = _fixture.CreateScope(other))
        {
            var hijack = () => scope.ServiceProvider.GetRequiredService<IPrizeClaimService>()
                .ClaimAsync(other, claimId, theirs.Id);
            await hijack.Should().ThrowAsync<DomainException>();
        }
    }

    private async Task<Guid> CreatePaidClaimAsync(Guid userId)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var auction = await TestData.CreateLiveAuctionAsync(db);
        var participant = await TestData.AddParticipantAsync(db, auction.Id, userId);
        var prize = await db.AuctionPrizes.FirstAsync(p => p.AuctionId == auction.Id && p.Rank == 1);

        var win = new Winner
        {
            AuctionId = auction.Id, ParticipantId = participant.Id, UserId = userId,
            Rank = 1, PrizeId = prize.Id, HighestPrice = 2480m
        };
        db.Winners.Add(win);
        await db.SaveChangesAsync();

        var claim = new PrizeClaim
        {
            WinnerId = win.Id, UserId = userId, AuctionId = auction.Id, PrizeId = prize.Id,
            Status = PrizeClaimStatus.Paid, PaymentAmount = 2480m, Currency = Money.DefaultCurrency,
            PaidAt = DateTime.UtcNow
        };
        db.PrizeClaims.Add(claim);
        await db.SaveChangesAsync();

        return claim.Id;
    }
}
