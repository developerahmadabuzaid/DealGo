using AuctionPlatform.Application.Store;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// An offer is a product priced below its own "before" price. Nothing else counts, and the
/// catalogue must report it the same way everywhere.
/// </summary>
[Collection("database")]
public class StoreOffersTests
{
    private readonly DatabaseFixture _fixture;

    public StoreOffersTests(DatabaseFixture fixture) => _fixture = fixture;

    private async Task<StoreProduct> AddProductAsync(decimal price, decimal? compareAt)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var product = new StoreProduct
        {
            Name = "منتج اختبار",
            Slug = $"test-{Guid.NewGuid():N}",
            Sku = $"TEST-{Guid.NewGuid():N}",
            Price = price,
            CompareAtPrice = compareAt,
            Currency = Money.DefaultCurrency,
            StockQuantity = 5,
            Status = StoreProductStatus.Active,
            PaymentMode = StorePaymentMode.Both
        };

        db.StoreProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    [Fact]
    public async Task A_discounted_product_is_listed_as_an_offer_with_its_percentage()
    {
        var product = await AddProductAsync(60m, 100m);

        await using var scope = _fixture.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStoreCatalogService>();

        var offers = await catalog.GetProductsAsync(new StoreProductQuery
        {
            OnOfferOnly = true,
            PageSize = 100
        });

        var listed = offers.Items.Should().ContainSingle(p => p.Id == product.Id).Subject;
        listed.CompareAtPrice.Should().Be(100m);
        listed.DiscountPercentage.Should().Be(40);
        listed.IsOnOffer.Should().BeTrue();

        var detail = await catalog.GetProductAsync(product.Id);
        detail.DiscountPercentage.Should().Be(40);
    }

    [Fact]
    public async Task A_product_without_a_real_saving_is_not_an_offer()
    {
        var plain = await AddProductAsync(60m, null);
        var notCheaper = await AddProductAsync(60m, 60m);

        await using var scope = _fixture.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStoreCatalogService>();

        var offers = await catalog.GetProductsAsync(new StoreProductQuery
        {
            OnOfferOnly = true,
            PageSize = 100
        });

        offers.Items.Should().NotContain(p => p.Id == plain.Id || p.Id == notCheaper.Id);
    }

    [Fact]
    public async Task The_home_page_lists_offers_with_the_biggest_saving_first()
    {
        await AddProductAsync(10m, 100m);   // 90% off
        await AddProductAsync(90m, 100m);   // 10% off

        await using var scope = _fixture.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IStoreCatalogService>();

        var home = await catalog.GetHomeAsync(null);

        home.Offers.Should().NotBeEmpty();
        home.Offers.Should().OnlyContain(p => p.IsOnOffer);
        home.Offers.Select(p => p.DiscountPercentage).Should().BeInDescendingOrder();
    }
}
