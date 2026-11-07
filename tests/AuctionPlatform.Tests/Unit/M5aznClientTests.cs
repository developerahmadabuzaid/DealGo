using System.Net;
using System.Text;
using System.Text.Json;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.M5azn;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Tests.Unit;

/// <summary>
/// The M5azn client, against a stand-in for M5azn's HTTP API. Nothing here reaches m5azn.com.
///
/// Two things matter: that what goes out matches M5azn's documented request (headers, sign-in,
/// the order body), and that every answer is sorted by whether sending the same order again
/// could place it twice — the difference between a refusal, a certain miss, and an unclear answer.
/// </summary>
public class M5aznClientTests
{
    private static readonly M5aznOrderRequest Order = new(
        100001, 450.01m, "card", 0m, new DateTime(2025, 4, 10, 18, 56, 30, DateTimeKind.Utc), "DealGo",
        new M5aznShippingInfo("1234 King Fahd Rd, Al Olaya", "Saudi Arabia", "SA", "Riyadh"),
        new M5aznCustomerInfo("Ahmad", "Ali", "a@example.test", "+966551234567"),
        new[] { new M5aznOrderItem(1, 1947201, "M2-4-3-4-S", "test-1", 1) });

    [Fact]
    public void Without_the_flag_or_the_account_the_client_is_off()
    {
        Build(new Router(), enabled: false).IsEnabled.Should().BeFalse();
        Build(new Router(), password: "").IsEnabled.Should().BeFalse();
        Build(new Router()).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task An_order_signs_in_once_and_carries_the_documented_headers_and_body()
    {
        var router = new Router();
        router.On("POST", "/api/v1/orders", _ => Json(HttpStatusCode.OK, new
        {
            status = 200, success = true, message = "created",
            data = new { order_id = 98765, status = "pending" }
        }));

        var client = Build(router);
        var result = await client.CreateOrderAsync(Order);
        await client.CreateOrderAsync(Order with { Id = 100002 });

        result.Outcome.Should().Be(M5aznSubmitOutcome.Accepted);
        result.RemoteOrderId.Should().Be("98765");
        result.RemoteStatus.Should().Be("pending");

        router.Calls.Count(c => c.Path == "/api/v1/auth/login").Should().Be(1, "the token is reused");

        var login = router.Calls.First(c => c.Path == "/api/v1/auth/login");
        var credentials = JsonDocument.Parse(login.Body!).RootElement;
        credentials.GetProperty("email").GetString().Should().Be("seller@example.test");
        credentials.GetProperty("password").GetString().Should().Be("secret-pass");

        var call = router.Calls.First(c => c.Path == "/api/v1/orders");
        call.Headers["Authorization"].Should().Be($"Bearer {router.Token}");
        call.Headers["X-Website"].Should().Be("M5AZN");
        call.Headers["X-Lang"].Should().Be("ar");
        call.Headers["X-Api"].Should().Be("1.0");
        call.Headers["X-Version"].Should().Be("1.0");
        call.Headers["X-Device"].Should().Be("Web");
        call.Headers["Accept"].Should().Contain("application/json");

        var body = JsonDocument.Parse(call.Body!).RootElement;
        body.GetProperty("id").GetInt64().Should().Be(100001);
        body.GetProperty("total").GetDecimal().Should().Be(450.01m);
        body.GetProperty("payment_method").GetString().Should().Be("card");
        body.GetProperty("cod_amount").GetDecimal().Should().Be(0m);
        body.GetProperty("order_created_at").GetString().Should().Be("2025-04-10 21:56:30", "Saudi time, as in M5azn's example");
        body.GetProperty("platform_name").GetString().Should().Be("DealGo");
        body.GetProperty("shipping_info").GetProperty("country_code").GetString().Should().Be("SA");
        body.GetProperty("customer_info").GetProperty("first_name").GetString().Should().Be("Ahmad");
        var item = body.GetProperty("items")[0];
        item.GetProperty("product_id").GetInt64().Should().Be(1947201);
        item.GetProperty("sku").GetString().Should().Be("M2-4-3-4-S");
        item.GetProperty("qty_ordered").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_refused_token_signs_in_again_once()
    {
        var router = new Router();
        var first = true;
        router.On("POST", "/api/v1/orders", _ =>
        {
            if (first) { first = false; return new HttpResponseMessage(HttpStatusCode.Unauthorized); }
            return Json(HttpStatusCode.OK, new { success = true, data = new { id = 5 } });
        });

        var result = await Build(router).CreateOrderAsync(Order);

        result.Outcome.Should().Be(M5aznSubmitOutcome.Accepted);
        result.RemoteOrderId.Should().Be("5");
        router.Calls.Count(c => c.Path == "/api/v1/auth/login").Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, M5aznSubmitOutcome.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, M5aznSubmitOutcome.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, M5aznSubmitOutcome.NotSent)]
    [InlineData(HttpStatusCode.TooManyRequests, M5aznSubmitOutcome.NotSent)]
    [InlineData(HttpStatusCode.InternalServerError, M5aznSubmitOutcome.Unknown)]
    [InlineData(HttpStatusCode.BadGateway, M5aznSubmitOutcome.Unknown)]
    public async Task Each_answer_is_sorted_by_what_it_means_for_sending_again(HttpStatusCode status, M5aznSubmitOutcome expected)
    {
        var router = new Router();
        router.On("POST", "/api/v1/orders", _ => Json(status, new { success = false, message = "sku not found" }));

        var result = await Build(router).CreateOrderAsync(Order);

        result.Outcome.Should().Be(expected);
        if (expected == M5aznSubmitOutcome.Rejected) result.Message.Should().Contain("sku not found");
    }

    [Fact]
    public async Task A_200_that_says_success_false_is_a_refusal()
    {
        var router = new Router();
        router.On("POST", "/api/v1/orders", _ => Json(HttpStatusCode.OK,
            new { status = 422, success = false, error = true, message = "Insufficient wallet balance" }));

        var result = await Build(router).CreateOrderAsync(Order);

        result.Outcome.Should().Be(M5aznSubmitOutcome.Rejected);
        result.Message.Should().Be("Insufficient wallet balance");
    }

    [Fact]
    public async Task A_failed_sign_in_means_the_order_was_not_sent()
    {
        var router = new Router { LoginStatus = HttpStatusCode.UnprocessableEntity };

        var result = await Build(router).CreateOrderAsync(Order);

        result.Outcome.Should().Be(M5aznSubmitOutcome.NotSent);
        router.Calls.Should().NotContain(c => c.Path == "/api/v1/orders");
    }

    [Fact]
    public async Task No_connection_at_all_means_not_sent_and_a_timeout_means_unclear()
    {
        var refused = new Router();
        refused.On("POST", "/api/v1/orders", _ =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        (await Build(refused).CreateOrderAsync(Order)).Outcome.Should().Be(M5aznSubmitOutcome.NotSent);

        var slow = new Router();
        slow.On("POST", "/api/v1/orders", _ => throw new TaskCanceledException("timeout"));
        (await Build(slow).CreateOrderAsync(Order)).Outcome.Should().Be(M5aznSubmitOutcome.Unknown);
    }

    [Fact]
    public async Task Order_details_are_read_from_whichever_names_M5azn_uses()
    {
        var router = new Router();
        router.On("GET", "/api/orders/98765", _ => Json(HttpStatusCode.OK, new
        {
            status = 200,
            success = true,
            data = new
            {
                id = 98765,
                order_status = "shipped",
                items = new[] { new { id = 1, status = "ignored line status" } },
                shipment = new { awb_number = "SMSA123", tracking_link = "https://t.example/SMSA123", shipping_company = new { name = "SMSA" } }
            }
        }));
        router.On("GET", "/api/orders/404", _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var client = Build(router);
        var order = await client.GetOrderAsync("98765");

        order.Should().NotBeNull();
        order!.RemoteOrderId.Should().Be("98765");
        order.Status.Should().Be("shipped", "the envelope's numeric status is not the order's");
        order.TrackingNumber.Should().Be("SMSA123");
        order.TrackingUrl.Should().Be("https://t.example/SMSA123");
        order.Carrier.Should().Be("SMSA");

        (await client.GetOrderAsync("404")).Should().BeNull();
    }

    [Fact]
    public async Task Stock_changes_are_read_per_product_and_per_variant()
    {
        var router = new Router();
        router.On("GET", "/api/v1/products/qty-changed", req =>
        {
            req.RequestUri!.Query.Should().Be("?hours=2");
            return Json(HttpStatusCode.OK, new
            {
                success = true,
                data = new object[]
                {
                    new { product_id = 11, sku = "A", qty = 4 },
                    new { id = 12, variants = new[] { new { sku = "B-S", quantity = 1 }, new { sku = "B-M", quantity = 0 } } },
                    new { product_id = "not-a-number", qty = 3 }
                }
            });
        });

        var changes = await Build(router).GetQuantityChangesAsync(2);

        changes.Should().BeEquivalentTo(new[]
        {
            new M5aznStockChange(11, "A", 4),
            new M5aznStockChange(12, "B-S", 1),
            new M5aznStockChange(12, "B-M", 0)
        });
    }

    [Fact]
    public void A_token_is_kept_until_shortly_before_it_expires()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var exp = new DateTimeOffset(now.AddHours(5)).ToUnixTimeSeconds();

        M5aznClient.ExpiryOf(FakeJwt(exp), now).Should().Be(now.AddHours(5).AddMinutes(-10));
        M5aznClient.ExpiryOf("not-a-jwt", now).Should().Be(now.AddDays(1));
    }

    [Theory]
    [InlineData("pending", M5aznShipmentStatus.Processing)]
    [InlineData("Processing", M5aznShipmentStatus.Processing)]
    [InlineData("قيد التجهيز", M5aznShipmentStatus.Processing)]
    [InlineData("shipped", M5aznShipmentStatus.Shipped)]
    [InlineData("out_for_delivery", M5aznShipmentStatus.Shipped)]
    [InlineData("تم الشحن", M5aznShipmentStatus.Shipped)]
    [InlineData("delivered", M5aznShipmentStatus.Delivered)]
    [InlineData("تم التوصيل", M5aznShipmentStatus.Delivered)]
    [InlineData("cancelled", M5aznShipmentStatus.Cancelled)]
    [InlineData("returned", M5aznShipmentStatus.Cancelled)]
    [InlineData("ملغي", M5aznShipmentStatus.Cancelled)]
    [InlineData("not delivered", null)]
    [InlineData("3", null)]
    [InlineData("", null)]
    public void Status_words_are_read_by_meaning(string remote, M5aznShipmentStatus? expected) =>
        M5aznStatusMap.Map(remote).Should().Be(expected);

    // ---------------------------------------------------------------- helpers

    private static M5aznClient Build(Router router, bool enabled = true, string password = "secret-pass")
    {
        var http = new HttpClient(router);
        var options = new M5aznOptions
        {
            Enabled = enabled,
            BaseUrl = "https://m5azn.test",
            Email = "seller@example.test",
            Password = password
        };

        return new M5aznClient(http, Options.Create(options), new M5aznTokenStore(),
            new SystemDateTimeProvider(), NullLogger<M5aznClient>.Instance);
    }

    private static string FakeJwt(long exp)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"exp\":{exp}}}")}.sig";
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private sealed record Call(string Method, string Path, string? Body, Dictionary<string, string> Headers);

    /// <summary>M5azn's sign-in plus whatever routes a test adds. Keeps every call it saw.</summary>
    private sealed class Router : HttpMessageHandler
    {
        private readonly Dictionary<(string, string), Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
        private int _logins;

        public List<Call> Calls { get; } = new();
        public HttpStatusCode LoginStatus { get; set; } = HttpStatusCode.OK;
        private static readonly long Expiry = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        public string Token => FakeJwt(Expiry) + _logins;

        public void On(string method, string path, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
            _routes[(method, path)] = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add(new Call(request.Method.Method, request.RequestUri!.AbsolutePath, body, headers));

            if (request.RequestUri.AbsolutePath == "/api/v1/auth/login")
            {
                if (LoginStatus != HttpStatusCode.OK)
                {
                    return Json(LoginStatus, new { success = false, message = "The given data was invalid." });
                }

                _logins++;
                return Json(HttpStatusCode.OK, new
                {
                    status = 200, success = true, message = "Successfully logged In",
                    data = new { accessToken = Token, userId = 1, shop_name = "Test Shop" }
                });
            }

            return _routes.TryGetValue((request.Method.Method, request.RequestUri.AbsolutePath), out var respond)
                ? respond(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
