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

    // ---------------------------------------------------------------- the seller's product list

    [Fact]
    public async Task A_paged_product_list_is_read_with_every_picture_and_plain_text()
    {
        var router = new Router();
        router.On("GET", "/api/v1/seller/myproductlist", req => Json(HttpStatusCode.OK, new
        {
            status = 200,
            success = true,
            data = new
            {
                current_page = 1,
                last_page = 2,
                data = new object[]
                {
                    // A seller row carrying the product inside it.
                    new
                    {
                        id = 77,
                        seller_price = "35.15",
                        product = new
                        {
                            id = 313658,
                            name = "سيروم فيتامين سي من أرينسيا",
                            sku = "8809562192770",
                            description = "<p>Brightening&nbsp;serum</p><ul><li>30 ml</li></ul>",
                            quantity = 12,
                            image = "/storage/products/main.jpg",
                            images = new object[] { new { url = "https://cdn.m5azn.test/1.jpg" }, "https://cdn.m5azn.test/2.jpg" },
                            category = new { id = 5, name = "العناية بالبشرة" },
                            delivery_time = "1-5 أيام"
                        }
                    },
                    // A flat one.
                    new { product_id = 99, title = "Flat", sku = "F-1", price = 20, stock = 0 }
                }
            }
        }));

        var page = await Build(router).GetMyProductsAsync(1);

        page.Unreadable.Should().BeFalse();
        page.HasMore.Should().BeTrue("last_page is 2");
        page.Problem.Should().BeNull();
        page.Products.Should().HaveCount(2);

        var serum = page.Products[0];
        serum.ProductId.Should().Be(313658, "the product's own id, not the seller row's");
        serum.Sku.Should().Be("8809562192770");
        serum.Price.Should().Be(35.15m);
        serum.Quantity.Should().Be(12);
        serum.Description.Should().Be("Brightening serum\n30 ml");
        serum.CategoryName.Should().Be("العناية بالبشرة");
        serum.DeliveryTime.Should().Be("1-5 أيام");
        serum.Images.Should().Equal(
            "https://m5azn.test/storage/products/main.jpg", "https://cdn.m5azn.test/1.jpg", "https://cdn.m5azn.test/2.jpg");

        page.Products[1].Should().BeEquivalentTo(new { ProductId = 99L, Name = "Flat", Sku = "F-1", Price = 20m, Quantity = 0 });
        router.Calls.Should().Contain(c => c.Method == "GET" && c.Path == "/api/v1/seller/myproductlist");
    }

    /// <summary>The shape M5azn's real seller list came in on the first import (2026-11-07).</summary>
    [Fact]
    public async Task The_seller_list_inside_records_is_read_with_its_paging()
    {
        var router = new Router();
        router.On("GET", "/api/v1/seller/myproductlist", _ => Json(HttpStatusCode.OK, new
        {
            status = 200,
            success = true,
            data = new
            {
                records = new
                {
                    current_page = 1,
                    last_page = 3,
                    data = new object[]
                    {
                        new
                        {
                            id = 140008, name_en = "Dummy Product 1", name_ar = "منتج تجريبي 1", sku = "Dummy Sku 1",
                            barcode = "dummycode01", qty = 0, price = 30, suggest_price = 100, sale_price = 51,
                            price_protect = false, image = "https://d1gpzof0viq1mp.cloudfront.net/p/1.jpg"
                        }
                    }
                }
            }
        }));

        var page = await Build(router).GetMyProductsAsync(1);

        page.Unreadable.Should().BeFalse();
        page.HasMore.Should().BeTrue("records.last_page is 3");
        page.Products.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ProductId = 140008L,
            Name = "منتج تجريبي 1",
            Sku = "Dummy Sku 1",
            Price = 30m,
            Quantity = 0,
            Images = new[] { "https://d1gpzof0viq1mp.cloudfront.net/p/1.jpg" }
        });
    }

    [Fact]
    public async Task An_empty_records_list_is_an_empty_shop()
    {
        var router = new Router();
        router.On("GET", "/api/v1/seller/myproductlist", _ => Json(HttpStatusCode.OK, new
        {
            success = true, data = new { records = new { current_page = 1, last_page = 1, data = Array.Empty<object>() } }
        }));

        var page = await Build(router).GetMyProductsAsync(1);

        page.Unreadable.Should().BeFalse();
        page.Products.Should().BeEmpty();
    }

    [Fact]
    public async Task Categories_and_their_sub_categories_are_read_in_Arabic_with_pictures()
    {
        var router = new Router();
        router.On("GET", "/api/v1/catalog/categories", _ => Json(HttpStatusCode.OK, new
        {
            success = true,
            data = new object[]
            {
                new
                {
                    id = 10, name_en = "Care", name_ar = "العناية", image = "/storage/cat/care.png",
                    children = new object[] { new { id = 11, name_en = "Skin", name_ar = "العناية بالبشرة" } }
                },
                new { id = 20, name_ar = "الكاميرات", icon = "https://cdn.m5azn.test/cam.png" }
            }
        }));

        var list = await Build(router).GetCategoriesAsync();

        list.Problem.Should().BeNull();
        list.Categories.Should().BeEquivalentTo(new[]
        {
            new M5aznCategory(10, "العناية", null, "https://m5azn.test/storage/cat/care.png"),
            new M5aznCategory(11, "العناية بالبشرة", 10, null),
            new M5aznCategory(20, "الكاميرات", null, "https://cdn.m5azn.test/cam.png")
        });
    }

    [Fact]
    public async Task A_product_category_given_as_an_id_or_an_object_is_kept()
    {
        var router = new Router();
        router.On("GET", "/api/v1/seller/myproductlist", req => Json(HttpStatusCode.OK, new
        {
            success = true,
            data = new { records = new { current_page = 1, last_page = 1, data = new object[]
            {
                new { id = 1, name_ar = "أ", sku = "A", price = 1, category_id = 55 },
                new { id = 2, name_ar = "ب", sku = "B", price = 1, category = new { id = 11, name_ar = "العناية بالبشرة", parent = new { id = 10, name_ar = "العناية" } } }
            } } }
        }));

        var page = await Build(router).GetMyProductsAsync(1, categoryId: 10);

        page.Products[0].CategoryId.Should().Be(55);
        page.Products[0].CategoryName.Should().BeNull();
        page.Products[1].Should().BeEquivalentTo(new { CategoryId = 11L, CategoryName = "العناية بالبشرة", ParentCategoryName = "العناية" });
    }

    [Fact]
    public async Task A_read_answered_too_many_requests_is_waited_out_and_asked_again()
    {
        var router = new Router();
        var asked = 0;
        router.On("GET", "/api/v1/catalog/categories", _ =>
        {
            if (++asked == 1)
            {
                var busy = Json(HttpStatusCode.TooManyRequests, new { message = "Too Many Attempts." });
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return busy;
            }

            return Json(HttpStatusCode.OK, new { success = true, data = new object[] { new { id = 1, name_ar = "العناية" } } });
        });

        var list = await Build(router).GetCategoriesAsync();

        asked.Should().Be(2);
        list.Categories.Should().ContainSingle(c => c.Name == "العناية");
    }

    [Fact]
    public async Task An_order_answered_too_many_requests_is_not_sent_again_on_the_spot()
    {
        var router = new Router();
        var posted = 0;
        router.On("POST", "/api/v1/orders", _ =>
        {
            posted++;
            var busy = Json(HttpStatusCode.TooManyRequests, new { message = "Too Many Attempts." });
            busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return busy;
        });

        var result = await Build(router).CreateOrderAsync(new M5aznOrderRequest(100001, 10m, "card", 0m, DateTime.UtcNow, "DealGo",
            new M5aznShippingInfo("a", "Saudi Arabia", "SA", "الرياض"), new M5aznCustomerInfo("a", "b", "c@d.e", "+966500000000"),
            [new M5aznOrderItem(1, 1, "S", "n", 1)]));

        posted.Should().Be(1, "an order is never repeated by the transport");
        result.Outcome.Should().Be(M5aznSubmitOutcome.NotSent);
    }

    // The shapes below are M5azn's real answers as seen on 2026-11-07 (field names; values made up).

    [Fact]
    public async Task The_real_category_tree_is_read_through_p_id_and_getChild()
    {
        var router = new Router();
        var child = new { id = 488, p_id = 12, name_en = "Skin, Face & Body Care", name_ar = "العناية بالبشرة والوجه والجسم", status = 1, category_products_count = 30 };
        router.On("GET", "/api/v1/catalog/categories", _ => Json(HttpStatusCode.OK, new
        {
            status = 200,
            success = true,
            data = new
            {
                userInfo = new { userId = 1, email = "seller@example.test", mobile = "0500000000", wallet = 10 },
                records = new object[]
                {
                    new { id = 12, p_id = 0, name_en = "Care", name_ar = "العناية", category_products_count = 300, getChild = new[] { child }, get_child = new[] { child } },
                    new { id = 109, p_id = 0, name_en = "Devices", name_ar = "الأجهزة", category_products_count = 9, get_child = Array.Empty<object>() }
                }
            },
            error = false,
            message = ""
        }));

        var list = await Build(router).GetCategoriesAsync();

        list.Problem.Should().BeNull();
        list.Categories.Should().BeEquivalentTo(new[]
        {
            new M5aznCategory(12, "العناية", null, null),
            new M5aznCategory(488, "العناية بالبشرة والوجه والجسم", 12, null),
            new M5aznCategory(109, "الأجهزة", null, null)
        }, "p_id 0 is a main category, and getChild/get_child repeat the same children");
    }

    [Fact]
    public async Task The_real_product_details_give_the_category_every_picture_and_the_description()
    {
        var router = new Router();
        router.On("GET", "/api/v1/product/details/3072551", _ => Json(HttpStatusCode.OK, new
        {
            status = 200,
            success = true,
            data = new
            {
                records = new
                {
                    id = 3072551, sku = "8809562192770", name_en = "Arencia Vitamin C Booster Shot 30ml", name_ar = "ارينسيا - سيروم",
                    category_id = 488, category_name = "العناية بالبشرة والوجه والجسم", brand_id = "", brand_name = "",
                    qty = 29, price = 35.15, suggest_price = 69, image = "https://cdn.m5azn.test/main.jpg",
                    delivery_time = "1-5 أيام",
                    multi_images = new object[] { new { id = 1, image = "https://cdn.m5azn.test/1.jpg" }, new { id = 2, image = "https://cdn.m5azn.test/2.jpg" } },
                    description = new { ar = "<p>سيروم بفيتامين سي</p>", en = "Vitamin C serum" },
                    has_attributes = Array.Empty<object>()
                }
            },
            error = false
        }));

        var product = await Build(router).GetProductAsync(3072551);

        product.Should().NotBeNull();
        product!.Should().BeEquivalentTo(new
        {
            ProductId = 3072551L, Sku = "8809562192770", Name = "ارينسيا - سيروم", Price = 35.15m, Quantity = 29,
            CategoryId = 488L, CategoryName = "العناية بالبشرة والوجه والجسم", Description = "سيروم بفيتامين سي", DeliveryTime = "1-5 أيام"
        });
        product.Images.Should().Equal("https://cdn.m5azn.test/main.jpg", "https://cdn.m5azn.test/1.jpg", "https://cdn.m5azn.test/2.jpg");
    }

    [Fact]
    public async Task The_real_product_list_gives_the_category_id_and_the_description()
    {
        var router = new Router();
        router.On("GET", "/api/v1/seller/myproductlist", _ => Json(HttpStatusCode.OK, new
        {
            status = 200,
            success = true,
            data = new { records = new
            {
                current_page = 1, last_page = 1, next_page_url = (string?)null,
                data = new object[]
                {
                    new
                    {
                        id = 3072551, name_en = "Arencia", name_ar = "ارينسيا", sku = "8809562192770", qty = 29, price = 35.15,
                        suggest_price = 69, sale_price = 50, image = "https://cdn.m5azn.test/main.jpg",
                        product_detail_en = "Serum", product_detail_ar = "سيروم", category_id = 488, brand_id = (int?)null,
                        delivery_time = "1-5 أيام", has_attributes = Array.Empty<object>()
                    }
                }
            } }
        }));

        var page = await Build(router).GetMyProductsAsync(1);

        page.Products.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ProductId = 3072551L, CategoryId = 488L, CategoryName = (string?)null, Description = "سيروم", Price = 35.15m
        });
    }

    [Fact]
    public async Task An_unknown_answer_is_said_to_be_unreadable_not_empty()
    {
        var router = new Router();
        router.On("GET", "/api/v1/seller/myproductlist", _ => Json(HttpStatusCode.OK, new { success = true, data = new { shop = "x" } }));

        var page = await Build(router).GetMyProductsAsync(1);

        page.Unreadable.Should().BeTrue();
        page.Products.Should().BeEmpty();
        page.Problem.Should().Contain("shape DealGo does not know");
    }

    [Fact]
    public async Task An_empty_list_is_an_empty_shop()
    {
        var router = new Router();
        router.On("GET", "/api/v1/seller/myproductlist", _ => Json(HttpStatusCode.OK, new { success = true, data = Array.Empty<object>() }));

        var page = await Build(router).GetMyProductsAsync(1);

        page.Unreadable.Should().BeFalse();
        page.Products.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
    }

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

        var account = new FixedAccount(new M5aznAccount(enabled, options.BaseUrl, options.Email, options.Password,
            true, 70m, M5aznSettingsSource.Server));

        return new M5aznClient(http, Options.Create(options), account, new M5aznTokenStore(),
            new SystemDateTimeProvider(), NullLogger<M5aznClient>.Instance);
    }

    private sealed class FixedAccount(M5aznAccount account) : IM5aznAccountProvider
    {
        public M5aznAccount Current => account;
        public Task<M5aznAccount> RefreshAsync(bool force = false, CancellationToken ct = default) => Task.FromResult(account);
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
