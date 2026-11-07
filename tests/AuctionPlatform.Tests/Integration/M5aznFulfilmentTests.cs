using System.Collections.Concurrent;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.M5azn;
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
/// Store orders handed to M5azn (store only: never auction prizes), against a real database and a stand-in for M5azn's API.
///
/// What matters most: a paid order is sent once and only once — money moves at M5azn on every
/// send — and what M5azn says afterwards is carried onto the order without ever moving it
/// backwards, refunding anything, or touching lines DealGo ships itself.
/// </summary>
[Collection("database")]
public class M5aznFulfilmentTests
{
    private const string M5aznSku = "M2-4-3-4-S";

    private readonly DatabaseFixture _fixture;

    public M5aznFulfilmentTests(DatabaseFixture fixture) => _fixture = fixture;

    // ---------------------------------------------------------------- store orders

    [Fact]
    public async Task A_paid_order_for_an_M5azn_product_is_sent_once_and_moves_to_processing()
    {
        var m5azn = new FakeM5azn();
        var (buyer, addressId) = await NewBuyerAsync();
        var product = await NewProductAsync(price: 75m, linked: true);
        var orderId = await CheckoutAsync(buyer, addressId, (product, 2));

        await RunAsync(m5azn);
        await RunAsync(m5azn);

        var shipment = await ShipmentForAsync(orderId);
        shipment.Status.Should().Be(M5aznShipmentStatus.Submitted);
        shipment.RemoteOrderId.Should().Be($"M-{shipment.Reference}");
        shipment.Attempts.Should().Be(1);

        var sent = m5azn.Created.Where(c => c.Id == shipment.Reference).ToList();
        sent.Should().ContainSingle("a second pass must never send it again");

        var body = sent[0];
        body.Total.Should().Be(150m);
        body.PaymentMethod.Should().Be("card", "the customer already paid DealGo");
        body.CodAmount.Should().Be(0m);
        body.PlatformName.Should().Be("DealGo");
        body.ShippingInfo.CountryCode.Should().Be("SA");
        body.ShippingInfo.Country.Should().Be("Saudi Arabia");
        body.ShippingInfo.City.Should().Be("الرياض");
        body.ShippingInfo.Address.Should().Contain("1234 طريق الملك فهد").And.Contain("العليا").And.Contain("RRRD2929");
        body.CustomerInfo.FirstName.Should().Be("أحمد");
        body.CustomerInfo.LastName.Should().Be("علي");
        body.CustomerInfo.Phone.Should().Be("+966551234567");
        body.CustomerInfo.Email.Should().EndWith("@test.local");
        body.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new M5aznOrderItem(1, RemoteIdOf(product), M5aznSku, "Test Item", 2));

