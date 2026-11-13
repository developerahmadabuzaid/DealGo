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
/// Exclusive offers: products of their own, shown only in their section, sold only while they run
/// and at most a set number to each customer.
/// </summary>
[Collection("database")]
public class ExclusiveOfferTests
{
    private readonly DatabaseFixture _fixture;

    public ExclusiveOfferTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<(Guid ProductId, Guid UserId)> OfferAsync(
        DateTime starts, DateTime ends, int maxPerUser = 2, int stock = 10)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var product = await TestData.CreateStoreProductAsync(db, price: 50m, stock: stock);
        product.Name = $"Offer {Guid.NewGuid():N}"[..20];
        product.CompareAtPrice = 80m;
        product.IsExclusiveOffer = true;
        product.OfferStartsAt = starts;
        product.OfferEndsAt = ends;
        product.OfferMaxPerUser = maxPerUser;
        var user = await TestData.CreateUserAsync(db, "Buyer", mainBalance: 1000m);
        await db.SaveChangesAsync();
        return (product.Id, user.Id);
    }

    private async Task<StoreOrderDto> BuyAsync(Guid userId, Guid productId, int quantity)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IStoreOrderService>().CheckoutAsync(userId,
            new CheckoutRequest([new CheckoutItemRequest(productId, quantity)], StorePaymentMethod.MainWallet, null,
                $"offer-{Guid.NewGuid():N}"));
    }

    [Fact]
    public async Task A_running_offer_shows_only_in_its_own_section()
    {
        var (live, _) = await OfferAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(5));
        var (later, _) = await OfferAsync(DateTime.UtcNow.AddHours(2), DateTime.UtcNow.AddHours(5));

        await using var scope = _fixture.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStoreCatalogService>();

        var home = await catalog.GetHomeAsync(null);
        home.ExclusiveOffers!.Select(p => p.Id).Should().Contain(live).And.NotContain(later);
        var shown = home.ExclusiveOffers!.Single(p => p.Id == live);
        shown.Offer!.MaxPerUser.Should().Be(2);
        shown.CompareAtPrice.Should().Be(80m);

        // Not among the store's ordinary products, nor in a search for it.
        var all = await catalog.GetProductsAsync(new StoreProductQuery { PageSize = 100, Search = shown.Name });
        all.Items.Should().NotContain(p => p.Id == live);
        home.NewArrivals.Should().NotContain(p => p.Id == live);

        // Its own list, and its own page, do show it.
        var offers = await catalog.GetProductsAsync(new StoreProductQuery { PageSize = 100, ExclusiveOffers = true });
        offers.Items.Select(p => p.Id).Should().Contain(live).And.NotContain(later);
        (await catalog.GetProductAsync(live)).Offer.Should().NotBeNull();
    }

    [Fact]
    public async Task A_customer_can_buy_no_more_than_the_limit_across_orders()
    {
        var (product, user) = await OfferAsync(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(5), maxPerUser: 2);

        var first = await BuyAsync(user, product, 1);
        first.Total.Should().Be(50m);

        var tooMany = () => BuyAsync(user, product, 2);
        (await tooMany.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.OfferLimitReached);

        await BuyAsync(user, product, 1);
        var more = () => BuyAsync(user, product, 1);
        (await more.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.OfferLimitReached);

        // A cancelled order no longer counts.
        await using (var scope = _fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStoreOrderService>().CancelOrderAsync(user, first.Id, "changed my mind");
        }
        (await BuyAsync(user, product, 1)).Total.Should().Be(50m);
    }

    [Fact]
    public async Task An_offer_cannot_be_bought_before_it_starts_or_after_it_ends()
    {
        var (early, user) = await OfferAsync(DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(5));
        var (late, _) = await OfferAsync(DateTime.UtcNow.AddHours(-5), DateTime.UtcNow.AddMinutes(-1));

        var before = () => BuyAsync(user, early, 1);
        var after = () => BuyAsync(user, late, 1);

        (await before.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ProductNotAvailable);
        (await after.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ProductNotAvailable);
    }

    [Fact]
    public async Task An_offer_needs_its_dates_and_a_limit()
    {
        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IStoreAdminService>();

        var noDates = () => admin.CreateProductAsync(new CreateStoreProductRequest(
            "عرض", null, null, null, $"OFR-{Guid.NewGuid():N}"[..20], 50m, 80m, null, 5,
            StoreProductStatus.Active, StorePaymentMode.Both, false, false, 0m, null, null,
            IsExclusiveOffer: true, OfferMaxPerUser: 1));
        (await noDates.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);

        var created = await admin.CreateProductAsync(new CreateStoreProductRequest(
            "عرض", null, null, null, $"OFR-{Guid.NewGuid():N}"[..20], 50m, 80m, null, 5,
            StoreProductStatus.Active, StorePaymentMode.Both, false, false, 0m, null, null,
            IsExclusiveOffer: true, OfferStartsAt: DateTime.UtcNow, OfferEndsAt: DateTime.UtcNow.AddDays(1), OfferMaxPerUser: 3));
        created.Offer!.MaxPerUser.Should().Be(3);

        var listed = await admin.GetProductsAsync(new StoreProductQuery { PageSize = 100, ExclusiveOffers = true });
        listed.Items.Should().Contain(p => p.Id == created.Id);
    }
}
