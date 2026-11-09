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
    public async Task Each_product_gets_its_M5azn_category_and_the_store_gets_the_same_categories()
    {
        var serum = NextRemoteId();
        var camera = NextRemoteId();
        var byId = NextRemoteId();
        var tag = Guid.NewGuid().ToString("N")[..6];
        long care = NextRemoteId(), skin = NextRemoteId(), cameras = NextRemoteId();

        var m5azn = new FakeM5azn();
        m5azn.Categories.AddRange([
            new M5aznCategory(care, $"العناية {tag}", null, "https://cdn.m5azn.test/cat/care.png"),
            new M5aznCategory(skin, $"العناية بالبشرة {tag}", care, null),
            new M5aznCategory(cameras, $"الكاميرات {tag}", null, "https://cdn.m5azn.test/cat/cam.png")
        ]);
        m5azn.InCategory[care] = [serum];
        m5azn.InCategory[skin] = [serum];
        m5azn.InCategory[cameras] = [camera];
        m5azn.ListPage(
            Catalog(serum, $"S-{serum}", "Serum", 35m, 3, ["https://cdn.m5azn.test/s1.jpg", "https://cdn.m5azn.test/s2.jpg"], null, "x"),
            Catalog(camera, $"C-{camera}", "Camera", 200m, 1, ["https://cdn.m5azn.test/c1.jpg", "https://cdn.m5azn.test/c2.jpg"], null, "x"),
            Catalog(byId, $"I-{byId}", "Known by id", 10m, 1, ["https://cdn.m5azn.test/i1.jpg", "https://cdn.m5azn.test/i2.jpg"], null, "x") with { CategoryId = cameras });

        try
        {
            var summary = await ImportAsync(m5azn);
            summary.Problems.Should().BeEmpty();

            var s = await ImportedAsync(serum);
            s.Category!.Name.Should().Be($"العناية بالبشرة {tag}", "the most specific category wins");

            await using (var scope = _fixture.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var sub = await db.StoreCategories.AsNoTracking().Include(c => c.Parent).FirstAsync(c => c.Id == s.CategoryId);
                sub.Parent!.Name.Should().Be($"العناية {tag}", "M5azn's main category becomes the main department");
                sub.Parent.ImageUrl.Should().Be("https://cdn.m5azn.test/cat/care.png");
            }

            (await ImportedAsync(camera)).Category!.Name.Should().Be($"الكاميرات {tag}");
            (await ImportedAsync(camera)).Category!.ImageUrl.Should().Be("https://cdn.m5azn.test/cat/cam.png");
            (await ImportedAsync(byId)).Category!.Name.Should().Be($"الكاميرات {tag}", "a category id is looked up by name");
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task The_details_or_a_sub_category_place_products_their_main_category_does_not_list()
    {
        var fromDetails = NextRemoteId();
        var onlyInSub = NextRemoteId();
        var lost = NextRemoteId();
        var tag = Guid.NewGuid().ToString("N")[..6];
        long watches = NextRemoteId(), men = NextRemoteId(), phones = NextRemoteId();

        var m5azn = new FakeM5azn();
        m5azn.Categories.AddRange([
            new M5aznCategory(watches, $"الساعات {tag}", null, null),
            new M5aznCategory(men, $"ساعات رجالية {tag}", watches, null),
            new M5aznCategory(phones, $"الجوال {tag}", null, null)
        ]);
        // The main category lists only its own products, not its sub-categories'.
        m5azn.InCategory[men] = [onlyInSub];
        m5azn.ListPage(
            Catalog(fromDetails, $"D-{fromDetails}", "From details", 10m, 1, ["https://cdn.m5azn.test/d1.jpg", "https://cdn.m5azn.test/d2.jpg"], null, "x"),
            Catalog(onlyInSub, $"U-{onlyInSub}", "Only in sub", 10m, 1, ["https://cdn.m5azn.test/u1.jpg", "https://cdn.m5azn.test/u2.jpg"], null, "x"),
            Catalog(lost, $"L-{lost}", "Nowhere", 10m, 1, ["https://cdn.m5azn.test/l1.jpg", "https://cdn.m5azn.test/l2.jpg"], null, "x"));
        m5azn.Details[fromDetails] = Catalog(fromDetails, null, null, null, null, [], null, null) with { CategoryId = phones };
        m5azn.Details[lost] = Catalog(lost, null, null, null, null, [], null, null) with { Shape = "id, name_ar, sku, brand{id, name}" };

        try
        {
            var summary = await ImportAsync(m5azn);

            (await ImportedAsync(fromDetails)).Category!.Name.Should().Be($"الجوال {tag}");
            (await ImportedAsync(onlyInSub)).Category!.Name.Should().Be($"ساعات رجالية {tag}");
            (await ImportedAsync(lost)).CategoryId.Should().BeNull();
            summary.Problems.Should().Contain(p => p.Contains("1 product(s) were found in no M5azn category")
                                                   && p.Contains("id, name_ar, sku, brand{id, name}"));
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task The_category_id_in_the_list_places_products_without_searching_any_category()
    {
        var serum = NextRemoteId();
        var shoe = NextRemoteId();
        var tag = Guid.NewGuid().ToString("N")[..6];
        long care = NextRemoteId(), skin = NextRemoteId(), shoes = NextRemoteId(), women = NextRemoteId();

        var m5azn = new FakeM5azn();
        m5azn.Categories.AddRange([
            new M5aznCategory(care, $"العناية {tag}", null, null),
            new M5aznCategory(skin, $"العناية بالبشرة والوجه والجسم {tag}", care, null),
            new M5aznCategory(shoes, $"الأحذية {tag}", null, null),
            new M5aznCategory(women, $"الأحذية النسائية {tag}", shoes, null)
        ]);
        // As M5azn's real list: one picture, the category id, no category name.
        m5azn.ListPage(
            Catalog(serum, $"S-{serum}", "Serum", 35.15m, 29, ["https://cdn.m5azn.test/s.jpg"], null, "x") with { CategoryId = skin },
            Catalog(shoe, $"W-{shoe}", "Shoe", 30m, 0, ["https://cdn.m5azn.test/w.jpg"], null, "x") with { CategoryId = women });
        m5azn.Details[serum] = Catalog(serum, null, null, null, null, ["https://cdn.m5azn.test/s.jpg", "https://cdn.m5azn.test/s2.jpg"], null, "Serum details.");
        m5azn.Details[shoe] = Catalog(shoe, null, null, null, null, ["https://cdn.m5azn.test/w.jpg", "https://cdn.m5azn.test/w2.jpg"], null, "Shoe details.");

        try
        {
            var first = await ImportAsync(m5azn);
            first.Problems.Should().BeEmpty();
            m5azn.CategoryFilterCalls.Should().Be(0, "the list's category_id is enough: no category is searched");

            var s = await ImportedAsync(serum);
            s.Category!.Name.Should().Be($"العناية بالبشرة والوجه والجسم {tag}", "the most specific category");
            s.Images.Should().HaveCount(2, "the details' pictures are used for a new product");
            (await ImportedAsync(shoe)).Category!.Name.Should().Be($"الأحذية النسائية {tag}");

            await using (var scope = _fixture.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var sub = await db.StoreCategories.AsNoTracking().Include(c => c.Parent).FirstAsync(c => c.Id == s.CategoryId);
                sub.Parent!.Name.Should().Be($"العناية {tag}");
            }

            // The hourly sync of known, complete products: no details and no category request.
            m5azn.DetailCalls = 0;
            var second = await ImportAsync(m5azn);
            second.Problems.Should().BeEmpty();
            m5azn.DetailCalls.Should().Be(0);
            m5azn.CategoryFilterCalls.Should().Be(0);
            (await ImportedAsync(serum)).Images.Should().HaveCount(2, "the list's single picture never shrinks the gallery");
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task A_category_staff_chose_is_never_overwritten_by_an_import()
    {
        var kept = NextRemoteId();
        var newcomer = NextRemoteId();
        var tag = Guid.NewGuid().ToString("N")[..6];
        long devices = NextRemoteId(), toys = NextRemoteId();

        var m5azn = new FakeM5azn();
        m5azn.Categories.AddRange([
            new M5aznCategory(devices, $"الأجهزة {tag}", null, null),
            new M5aznCategory(toys, $"الألعاب {tag}", null, null)
        ]);
        m5azn.ListPage(Catalog(kept, $"K-{kept}", "Kept", 4.3m, 6, ["https://cdn.m5azn.test/k.jpg", "https://cdn.m5azn.test/k2.jpg"], null, "x") with { CategoryId = devices });

        try
        {
            await ImportAsync(m5azn);

            Guid chosen;
            await using (var scope = _fixture.CreateScope())
            {
                // Staff move it to a category of their own.
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var category = new StoreCategory { Name = $"اختيار الإدارة {tag}", Slug = $"staff-choice-{tag}" };
                db.StoreCategories.Add(category);
                var product = await db.StoreProducts.FirstAsync(x => x.Source == StoreProductSource.M5azn && x.M5aznProductId == kept);
                product.CategoryId = category.Id;
                await db.SaveChangesAsync();
                chosen = category.Id;
            }

            // A new product makes the import look categories up again; the moved one keeps its own.
            m5azn.Pages.Clear();
            m5azn.ListPage(
                Catalog(kept, $"K-{kept}", "Kept", 4.3m, 6, ["https://cdn.m5azn.test/k.jpg", "https://cdn.m5azn.test/k2.jpg"], null, "x") with { CategoryId = devices },
                Catalog(newcomer, $"N-{newcomer}", "New", 9m, 1, ["https://cdn.m5azn.test/n.jpg", "https://cdn.m5azn.test/n2.jpg"], null, "x") with { CategoryId = toys });
            await ImportAsync(m5azn);

            (await ImportedAsync(kept)).CategoryId.Should().Be(chosen);
            (await ImportedAsync(newcomer)).Category!.Name.Should().Be($"الألعاب {tag}");
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task A_list_that_ignores_the_category_filter_puts_nobody_in_the_wrong_category()
    {
        var id = NextRemoteId();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var m5azn = new FakeM5azn { IgnoresCategoryFilter = true };
        m5azn.Categories.Add(new M5aznCategory(NextRemoteId(), $"Wrong {tag}", null, null));
        m5azn.ListPage(Catalog(id, $"F-{id}", "Filtered", 10m, 1, ["https://cdn.m5azn.test/f1.jpg", "https://cdn.m5azn.test/f2.jpg"], null, "x"));

        try
        {
            var summary = await ImportAsync(m5azn);
            summary.Problems.Should().Contain(p => p.Contains("does not filter by category"));
            (await ImportedAsync(id)).CategoryId.Should().BeNull();
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    /// <summary>
    /// What the real account hit on 2026-11-07: products imported with one picture, then M5azn's
    /// details giving the whole gallery. The new pictures must be added, not taken for rows to update.
    /// </summary>
    [Fact]
    public async Task An_existing_product_with_one_picture_gets_its_whole_gallery()
    {
        var id = NextRemoteId();
        var m5azn = new FakeM5azn();
        m5azn.ListPage(Catalog(id, $"G-{id}", "Gallery", 35.15m, 29, ["https://cdn.m5azn.test/g0.jpg"], null, "x"));

        try
        {
            await ImportAsync(m5azn);   // no details yet: one picture
            (await ImportedAsync(id)).Images.Should().ContainSingle();

            m5azn.Details[id] = Catalog(id, null, null, null, null,
                ["https://cdn.m5azn.test/g0.jpg", "https://cdn.m5azn.test/g1.jpg", "https://cdn.m5azn.test/g2.jpg"], null, "Full description.");
            var summary = await ImportAsync(m5azn);

            summary.Problems.Should().BeEmpty();
            summary.Updated.Should().BeGreaterThanOrEqualTo(1);
            var product = await ImportedAsync(id);
            product.Images.OrderBy(x => x.DisplayOrder).Select(x => x.Url).Should().Equal(
                "https://cdn.m5azn.test/g0.jpg", "https://cdn.m5azn.test/g1.jpg", "https://cdn.m5azn.test/g2.jpg");
            product.Images.Count(x => x.IsPrimary).Should().Be(1);
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task Only_one_import_runs_at_a_time()
    {
        var id = NextRemoteId();
        var m5azn = new FakeM5azn();
        m5azn.ListPage(Catalog(id, $"ONE-{id}", "One", 10m, 1, ["https://cdn.m5azn.test/o1.jpg", "https://cdn.m5azn.test/o2.jpg"], null, "x"));

        try
        {
            await using var scope = _fixture.CreateScope(m5azn: m5azn);
            var locks = scope.ServiceProvider.GetRequiredService<IDistributedLockService>();
            var catalog = scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>();

            // The hourly sync is running.
            await using (var running = await locks.AcquireAsync("m5azn:import", TimeSpan.FromMinutes(1)))
            {
                running.Should().NotBeNull();
                var second = await catalog.ImportAsync();
                second.AlreadyRunning.Should().BeTrue();
                second.Found.Should().Be(0);
            }

            // Finished: the button works again.
            (await catalog.ImportAsync()).Found.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task The_progress_of_an_import_can_be_read_while_it_runs_and_after()
    {
        var (first, second) = (NextRemoteId(), NextRemoteId());
        var m5azn = new FakeM5azn();
        m5azn.ListPage(Catalog(first, $"PR-{first}", "First", 10m, 1, ["https://cdn.m5azn.test/p1.jpg", "https://cdn.m5azn.test/p2.jpg"], null, "x"));
        m5azn.ListPage(Catalog(second, $"PR-{second}", "Second", 10m, 1, ["https://cdn.m5azn.test/p3.jpg", "https://cdn.m5azn.test/p4.jpg"], null, "x"));

        try
        {
            await using var scope = _fixture.CreateScope(m5azn: m5azn);
            var reader = scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>();

            M5aznImportStatus? whileReadingPageTwo = null;
            m5azn.OnListPage = async page =>
            {
                if (page == 2) whileReadingPageTwo = await reader.GetImportStatusAsync();
            };

            await using var importScope = _fixture.CreateScope(m5azn: m5azn);
            var summary = await importScope.ServiceProvider.GetRequiredService<IM5aznCatalogService>().ImportAsync();

            whileReadingPageTwo.Should().NotBeNull();
            whileReadingPageTwo!.Running.Should().BeTrue();
            whileReadingPageTwo.Phase.Should().Be(M5aznImportPhase.ReadingList);
            whileReadingPageTwo.Trigger.Should().Be(M5aznImportTrigger.Schedule);
            whileReadingPageTwo.Found.Should().BeGreaterThanOrEqualTo(1);
            whileReadingPageTwo.StartedAt.Should().NotBeNull();

            var after = await reader.GetImportStatusAsync();
            after.Running.Should().BeFalse();
            after.Phase.Should().Be(M5aznImportPhase.Finished);
            after.Percent.Should().Be(100);
            after.Summary!.Found.Should().Be(summary.Found);
            after.Error.Should().BeNull();
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task The_button_starts_the_import_in_the_background_and_only_once()
    {
        var id = NextRemoteId();
        var m5azn = new FakeM5azn();
        m5azn.ListPage(Catalog(id, $"BTN-{id}", "Button", 10m, 1, ["https://cdn.m5azn.test/b1.jpg", "https://cdn.m5azn.test/b2.jpg"], null, "x"));

        try
        {
            await using var scope = _fixture.CreateScope(m5azn: m5azn);
            var catalog = scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>();

            var started = await catalog.RequestImportAsync();
            started.Running.Should().BeTrue();
            started.AlreadyRunning.Should().BeFalse();
            started.Phase.Should().Be(M5aznImportPhase.Starting);
            started.Trigger.Should().Be(M5aznImportTrigger.Button);

            (await catalog.RequestImportAsync()).AlreadyRunning.Should().BeTrue();

            // What the background worker does with the request.
            await catalog.RunRequestedImportAsync();

            var after = await catalog.GetImportStatusAsync();
            after.Running.Should().BeFalse();
            after.Trigger.Should().Be(M5aznImportTrigger.Button);
            after.Summary!.Found.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task A_requested_import_that_cannot_start_does_not_stay_on_the_page()
    {
        var m5azn = new FakeM5azn();

        try
        {
            await using var scope = _fixture.CreateScope(m5azn: m5azn);
            var catalog = scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>();
            var locks = scope.ServiceProvider.GetRequiredService<IDistributedLockService>();

            await catalog.RequestImportAsync();

            // Another server is importing, without a status this server can see.
            await using (await locks.AcquireAsync("m5azn:import", TimeSpan.FromMinutes(1)))
            {
                await catalog.RunRequestedImportAsync();
            }

            (await catalog.GetImportStatusAsync()).Running.Should().BeFalse();
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task A_failed_import_says_why_and_is_no_longer_running()
    {
        var m5azn = new FakeM5azn();
        m5azn.OnListPage = _ => throw new HttpRequestException("M5azn answered 500.");

        try
        {
            await using var scope = _fixture.CreateScope(m5azn: m5azn);
            var catalog = scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>();

            await catalog.Invoking(c => c.ImportAsync()).Should().ThrowAsync<DomainException>();

            var after = await catalog.GetImportStatusAsync();
            after.Running.Should().BeFalse();
            after.Phase.Should().Be(M5aznImportPhase.Failed);
            after.Error.Should().Contain("could not be read");
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
    public async Task Every_product_type_is_imported_not_only_the_local_ones()
    {
        var local = NextRemoteId();
        var global = NextRemoteId();
        var global2 = NextRemoteId();
        var ali = NextRemoteId();
        var m5azn = new FakeM5azn();
        m5azn.ListPage(Catalog(local, $"L-{local}", "Local", 10m, 5, ["https://cdn.m5azn.test/l.jpg"], null, "x"));
        m5azn.TypePages["global"] =
        [
            new M5aznProductPage([Catalog(global, $"G-{global}", "Global", 20m, 5, ["https://cdn.m5azn.test/g.jpg"], null, "x")], false, null),
            new M5aznProductPage([Catalog(global2, $"G-{global2}", "Global 2", 20m, 5, ["https://cdn.m5azn.test/g2.jpg"], null, "x")], false, null),
        ];
        m5azn.TypePages["ali_express"] =
            [new M5aznProductPage([Catalog(ali, $"A-{ali}", "Ali", 30m, 5, ["https://cdn.m5azn.test/a.jpg"], null, "x")], false, null)];

        try
        {
            var summary = await ImportAsync(m5azn);

            summary.Found.Should().BeGreaterThanOrEqualTo(4, "the seller's list is every type together, over every page");
            foreach (var id in new[] { local, global, global2, ali })
            {
                (await ImportedAsync(id)).Status.Should().Be(StoreProductStatus.Active);
            }
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task A_product_type_that_cannot_be_read_is_reported_and_hides_nothing()
    {
        var local = NextRemoteId();
        var global = NextRemoteId();
        var m5azn = new FakeM5azn();
        m5azn.ListPage(Catalog(local, $"L-{local}", "Local", 10m, 5, ["https://cdn.m5azn.test/l.jpg"], null, "x"));
        m5azn.TypePages["global"] =
            [new M5aznProductPage([Catalog(global, $"G-{global}", "Global", 20m, 5, ["https://cdn.m5azn.test/g.jpg"], null, "x")], false, null)];

        try
        {
            await ImportAsync(m5azn);

            m5azn.FailingTypes.Add("global");
            var summary = await ImportAsync(m5azn);

            summary.Problems.Should().Contain(x => x.Contains("global"));
            summary.Hidden.Should().Be(0);
            (await ImportedAsync(global)).Status.Should().Be(StoreProductStatus.Active,
                "a list that could not be read says nothing about whether its products are gone");
        }
        finally
        {
            await ClearSettingsAsync();
        }
    }

    [Fact]
    public async Task A_very_long_list_is_read_far_and_hides_nothing_when_it_is_cut_off()
    {
        var beyond = NextRemoteId();
        var page60 = NextRemoteId();
        string[] pictures = ["https://cdn.m5azn.test/x.jpg", "https://cdn.m5azn.test/y.jpg"];

        var first = new FakeM5azn();
        first.ListPage(Catalog(beyond, $"B-{beyond}", "Past the limit", 10m, 5, pictures, null, "x"));

        var longList = new FakeM5azn();
        for (var page = 1; page <= 400; page++)
        {
            var id = page == 60 ? page60 : NextRemoteId();
            longList.ListPage(Catalog(id, $"P-{id}", $"Page {page}", 10m, 5, pictures, null, "x"));
        }
        longList.ListPage(Catalog(beyond, $"B-{beyond}", "Past the limit", 10m, 5, pictures, null, "x"));

        try
        {
            await ImportAsync(first);
            var summary = await ImportAsync(longList);

            (await ImportedAsync(page60)).Status.Should().Be(StoreProductStatus.Active,
                "a list of more than 50 pages is read past the 50th");
            summary.Problems.Should().Contain(x => x.Contains("longer than"));
            summary.Hidden.Should().Be(0);
            (await ImportedAsync(beyond)).Status.Should().Be(StoreProductStatus.Active,
                "a product past the end of what was read has not left M5azn");
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

    [Fact]
    public async Task A_product_with_its_own_profit_rate_keeps_it_and_the_rest_follow_the_general_one()
    {
        var own = NextRemoteId();
        var general = NextRemoteId();
        var m5azn = new FakeM5azn();
        m5azn.ListPage(
            Catalog(own, $"OWN-{own}", "Own rate", 40m, 5, ["https://cdn.m5azn.test/o1.jpg", "https://cdn.m5azn.test/o2.jpg"], null, "x"),
            Catalog(general, $"GEN-{general}", "General rate", 40m, 5, ["https://cdn.m5azn.test/g1.jpg", "https://cdn.m5azn.test/g2.jpg"], null, "x"));

        try
        {
            await ImportAsync(m5azn);
            var ownId = (await ImportedAsync(own)).Id;

            await using (var scope = _fixture.CreateScope(m5azn: m5azn))
            {
                var catalog = scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>();

                var row = await catalog.SetProductProfitAsync(ownId, 20m);
                row.ProfitPercentage.Should().Be(20m);
                row.Price.Should().Be(48m, "40 + its own 20%");

                var negative = () => catalog.SetProductProfitAsync(ownId, -5m);
                (await negative.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ValidationFailed);

                var adminProduct = await NewProductAsync(price: 10m, linked: false);
                var notImported = () => catalog.SetProductProfitAsync(adminProduct, 10m);
                (await notImported.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.ProductNotFound);

                // The general rate changes: only the product without its own rate follows.
                await scope.ServiceProvider.GetRequiredService<IM5aznFulfilmentService>()
                    .UpdateSettingsAsync(new M5aznSettingsRequest(true, null, null, null, true, 25m));
            }

            (await ImportedAsync(own)).Price.Should().Be(48m);
            (await ImportedAsync(general)).Price.Should().Be(50m, "40 + the general 25%");

            // M5azn's price changes: each keeps its own rule.
            m5azn.Pages.Clear();
            m5azn.ListPage(
                Catalog(own, $"OWN-{own}", "Own rate", 50m, 5, ["https://cdn.m5azn.test/o1.jpg", "https://cdn.m5azn.test/o2.jpg"], null, "x"),
                Catalog(general, $"GEN-{general}", "General rate", 50m, 5, ["https://cdn.m5azn.test/g1.jpg", "https://cdn.m5azn.test/g2.jpg"], null, "x"));
            await ImportAsync(m5azn);
            (await ImportedAsync(own)).Price.Should().Be(60m);
            (await ImportedAsync(general)).Price.Should().Be(62.5m);

            // Cleared: back to the general rate.
            await using (var scope = _fixture.CreateScope(m5azn: m5azn))
            {
                var row = await scope.ServiceProvider.GetRequiredService<IM5aznCatalogService>().SetProductProfitAsync(ownId, null);
                row.ProfitPercentage.Should().BeNull();
                row.Price.Should().Be(62.5m);
            }
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

        public List<M5aznCategory> Categories { get; } = new();

        /// <summary>The products M5azn lists under each category id, when asked for one category.</summary>
        public Dictionary<long, List<long>> InCategory { get; } = new();

        /// <summary>True: M5azn answers every category with the whole list, as if it ignored the filter.</summary>
        public bool IgnoresCategoryFilter { get; set; }

        public int CategoryFilterCalls;
        public int DetailCalls;

        /// <summary>The list M5azn gives for a named product type (global, ali_express, ...); none: empty.</summary>
        public Dictionary<string, List<M5aznProductPage>> TypePages { get; } = new();

        /// <summary>Product types whose list cannot be read.</summary>
        public HashSet<string> FailingTypes { get; } = new();

        /// <summary>Called as each page of the list is asked for, with the page number.</summary>
        public Func<int, Task>? OnListPage { get; set; }

        public async Task<M5aznProductPage> GetMyProductsAsync(int page, long? categoryId = null, CancellationToken ct = default, string? productType = null)
        {
            if (OnListPage is { } onListPage && productType is null && categoryId is null) await onListPage(page);

            if (productType is not null && FailingTypes.Contains(productType))
            {
                throw new HttpRequestException($"M5azn answered 500 for {productType}.");
            }

            var pages = productType is null ? Pages : TypePages.GetValueOrDefault(productType) ?? [];
            if (categoryId is not null) Interlocked.Increment(ref CategoryFilterCalls);
            var all = page <= pages.Count
                ? pages[page - 1] with { HasMore = page < pages.Count }
                : new M5aznProductPage([], false, null);
            if (categoryId is null || IgnoresCategoryFilter) return all;

            var ids = InCategory.TryGetValue(categoryId.Value, out var list) ? list : [];
            return all with { Products = all.Products.Where(p => ids.Contains(p.ProductId)).ToList() };
        }

        public Task<M5aznCategoryList> GetCategoriesAsync(CancellationToken ct = default) =>
            Task.FromResult(new M5aznCategoryList(Categories.ToList(), null));

        public Task<M5aznCatalogProduct?> GetProductAsync(long productId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref DetailCalls);
            return Task.FromResult(Details.TryGetValue(productId, out var d) ? d : null);
        }

        public Task<string?> CheckSignInAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