        (await OrderAsync(orderId)).Status.Should().Be(StoreOrderStatus.Processing);
    }

    [Fact]
    public async Task Once_sent_the_customer_can_no_longer_cancel_but_sees_the_tracking()
    {
        var m5azn = new FakeM5azn();
        var (buyer, addressId) = await NewBuyerAsync();
        var (stranger, _) = await NewBuyerAsync();
        var product = await NewProductAsync(price: 40m, linked: true);
        var orderId = await CheckoutAsync(buyer, addressId, (product, 1));

        await RunAsync(m5azn);

        await using (var scope = _fixture.CreateScope(buyer))
        {
            var cancel = () => scope.ServiceProvider.GetRequiredService<IStoreOrderService>()
                .CancelOrderAsync(buyer, orderId, "changed my mind");
            (await cancel.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.OrderNotCancellable);
        }

        await using (var scope = _fixture.CreateScope(buyer, m5azn: m5azn))
        {
            var service = scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>();

            var mine = await service.GetStoreOrderTrackingAsync(buyer, orderId);
            mine.Should().NotBeNull();
            mine!.Provider.Should().Be("M5azn");
            mine.Status.Should().Be(M5aznShipmentStatus.Submitted);

            (await service.GetStoreOrderTrackingAsync(stranger, orderId)).Should().BeNull("it is not their order");
        }
    }

    [Fact]
    public async Task What_M5azn_says_is_carried_onto_the_order_forwards_only()
    {
        var m5azn = new FakeM5azn();
        var (buyer, addressId) = await NewBuyerAsync();
        var product = await NewProductAsync(price: 30m, linked: true);
        var orderId = await CheckoutAsync(buyer, addressId, (product, 1));

        await RunAsync(m5azn);
        var shipment = await ShipmentForAsync(orderId);

        m5azn.Remote[shipment.RemoteOrderId!] = new M5aznRemoteOrder(shipment.RemoteOrderId, "shipped", "AWB-555", "https://track.example/AWB-555", "SMSA");
        await RefreshAsync(m5azn, shipment.Id);

        var order = await OrderAsync(orderId);
        order.Status.Should().Be(StoreOrderStatus.Shipped);
        order.TrackingNumber.Should().Be("AWB-555");

        shipment = await ShipmentForAsync(orderId);
        shipment.Status.Should().Be(M5aznShipmentStatus.Shipped);
        shipment.Carrier.Should().Be("SMSA");
        shipment.TrackingUrl.Should().Be("https://track.example/AWB-555");

        // A stale answer must not move it back.
        m5azn.Remote[shipment.RemoteOrderId!] = new M5aznRemoteOrder(shipment.RemoteOrderId, "processing", null, null, null);
        await RefreshAsync(m5azn, shipment.Id);
        (await ShipmentForAsync(orderId)).Status.Should().Be(M5aznShipmentStatus.Shipped);

        m5azn.Remote[shipment.RemoteOrderId!] = new M5aznRemoteOrder(shipment.RemoteOrderId, "تم التوصيل", null, null, null);
        await RefreshAsync(m5azn, shipment.Id);

        (await OrderAsync(orderId)).Status.Should().Be(StoreOrderStatus.Delivered);
        (await ShipmentForAsync(orderId)).DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_mixed_order_sends_only_the_M5azn_lines_and_is_not_marked_shipped_for_the_rest()
    {
        var m5azn = new FakeM5azn();
        var (buyer, addressId) = await NewBuyerAsync();
        var theirs = await NewProductAsync(price: 20m, linked: true);
        var ours = await NewProductAsync(price: 10m, linked: false);
        var orderId = await CheckoutAsync(buyer, addressId, (theirs, 1), (ours, 3));

        await RunAsync(m5azn);
        var shipment = await ShipmentForAsync(orderId);

        var body = m5azn.Created.Single(c => c.Id == shipment.Reference);
        body.Items.Should().ContainSingle().Which.ProductId.Should().Be(RemoteIdOf(theirs));
        body.Total.Should().Be(20m, "only M5azn's line is M5azn's to deliver");

        m5azn.Remote[shipment.RemoteOrderId!] = new M5aznRemoteOrder(shipment.RemoteOrderId, "delivered", "AWB-1", null, null);
        await RefreshAsync(m5azn, shipment.Id);

        (await ShipmentForAsync(orderId)).Status.Should().Be(M5aznShipmentStatus.Delivered);
        (await OrderAsync(orderId)).Status.Should().Be(StoreOrderStatus.Processing,
            "DealGo still has its own lines to ship");
    }

    [Fact]
    public async Task An_unclear_answer_is_checked_by_reference_and_never_blindly_resent()
    {
        // First case: the order did reach M5azn even though the answer was lost.
        var reached = new FakeM5azn
        {
            OnCreate = (fake, order) =>
            {
                fake.Remote[order.Id.ToString()] = new M5aznRemoteOrder("R-77", "new", null, null, null);
                return new M5aznSubmitResult(M5aznSubmitOutcome.Unknown, Message: "timeout");
            }
        };

        var (buyer, addressId) = await NewBuyerAsync();
        var product = await NewProductAsync(price: 15m, linked: true);
        var firstOrder = await CheckoutAsync(buyer, addressId, (product, 1));

        await RunAsync(reached);
        var first = await ShipmentForAsync(firstOrder);
        first.Status.Should().BeOneOf(M5aznShipmentStatus.Submitted, M5aznShipmentStatus.Processing);
        first.RemoteOrderId.Should().Be("R-77");
        reached.Created.Count(c => c.Id == first.Reference).Should().Be(1);

        // Second case: it did not, and M5azn does not know it. It waits before trying again.
        var lost = new FakeM5azn
        {
            OnCreate = (_, _) => new M5aznSubmitResult(M5aznSubmitOutcome.Unknown, Message: "timeout")
        };
        var secondOrder = await CheckoutAsync(buyer, addressId, (product, 1));

        await RunAsync(lost);
        await RunAsync(lost);

        var second = await ShipmentForAsync(secondOrder);
        second.Status.Should().Be(M5aznShipmentStatus.Queued);
        second.Attempts.Should().Be(1);
        second.NextAttemptAt.Should().BeAfter(DateTime.UtcNow);
        second.LastError.Should().Contain("may have the order");
        lost.Created.Count(c => c.Id == second.Reference).Should().Be(1, "the retry waits for its turn");
    }

    [Fact]
    public async Task A_refused_order_fails_for_a_person_and_can_be_sent_again_by_hand()
    {
        var m5azn = new FakeM5azn
        {
            OnCreate = (_, _) => new M5aznSubmitResult(M5aznSubmitOutcome.Rejected, Message: "sku not found")
        };
        var (buyer, addressId) = await NewBuyerAsync();
        var product = await NewProductAsync(price: 12m, linked: true);
        var orderId = await CheckoutAsync(buyer, addressId, (product, 1));

        await RunAsync(m5azn);

        var shipment = await ShipmentForAsync(orderId);
        shipment.Status.Should().Be(M5aznShipmentStatus.Failed);
        shipment.LastError.Should().Contain("sku not found");
        (await OrderAsync(orderId)).Status.Should().Be(StoreOrderStatus.Paid, "nothing was sent");

        m5azn.OnCreate = null;   // fixed at M5azn
        await using (var scope = _fixture.CreateScope(m5azn: m5azn))
        {
            var dto = await scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>().SubmitNowAsync(shipment.Id);
            dto.Status.Should().Be(M5aznShipmentStatus.Submitted);
            dto.SourceNumber.Should().StartWith("ORD-");
        }

        (await OrderAsync(orderId)).Status.Should().Be(StoreOrderStatus.Processing);
    }

    [Fact]
    public async Task With_automatic_sending_off_orders_wait_for_approval()
    {
        var m5azn = new FakeM5azn();
        var (buyer, addressId) = await NewBuyerAsync();
        var product = await NewProductAsync(price: 25m, linked: true);
        var orderId = await CheckoutAsync(buyer, addressId, (product, 1));

        await RunAsync(m5azn, o => o.AutoSubmit = false);

        var shipment = await ShipmentForAsync(orderId);
        shipment.Status.Should().Be(M5aznShipmentStatus.AwaitingApproval);
        m5azn.Created.Should().NotContain(c => c.Id == shipment.Reference);

        await using (var scope = _fixture.CreateScope(buyer, m5azn: m5azn))
        {
            (await scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>()
                .GetStoreOrderTrackingAsync(buyer, orderId)).Should().BeNull("nothing has gone to M5azn yet");
        }

        await using (var scope = _fixture.CreateScope(m5azn: m5azn, configureM5azn: o => o.AutoSubmit = false))
        {
            (await scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>().SubmitNowAsync(shipment.Id))
                .Status.Should().Be(M5aznShipmentStatus.Submitted);
        }

        m5azn.Created.Should().ContainSingle(c => c.Id == shipment.Reference);
    }

    [Fact]
    public async Task An_order_cancelled_before_it_was_sent_is_never_sent()
    {
        var m5azn = new FakeM5azn();
        var (buyer, addressId) = await NewBuyerAsync();
        var product = await NewProductAsync(price: 33m, linked: true);
        var orderId = await CheckoutAsync(buyer, addressId, (product, 1));

        await RunAsync(m5azn, o => o.AutoSubmit = false);

        await using (var scope = _fixture.CreateScope(buyer))
        {
            await scope.ServiceProvider.GetRequiredService<IStoreOrderService>()
                .CancelOrderAsync(buyer, orderId, "changed my mind");
        }

        var shipment = await ShipmentForAsync(orderId);
        await using (var scope = _fixture.CreateScope(m5azn: m5azn))
        {
            (await scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>().SubmitNowAsync(shipment.Id))
                .Status.Should().Be(M5aznShipmentStatus.Cancelled);
        }

        m5azn.Created.Should().NotContain(c => c.Id == shipment.Reference);
    }

    [Fact]
    public async Task Switching_on_does_not_send_old_orders()
    {
        var m5azn = new FakeM5azn();
        var (buyer, addressId) = await NewBuyerAsync();
        var product = await NewProductAsync(price: 18m, linked: true);
        var orderId = await CheckoutAsync(buyer, addressId, (product, 1));

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var order = await db.StoreOrders.FirstAsync(o => o.Id == orderId);
            order.PaidAt = DateTime.UtcNow.AddDays(-10);
            await db.SaveChangesAsync();
        }

        await RunAsync(m5azn);

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.M5aznShipments.AnyAsync(s => s.SourceId == orderId)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Switched_off_it_does_nothing_and_calls_nobody()
    {
        var (buyer, addressId) = await NewBuyerAsync();
        var product = await NewProductAsync(price: 22m, linked: true);
        var orderId = await CheckoutAsync(buyer, addressId, (product, 1));

        await using var scope = _fixture.CreateScope();   // the disabled client throws if called
        var service = scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>();

        service.IsEnabled.Should().BeFalse();
        (await service.RunAsync()).Should().Be(new M5aznRunSummary(0, 0, 0));
        (await service.SyncStockAsync()).Should().Be(0);

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.M5aznShipments.AnyAsync(s => s.SourceId == orderId)).Should().BeFalse();
    }

    // ---------------------------------------------------------------- store only

    [Fact]
    public async Task Auction_prizes_are_never_sent_to_M5azn()
    {
        var m5azn = new FakeM5azn();
        var (winner, addressId) = await NewBuyerAsync();
        var claimId = await CreatePaidClaimAsync(winner);

        await using (var scope = _fixture.CreateScope(winner))
        {
            await scope.ServiceProvider.GetRequiredService<IPrizeClaimService>().ClaimAsync(winner, claimId, addressId);
        }

        // A linked store order in the same pass shows the worker did run.
        var product = await NewProductAsync(price: 31m, linked: true);
        var orderId = await CheckoutAsync(winner, addressId, (product, 1));

        await RunAsync(m5azn);

        (await ShipmentForAsync(orderId)).Status.Should().Be(M5aznShipmentStatus.Submitted);

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.M5aznShipments.AnyAsync(s => s.SourceId == claimId)).Should().BeFalse();
        }

        m5azn.Created.Should().NotContain(c => c.Total == 2480m);
    }

    // ---------------------------------------------------------------- settings and pricing

    [Fact]
    public async Task Dashboard_settings_win_and_the_password_is_kept_encrypted_and_never_returned()
    {
        try
        {
            await using (var scope = _fixture.CreateScope(m5azn: new FakeM5azn()))
            {
                var service = scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>();

                var before = await service.GetSettingsAsync();
                before.Source.Should().Be(M5aznSettingsSource.Server);
                before.ProfitPercentage.Should().Be(70m, "70% is the default profit");

                var saved = await service.UpdateSettingsAsync(new M5aznSettingsRequest(
                    true, " owner@dealgo.test ", "dash-pass", "https://m5azn.ae", false, 70m));
                saved.Settings.Should().BeEquivalentTo(new
                {
                    Enabled = true, Email = "owner@dealgo.test", HasPassword = true, PasswordUnreadable = false,
                    BaseUrl = "https://m5azn.ae", AutoSubmit = false, Source = M5aznSettingsSource.Dashboard, Active = true
                });

                var account = scope.ServiceProvider.GetRequiredService<IM5aznAccountProvider>().Current;
                account.Password.Should().Be("dash-pass");
                account.ToString().Should().NotContain("dash-pass");

                // An empty password keeps the saved one.
                await service.UpdateSettingsAsync(new M5aznSettingsRequest(true, "owner@dealgo.test", "", "https://m5azn.ae", true, 70m));
                scope.ServiceProvider.GetRequiredService<IM5aznAccountProvider>().Current.Password.Should().Be("dash-pass");

                // A different account needs its own password.
                var noPassword = () => service.UpdateSettingsAsync(
                    new M5aznSettingsRequest(true, "other@dealgo.test", null, null, true, 70m));
                (await noPassword.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);

                var badProfit = () => service.UpdateSettingsAsync(
                    new M5aznSettingsRequest(true, "owner@dealgo.test", null, null, true, -1m));
                (await badProfit.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);

                var plainHttp = () => service.UpdateSettingsAsync(
                    new M5aznSettingsRequest(true, "owner@dealgo.test", null, "http://m5azn.com", true, 70m));
                (await plainHttp.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);

                (await service.CheckSignInAsync()).Ok.Should().BeTrue();
            }

            await using (var scope = _fixture.CreateScope())
            {
                var row = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                    .M5aznSettings.AsNoTracking().SingleAsync();
                row.PasswordProtected.Should().StartWith("v1:").And.NotContain("dash-pass");

                // The dashboard's switch and account win over the server's (which is off and empty here).
                var account = await scope.ServiceProvider.GetRequiredService<IM5aznAccountProvider>().RefreshAsync(force: true);
                account.Source.Should().Be(M5aznSettingsSource.Dashboard);
                account.IsActive.Should().BeTrue();
            }
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    // ---------------------------------------------------------------- importing products

    [Fact]
    public async Task Importing_creates_products_with_every_picture_category_price_and_stock_and_never_duplicates()
    {
        var full = NextRemoteId();
        var thin = NextRemoteId();
        var category = $"عناية بالبشرة {Guid.NewGuid():N}"[..24];
        var m5azn = new FakeM5azn();
        m5azn.ListPage(
            Catalog(full, $"8809562192770-{full}", "سيروم فيتامين سي", 35.15m, 7, ["https://cdn.m5azn.test/a.jpg", "https://cdn.m5azn.test/b.jpg", "https://cdn.m5azn.test/c.jpg"], category, "Brightening serum."),
            Catalog(thin, $"THIN-{thin}", "Thin item", 10m, 3, ["https://cdn.m5azn.test/x.jpg"], null, null));
        m5azn.Details[thin] = Catalog(thin, null, null, null, null, ["https://cdn.m5azn.test/x.jpg", "https://cdn.m5azn.test/y.jpg"], null, "From the details.");

        var adminProduct = await NewProductAsync(price: 12m, linked: false);

        try
        {
            var first = await ImportAsync(m5azn);
            first.Created.Should().BeGreaterThanOrEqualTo(2);
            first.Problems.Should().BeEmpty();

            var a = await ImportedAsync(full);
            a.Source.Should().Be(StoreProductSource.M5azn);
            a.Name.Should().Be("سيروم فيتامين سي");
            a.Description.Should().Be("Brightening serum.");
            a.M5aznSku.Should().Be($"8809562192770-{full}");
            a.Sku.Should().Be($"8809562192770-{full}", "M5azn's own SKU when nobody else uses it");
            a.M5aznCostPrice.Should().Be(35.15m);
            a.Price.Should().Be(59.76m, "35.15 + 70%, rounded");
            a.StockQuantity.Should().Be(7);
            a.Status.Should().Be(StoreProductStatus.Active);
            a.DeliveryTime.Should().Be("1-5 أيام");
            a.Category!.Name.Should().Be(category);
            a.Images.OrderBy(x => x.DisplayOrder).Select(x => x.Url).Should().Equal(
                "https://cdn.m5azn.test/a.jpg", "https://cdn.m5azn.test/b.jpg", "https://cdn.m5azn.test/c.jpg");
            a.Images.Single(x => x.IsPrimary).Url.Should().Be("https://cdn.m5azn.test/a.jpg");

            var b = await ImportedAsync(thin);
            b.Description.Should().Be("From the details.", "the details fill in what the list leaves out");
            b.Images.Select(x => x.Url).Should().BeEquivalentTo("https://cdn.m5azn.test/x.jpg", "https://cdn.m5azn.test/y.jpg");

            // Again: M5azn's price and stock changed, and the thin one is gone from the list.
            m5azn.Pages.Clear();
            m5azn.ListPage(Catalog(full, $"8809562192770-{full}", "سيروم فيتامين سي", 40m, 0,
                ["https://cdn.m5azn.test/a.jpg", "https://cdn.m5azn.test/b.jpg", "https://cdn.m5azn.test/c.jpg"], category, "Brightening serum."));
            var second = await ImportAsync(m5azn);
            second.Hidden.Should().BeGreaterThanOrEqualTo(1);

            a = await ImportedAsync(full);
            a.Price.Should().Be(68m);
            a.StockQuantity.Should().Be(0);
            a.Status.Should().Be(StoreProductStatus.OutOfStock);

            b = await ImportedAsync(thin);
            b.Status.Should().Be(StoreProductStatus.Hidden, "a product M5azn no longer lists is hidden, never deleted");
            b.M5aznRemovedAt.Should().NotBeNull();

            // Back in the list: shown again.
            m5azn.ListPage(Catalog(thin, $"THIN-{thin}", "Thin item", 10m, 3, ["https://cdn.m5azn.test/x.jpg", "https://cdn.m5azn.test/y.jpg"], null, "From the details."));
            await ImportAsync(m5azn);
            (await ImportedAsync(thin)).Status.Should().Be(StoreProductStatus.Active);

            await using (var scope = _fixture.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                (await db.StoreProducts.CountAsync(x => x.M5aznProductId == full)).Should().Be(1, "importing again never duplicates");

                var admin = await db.StoreProducts.AsNoTracking().FirstAsync(x => x.Id == adminProduct);
                admin.Source.Should().Be(StoreProductSource.Admin);
                admin.Price.Should().Be(12m);
                admin.Status.Should().NotBe(StoreProductStatus.Hidden);
            }

            await using (var scope = _fixture.CreateScope(m5azn: m5azn))
            {
                var listed = await scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>()
                    .GetImportedProductsAsync(new M5aznProductQuery { PageSize = 100, Search = full.ToString() });
                listed.Items.Should().ContainSingle(x => x.M5aznProductId == full);

                var all = await scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>()
                    .GetImportedProductsAsync(new M5aznProductQuery { PageSize = 100 });
                all.Items.Should().NotContain(x => x.Id == adminProduct, "the M5azn page lists imported products only");
            }
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task An_answer_that_cannot_be_read_changes_nothing()
    {
        var id = NextRemoteId();
        var m5azn = new FakeM5azn();
        m5azn.ListPage(Catalog(id, $"KEEP-{id}", "Kept", 10m, 5, ["https://cdn.m5azn.test/k.jpg"], null, "x"));

        try
        {
            await ImportAsync(m5azn);

            m5azn.Pages.Clear();
            m5azn.Pages.Add(new M5aznProductPage([], false, "M5azn's product list came in a shape DealGo does not know yet: {}", Unreadable: true));
            var summary = await ImportAsync(m5azn);

            summary.Problems.Should().Contain(x => x.Contains("shape DealGo does not know"));
            summary.Hidden.Should().Be(0);
            (await ImportedAsync(id)).Status.Should().Be(StoreProductStatus.Active, "an unreadable answer is not an empty shop");
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task A_product_without_a_sku_or_a_price_is_skipped_and_said_so()
    {
        var noSku = NextRemoteId();
        var noPrice = NextRemoteId();
        var m5azn = new FakeM5azn();
        m5azn.ListPage(
            Catalog(noSku, null, "No SKU", 10m, 1, [], null, "x"),
            Catalog(noPrice, $"NP-{noPrice}", "No price", null, 1, [], null, "x"));
        m5azn.Details[noSku] = Catalog(noSku, null, null, null, null, [], null, null);

        try
        {
            var summary = await ImportAsync(m5azn);
            summary.Skipped.Should().Be(2);
            summary.Problems.Should().Contain(x => x.Contains($"{noSku}") && x.Contains("no SKU"));
            summary.Problems.Should().Contain(x => x.Contains($"{noPrice}") && x.Contains("no price"));

            await using var scope = _fixture.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().StoreProducts
                .AnyAsync(x => x.M5aznProductId == noSku || x.M5aznProductId == noPrice)).Should().BeFalse();
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task A_product_added_by_staff_never_goes_to_M5azn_even_with_an_old_link()
    {
        var m5azn = new FakeM5azn();
        var (buyer, addressId) = await NewBuyerAsync();
        var adminProduct = await NewProductAsync(price: 15m, linked: false);

        await using (var scope = _fixture.CreateScope())
        {
            // A link made by hand before products were imported.
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var p = await db.StoreProducts.FirstAsync(x => x.Id == adminProduct);
            p.M5aznProductId = NextRemoteId();
            p.M5aznSku = "OLD-LINK";
            await db.SaveChangesAsync();
        }

        var orderId = await CheckoutAsync(buyer, addressId, (adminProduct, 1));
        await RunAsync(m5azn);

        await using (var scope = _fixture.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().M5aznShipments
                .AnyAsync(x => x.SourceId == orderId)).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Changing_the_profit_reprices_imported_products_without_asking_M5azn()
    {
        var id = NextRemoteId();
        var m5azn = new FakeM5azn();
        m5azn.ListPage(Catalog(id, $"PROFIT-{id}", "Profit item", 40m, 5, ["https://cdn.m5azn.test/p.jpg", "https://cdn.m5azn.test/q.jpg"], null, "x"));

        try
        {
            await ImportAsync(m5azn);
            (await ImportedAsync(id)).Price.Should().Be(68m);   // 40 + 70%

            m5azn.Pages.Clear();   // M5azn is not asked: a call would find nothing
            await using (var scope = _fixture.CreateScope(m5azn: m5azn))
            {
                var saved = await scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>()
                    .UpdateSettingsAsync(new M5aznSettingsRequest(true, null, null, null, true, 25m, "2-4 أيام"));
                saved.Repriced.Should().BeGreaterThanOrEqualTo(1);
                saved.Settings.DeliveryTime.Should().Be("2-4 أيام");
            }

            (await ImportedAsync(id)).Price.Should().Be(50m);   // 40 + 25%
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Theory]
    [InlineData(100, 70, 170)]
    [InlineData(19.99, 70, 33.98)]
    [InlineData(50, 0, 50)]
    [InlineData(10, 12.5, 11.25)]
    public void The_price_is_the_M5azn_price_plus_the_profit(decimal m5azn, decimal profit, decimal expected) =>
        M5aznPricing.PriceFor(m5azn, profit).Should().Be(expected);

    private async Task<decimal> PriceOfAsync(Guid productId)
    {
        await using var scope = _fixture.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .StoreProducts.AsNoTracking().FirstAsync(p => p.Id == productId)).Price;
    }

    /// <summary>The settings row would change every later test's M5azn, so each test that saves one removes it.</summary>
    private async Task ClearSettingsAsync()
    {
        await using var scope = _fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().M5aznSettings.ExecuteDeleteAsync();
    }

    // ---------------------------------------------------------------- stock

    [Fact]
    public async Task Stock_follows_M5azn_for_linked_products_only()
    {
        var linkedId = NextRemoteId();
        var linked = await NewProductAsync(price: 10m, linked: true, stock: 10, m5aznId: linkedId);
        var other = await NewProductAsync(price: 10m, linked: false, stock: 10);

        var m5azn = new FakeM5azn();
        m5azn.StockChanges.Add(new M5aznStockChange(linkedId, M5aznSku, 3));
        m5azn.StockChanges.Add(new M5aznStockChange(linkedId, "SOME-OTHER-SIZE", 99));

        await using (var scope = _fixture.CreateScope(m5azn: m5azn))
        {
            (await scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>().SyncStockAsync()).Should().Be(1);
        }

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Id == linked)).StockQuantity.Should().Be(3, "matched by SKU");
            (await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Id == other)).StockQuantity.Should().Be(10);
            (await db.InventoryTransactions.AnyAsync(t => t.ProductId == linked && t.ReferenceType == "M5azn" && t.Quantity == -7))
                .Should().BeTrue();
        }

        m5azn.StockChanges.Clear();
        m5azn.StockChanges.Add(new M5aznStockChange(linkedId, M5aznSku, 0));
        await using (var scope = _fixture.CreateScope(m5azn: m5azn))
        {
            await scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>().SyncStockAsync();
        }

        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await db.StoreProducts.AsNoTracking().FirstAsync(p => p.Id == linked);
            product.StockQuantity.Should().Be(0);
            product.Status.Should().Be(StoreProductStatus.OutOfStock);
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task RunAsync(FakeM5azn m5azn, Action<M5aznOptions>? configure = null)
    {
        await using var scope = _fixture.CreateScope(m5azn: m5azn, configureM5azn: configure);
        await scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>().RunAsync();
    }

    private async Task RefreshAsync(FakeM5azn m5azn, Guid shipmentId)
    {
        await using var scope = _fixture.CreateScope(m5azn: m5azn);
        await scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>().RefreshAsync(shipmentId);
    }

    private async Task<(Guid UserId, Guid AddressId)> NewBuyerAsync()
    {
        Guid userId;
        await using (var scope = _fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            userId = (await TestData.CreateUserAsync(db, "Customer", mainBalance: 10_000m, shoppingBalance: 10_000m)).Id;
        }

        await using (var scope = _fixture.CreateScope(userId))
        {
            var address = await scope.ServiceProvider.GetRequiredService<IShippingAddressService>().CreateAsync(userId,
                new SaveShippingAddressRequest("أحمد علي", "0551234567", "الرياض", "العليا", "طريق الملك فهد", "1234",
                    "بجانب المسجد", "RRRD2929", null, null, true));
            return (userId, address.Id);
        }
    }

    private static long _remoteIds = 5_000_000_000L + Random.Shared.Next(1_000_000) * 1000L;
    private static readonly ConcurrentDictionary<Guid, long> RemoteIds = new();

    /// <summary>An M5azn product id no other test has used: imported products are unique by it.</summary>
    private static long NextRemoteId() => Interlocked.Increment(ref _remoteIds);

    private static long RemoteIdOf(Guid productId) => RemoteIds[productId];

    /// <summary>A store product; <paramref name="linked"/>: one imported from M5azn.</summary>
    private async Task<Guid> NewProductAsync(decimal price, bool linked, int stock = 50, long? m5aznId = null)
    {
        await using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var product = await TestData.CreateStoreProductAsync(db, price, stock);
        product.RequiresShipping = true;
        if (linked)
        {
            product.Source = StoreProductSource.M5azn;
            product.M5aznProductId = m5aznId ?? NextRemoteId();
            product.M5aznSku = M5aznSku;
            RemoteIds[product.Id] = product.M5aznProductId.Value;
        }
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static M5aznCatalogProduct Catalog(
        long id, string? sku, string? name, decimal? price, int? quantity, string[] images, string? category, string? description) =>
        new(id, sku, name, description, price, quantity, images, category, null);

    private async Task<M5aznImportSummary> ImportAsync(FakeM5azn m5azn)
    {
        await using var scope = _fixture.CreateScope(m5azn: m5azn);
        return await scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>().ImportAsync();
    }

    private async Task<StoreProduct> ImportedAsync(long m5aznProductId)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().StoreProducts.AsNoTracking()
            .Include(x => x.Images).Include(x => x.Category)
            .SingleAsync(x => x.Source == StoreProductSource.M5azn && x.M5aznProductId == m5aznProductId);
    }

    private async Task<Guid> CheckoutAsync(Guid buyer, Guid addressId, params (Guid Product, int Quantity)[] lines)
    {
        await using var scope = _fixture.CreateScope(buyer);
        var order = await scope.ServiceProvider.GetRequiredService<IStoreOrderService>().CheckoutAsync(buyer,
            new CheckoutRequest(lines.Select(l => new CheckoutItemRequest(l.Product, l.Quantity)).ToList(),
                StorePaymentMethod.ShoppingWallet, addressId, $"m5azn-{Guid.NewGuid():N}"));
        return order.Id;
    }

    private async Task<StoreOrder> OrderAsync(Guid orderId)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .StoreOrders.AsNoTracking().FirstAsync(o => o.Id == orderId);
    }

    private async Task<M5aznShipment> ShipmentForAsync(Guid orderId)
    {
        await using var scope = _fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .M5aznShipments.AsNoTracking().SingleAsync(s => s.SourceType == M5aznSourceType.StoreOrder && s.SourceId == orderId);
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

    /// <summary>M5azn as far as these tests need: it remembers what was sent and answers what it is told to.</summary>
    private sealed class FakeM5azn : IM5aznClient
    {
        public ConcurrentBag<M5aznOrderRequest> Created { get; } = new();
        public ConcurrentDictionary<string, M5aznRemoteOrder> Remote { get; } = new();
        public List<M5aznStockChange> StockChanges { get; } = new();

        /// <summary>Null: accept, and remember the order (with no status yet) under M-{reference}.</summary>
        public Func<FakeM5azn, M5aznOrderRequest, M5aznSubmitResult>? OnCreate { get; set; }

        public bool IsEnabled => true;

        public Task<M5aznSubmitResult> CreateOrderAsync(M5aznOrderRequest order, CancellationToken ct = default)
        {
            Created.Add(order);
            if (OnCreate is { } custom) return Task.FromResult(custom(this, order));

            var remoteId = $"M-{order.Id}";
            Remote[remoteId] = new M5aznRemoteOrder(remoteId, null, null, null, null);
            return Task.FromResult(new M5aznSubmitResult(M5aznSubmitOutcome.Accepted, remoteId, null));
        }

        public Task<M5aznRemoteOrder?> GetOrderAsync(string orderId, CancellationToken ct = default) =>
            Task.FromResult(Remote.TryGetValue(orderId, out var o) ? o : null);

        public Task<IReadOnlyList<M5aznStockChange>> GetQuantityChangesAsync(int hours, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<M5aznStockChange>>(StockChanges.ToList());

        /// <summary>The seller's list, page by page; empty means an empty shop.</summary>
        public List<M5aznProductPage> Pages { get; } = new();

        public ConcurrentDictionary<long, M5aznCatalogProduct> Details { get; } = new();

        public void ListPage(params M5aznCatalogProduct[] products) => Pages.Add(new M5aznProductPage(products, false, null));

        public Task<M5aznProductPage> GetMyProductsAsync(int page, CancellationToken ct = default) =>
            Task.FromResult(page <= Pages.Count
                ? Pages[page - 1] with { HasMore = page < Pages.Count }
                : new M5aznProductPage([], false, null));

        public Task<M5aznCatalogProduct?> GetProductAsync(long productId, CancellationToken ct = default) =>
            Task.FromResult(Details.TryGetValue(productId, out var d) ? d : null);

        public Task<string?> CheckSignInAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
