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
/// The two kinds of shipping: the store's own products at their fee or the default one, and
/// M5azn's once per order, at M5azn's charge less what DealGo covers, plus tax on the rest.
/// </summary>
[Collection("database")]
public class StoreShippingTests
{
    private readonly DatabaseFixture _fixture;

    public StoreShippingTests(DatabaseFixture fixture) => _fixture = fixture;

    /// <summary>Sets the shipping settings for one test and puts the old ones back afterwards.</summary>
    private async Task<IAsyncDisposable> ShippingAsync(decimal defaultFee, decimal m5aznCost, decimal covered, decimal taxPercent)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = await db.StoreSettings.FirstOrDefaultAsync();
        if (settings is null)
        {
            settings = new StoreSettings();
            db.StoreSettings.Add(settings);
        }

        var before = (settings.ShippingEnabled, settings.DefaultShippingFee, settings.TaxEnabled,
            settings.M5aznShippingCost, settings.M5aznShippingCovered, settings.M5aznShippingTaxPercentage);

        settings.ShippingEnabled = true;
        settings.DefaultShippingFee = defaultFee;
        settings.TaxEnabled = false;
        settings.M5aznShippingCost = m5aznCost;
        settings.M5aznShippingCovered = covered;
        settings.M5aznShippingTaxPercentage = taxPercent;
        await db.SaveChangesAsync();

        return new Restore(_fixture, before);
    }

    private sealed class Restore(DatabaseFixture fixture,
        (bool, decimal, bool, decimal, decimal, decimal) before) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var scope = fixture.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var s = await db.StoreSettings.FirstAsync();
            (s.ShippingEnabled, s.DefaultShippingFee, s.TaxEnabled,
                s.M5aznShippingCost, s.M5aznShippingCovered, s.M5aznShippingTaxPercentage) = before;
            await db.SaveChangesAsync();
        }
    }

    private async Task<(Guid UserId, Guid AddressId, Guid Own, Guid M5azn)> CustomerAsync()
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await TestData.CreateUserAsync(db, "Buyer", mainBalance: 1000m);
        var address = new ShippingAddress
        {
            UserId = user.Id, FullName = "Buyer", Phone = "+966551234567", Country = "SA",
            City = "الرياض", Street = "", ShortAddress = "RAGI1234", IsDefault = true
        };
        db.ShippingAddresses.Add(address);

        var own = await TestData.CreateStoreProductAsync(db, price: 100m, stock: 10);
        own.RequiresShipping = true;
        var m5azn = await TestData.CreateStoreProductAsync(db, price: 100m, stock: 10);
        m5azn.RequiresShipping = true;
        m5azn.Source = StoreProductSource.M5azn;
        await db.SaveChangesAsync();
        return (user.Id, address.Id, own.Id, m5azn.Id);
    }

    private async Task<StoreOrderDto> BuyAsync(Guid userId, Guid addressId, params (Guid Id, int Qty)[] items)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IStoreOrderService>().CheckoutAsync(userId,
            new CheckoutRequest(items.Select(i => new CheckoutItemRequest(i.Id, i.Qty)).ToList(),
                StorePaymentMethod.MainWallet, addressId, $"ship-{Guid.NewGuid():N}"));
    }

    [Fact]
    public async Task M5azn_shipping_is_its_cost_less_what_DealGo_covers_plus_tax_once_per_order()
    {
        // M5azn charges 30; DealGo covers 10; 15% tax on the customer's 20 is 3.
        await using var _ = await ShippingAsync(defaultFee: 12m, m5aznCost: 30m, covered: 10m, taxPercent: 15m);
        var c = await CustomerAsync();

        var order = await BuyAsync(c.UserId, c.AddressId, (c.M5azn, 2));

        order.Subtotal.Should().Be(200m);
        order.ShippingFee.Should().Be(20m, "once per order, however many M5azn items");
        order.Tax.Should().Be(3m);
        order.Total.Should().Be(223m);
    }

    [Fact]
    public async Task A_mixed_order_pays_each_kind_of_shipping_once()
    {
        await using var _ = await ShippingAsync(defaultFee: 12m, m5aznCost: 30m, covered: 10m, taxPercent: 15m);
        var c = await CustomerAsync();

        var order = await BuyAsync(c.UserId, c.AddressId, (c.Own, 1), (c.M5azn, 1));

        order.ShippingFee.Should().Be(12m + 20m, "the store's own fee for its product, and M5azn's for M5azn's");
        order.Tax.Should().Be(3m);
        order.Total.Should().Be(200m + 32m + 3m);
    }

    [Fact]
    public async Task A_product_with_its_own_delivery_charge_uses_it_and_one_without_uses_the_default()
    {
        await using var _ = await ShippingAsync(defaultFee: 12m, m5aznCost: 0m, covered: 0m, taxPercent: 0m);
        var c = await CustomerAsync();

        Guid special;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await TestData.CreateStoreProductAsync(db, price: 50m, stock: 10);
            product.RequiresShipping = true;
            product.ShippingFee = 18m;
            await db.SaveChangesAsync();
            special = product.Id;
        }

        var order = await BuyAsync(c.UserId, c.AddressId, (special, 1), (c.Own, 1));

        order.ShippingFee.Should().Be(18m + 12m, "its own 18, and the default 12 for the product without one");
    }

    [Fact]
    public async Task An_M5azn_products_own_charge_is_added_on_top_and_taxed_like_M5azn_shipping()
    {
        // Order's M5azn shipping: 30 - 10 = 20, tax 3. The product's own 8 adds 8 and tax 1.20.
        await using var _ = await ShippingAsync(defaultFee: 12m, m5aznCost: 30m, covered: 10m, taxPercent: 15m);
        var c = await CustomerAsync();

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await db.StoreProducts.SingleAsync(p => p.Id == c.M5azn);
            product.ShippingFee = 8m;
            await db.SaveChangesAsync();
        }

        var order = await BuyAsync(c.UserId, c.AddressId, (c.M5azn, 1));

        order.ShippingFee.Should().Be(28m);
        order.Tax.Should().Be(4.20m);
        order.Total.Should().Be(100m + 28m + 4.20m);
    }

    [Fact]
    public async Task A_negative_delivery_charge_is_refused()
    {
        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IStoreAdminService>();

        var act = () => admin.CreateProductAsync(new CreateStoreProductRequest(
            "منتج", null, null, null, $"NEG-{Guid.NewGuid():N}"[..20], 50m, null, null, 5,
            StoreProductStatus.Active, StorePaymentMode.Both, false, true, -5m, null, null));

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Covering_more_than_M5azns_cost_is_refused()
    {
        await using var scope = _fixture.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IStoreAdminService>();
        var current = await admin.GetSettingsAsync();

        var act = () => admin.UpdateSettingsAsync(current with { M5aznShippingCost = 20m, M5aznShippingCovered = 25m });

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);
    }
}
