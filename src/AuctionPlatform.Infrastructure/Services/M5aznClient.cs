using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.M5azn;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// The seller session with M5azn. One per process: signing in on every call would be slow and,
/// for a supplier that watches its sellers, noisy.
/// </summary>
public sealed class M5aznTokenStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private string? _sessionKey;
    private DateTime _expiresAt;

    internal SemaphoreSlim Gate => _gate;

    /// <summary>The token, while it is fresh and was issued for the account in use now.</summary>
    internal string? Current(DateTime now, string sessionKey) =>
        _token is not null && now < _expiresAt && _sessionKey == sessionKey ? _token : null;

    internal void Set(string token, DateTime expiresAt, string sessionKey)
    {
        _token = token;
        _expiresAt = expiresAt;
        _sessionKey = sessionKey;
    }

    internal void Clear(string? token)
    {
        // Only drop the token that failed, not one another call has just fetched.
        if (token is null || token == _token) _token = null;
    }
}

/// <summary>
/// Talks to M5azn's seller API (https://m5azn-api.stoplight.io). Its documentation gives the
/// request bodies but, apart from sign-in, not the answers, so every answer is read loosely:
/// the usual <c>{ status, success, data, message }</c> envelope, and the fields looked for under
/// the names they are likely to have. What is assumed is written down in docs/M5AZN.md.
///
/// Neither the password nor the token is ever logged.
/// </summary>
public sealed class M5aznClient : IM5aznClient
{
    private static readonly TimeZoneInfo Riyadh = FindRiyadh();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly HttpClient _http;
    private readonly M5aznOptions _options;
    private readonly IM5aznAccountProvider _account;
    private readonly M5aznTokenStore _tokens;
    private readonly IDateTimeProvider _clock;
    private readonly ILogger<M5aznClient> _logger;

    public M5aznClient(
        HttpClient http,
        IOptions<M5aznOptions> options,
        IM5aznAccountProvider account,
        M5aznTokenStore tokens,
        IDateTimeProvider clock,
        ILogger<M5aznClient> logger)
    {
        _http = http;
        _options = options.Value;
        _account = account;
        _tokens = tokens;
        _clock = clock;
        _logger = logger;

        // One client per scope, so the address in use when it was made is the one it keeps.
        if (Uri.TryCreate(account.Current.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
        {
            _http.BaseAddress ??= baseUri;
        }
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 120));
    }

    public bool IsEnabled => _account.Current.IsActive && _http.BaseAddress is not null;

    public async Task<string?> CheckSignInAsync(CancellationToken ct = default)
    {
        var account = _account.Current;
        if (!account.HasCredentials) return "The seller email and password are not set.";
        if (_http.BaseAddress is null) return "The M5azn address is not a valid URL.";

        _tokens.Clear(null);
        try
        {
            await GetTokenAsync(ct);
            return null;
        }
        catch (M5aznSignInException ex)
        {
            return ex.Message;
        }
    }

    // ---------------------------------------------------------------- orders

    public async Task<M5aznSubmitResult> CreateOrderAsync(M5aznOrderRequest order, CancellationToken ct = default)
    {
        var body = new
        {
            id = order.Id,
            total = order.Total,
            payment_method = order.PaymentMethod,
            cod_amount = order.CodAmount,
            order_created_at = ToRiyadhText(order.OrderCreatedAt),
            platform_name = order.PlatformName,
            shipping_info = new
            {
                address = order.ShippingInfo.Address,
                country = order.ShippingInfo.Country,
                country_code = order.ShippingInfo.CountryCode,
                city = order.ShippingInfo.City
            },
            customer_info = new
            {
                first_name = order.CustomerInfo.FirstName,
                last_name = order.CustomerInfo.LastName,
                email = order.CustomerInfo.Email,
                phone = order.CustomerInfo.Phone
            },
            items = order.Items.Select(i => new
            {
                id = i.Id,
                product_id = i.ProductId,
                sku = i.Sku,
                name = i.Name,
                qty_ordered = i.QtyOrdered
            })
        };

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "api/v1/orders")
            {
                Content = JsonContent.Create(body, options: Json)
            }, ct);
        }
        catch (M5aznSignInException ex)
        {
            return new M5aznSubmitResult(M5aznSubmitOutcome.NotSent, Message: ex.Message);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError
                                                  or HttpRequestError.NameResolutionError
                                                  or HttpRequestError.SecureConnectionError)
        {
            return new M5aznSubmitResult(M5aznSubmitOutcome.NotSent, Message: $"Could not reach M5azn ({ex.HttpRequestError}).");
        }
        catch (HttpRequestException ex)
        {
            return new M5aznSubmitResult(M5aznSubmitOutcome.Unknown, Message: $"The connection broke ({ex.HttpRequestError}).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new M5aznSubmitResult(M5aznSubmitOutcome.Unknown, Message: "M5azn did not answer in time.");
        }

        using (response)
        {
            var root = await ReadJsonAsync(response, ct);
            var message = Text(root?["message"]) ?? Text(root?["error"] is JsonValue ? root["error"] : null);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                if (IsFalse(root?["success"]) || IsTrue(root?["error"]))
                {
                    return new M5aznSubmitResult(M5aznSubmitOutcome.Rejected, Message: message ?? "success: false");
                }

                var data = Data(root);
                return new M5aznSubmitResult(
                    M5aznSubmitOutcome.Accepted,
                    RemoteOrderId: Find(data, OrderIdKeys, nested: true) ?? Find(data, ["id"], nested: false),
                    RemoteStatus: Find(data, StatusKeys, nested: true),
                    Message: message);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new M5aznSubmitResult(M5aznSubmitOutcome.NotSent,
                    Message: $"M5azn refused the seller account ({status}). Check the seller account on the M5azn page.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new M5aznSubmitResult(M5aznSubmitOutcome.NotSent, Message: "M5azn asked us to slow down (429).");
            }

            if (status is >= 400 and < 500)
            {
                return new M5aznSubmitResult(M5aznSubmitOutcome.Rejected, Message: $"{status}: {message ?? Snippet(root)}");
            }

            return new M5aznSubmitResult(M5aznSubmitOutcome.Unknown, Message: $"M5azn answered {status}.");
        }
    }

    public async Task<M5aznRemoteOrder?> GetOrderAsync(string orderId, CancellationToken ct = default)
    {
        // The documentation gives this one without the /v1 the others have.
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"api/orders/{Uri.EscapeDataString(orderId)}"), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var root = await ReadJsonAsync(response, ct);
        if (root is null || IsFalse(root["success"])) return null;

        var data = Data(root);
        if (data is null) return null;

        return new M5aznRemoteOrder(
            Find(data, OrderIdKeys, nested: true) ?? Find(data, ["id"], nested: false),
            Find(data, StatusKeys, nested: true),
            Find(data, TrackingKeys, nested: true),
            Find(data, TrackingUrlKeys, nested: true),
            Find(data, CarrierKeys, nested: true));
    }

    // ---------------------------------------------------------------- catalogue

    public async Task<IReadOnlyList<M5aznStockChange>> GetQuantityChangesAsync(int hours, CancellationToken ct = default)
    {
        hours = Math.Clamp(hours, 1, 24);
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"api/v1/products/qty-changed?hours={hours}"), ct);
        response.EnsureSuccessStatusCode();

        var root = await ReadJsonAsync(response, ct);
        var list = AsArray(Data(root)) ?? AsArray(root);
        if (list is null) return Array.Empty<M5aznStockChange>();

        var changes = new List<M5aznStockChange>();
        foreach (var item in list.OfType<JsonObject>())
        {
            if (!long.TryParse(Find(item, ["product_id", "productId", "id"], nested: false), out var productId)) continue;

            var variants = AsArray(item["variants"]) ?? AsArray(item["skus"]) ?? AsArray(item["options"]);
            if (variants is { Count: > 0 })
            {
                foreach (var v in variants.OfType<JsonObject>())
                {
                    if (TryQuantity(v, out var vq)) changes.Add(new M5aznStockChange(productId, Find(v, ["sku"], false), vq));
                }
                continue;
            }

            if (TryQuantity(item, out var qty)) changes.Add(new M5aznStockChange(productId, Find(item, ["sku"], false), qty));
        }

        return changes;
    }

    public async Task<M5aznCatalogProduct?> GetProductAsync(long productId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"api/v1/product/details/{productId}"), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var root = await ReadJsonAsync(response, ct);
        if (root is null || IsFalse(root["success"])) return null;
        var data = root is JsonObject obj && obj.ContainsKey("data") ? obj["data"] : root;
        if (data is JsonObject { Count: 1 } wrapper && wrapper["product"] is JsonObject inner) data = inner;
        if (data is not JsonObject product) return null;

        var parsed = ParseProduct(product);
        return parsed is null ? null : parsed with { ProductId = productId };
    }

    public async Task<M5aznProductPage> GetMyProductsAsync(int page, long? categoryId = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        var path = categoryId is { } c
            ? $"api/v1/seller/myproductlist?page={page}&category_id={c}"
            : $"api/v1/seller/myproductlist?page={page}";
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), ct);
        response.EnsureSuccessStatusCode();

        var root = await ReadJsonAsync(response, ct);
        if (root is JsonObject o && IsFalse(o["success"]))
        {
            return new M5aznProductPage([], false, $"M5azn refused the product list: {Text(o["message"]) ?? Snippet(root)}", Unreadable: true);
        }

        var found = FindProductList(root) ?? EmptyProductList(root);
        if (found is null)
        {
            _logger.LogWarning("M5azn: the product list came in an unknown shape: {Shape}", Snippet(root));
            return new M5aznProductPage([], false,
                $"M5azn's product list came in a shape DealGo does not know yet: {Snippet(root, 1500)}", Unreadable: true);
        }

        var (list, holder) = found.Value;
        var products = new List<M5aznCatalogProduct>();
        JsonNode? firstUnreadable = null;
        foreach (var node in list)
        {
            if (node is JsonObject item && ParseProduct(item) is { } product) products.Add(product);
            else firstUnreadable ??= node;
        }

        string? problem = null;
        if (firstUnreadable is not null)
        {
            problem = $"{list.Count - products.Count} item(s) on page {page} had no product id and were skipped, for example: {Snippet(firstUnreadable)}";
            _logger.LogWarning("M5azn: {Problem}", problem);
        }

        return new M5aznProductPage(products, HasMorePages(root, holder, page, list.Count), problem);
    }

    // ---------------------------------------------------------------- transport

    /// <summary>Sends with the fixed headers and the seller token, signing in again once if the token is refused.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        if (!IsEnabled) throw new InvalidOperationException("M5azn is not enabled.");

        var token = await GetTokenAsync(ct);
        var response = await _http.SendAsync(Prepare(build(), token), ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _tokens.Clear(token);
            token = await GetTokenAsync(ct);
            response = await _http.SendAsync(Prepare(build(), token), ct);
        }

        // Too many requests: a read is waited out and asked again, as M5azn says or a few seconds.
        // Never an order: that keeps its own rules (a 429 there is "not sent", retried later).
        for (var attempt = 1; attempt <= 2 && response.StatusCode == HttpStatusCode.TooManyRequests; attempt++)
        {
            using var probe = build();
            if (probe.Method != HttpMethod.Get) break;

            var wait = RetryAfter(response, attempt);
            _logger.LogInformation("M5azn: too many requests; waiting {Seconds}s before asking again.", wait.TotalSeconds);
            response.Dispose();
            await Task.Delay(wait, ct);
            response = await _http.SendAsync(Prepare(build(), token), ct);
        }

        return response;
    }

    /// <summary>M5azn's Retry-After when it gives one (at most 30 s), else 3 s then 9 s.</summary>
    internal static TimeSpan RetryAfter(HttpResponseMessage response, int attempt)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta
                   ?? (header?.Date is { } at ? at - DateTimeOffset.UtcNow : (TimeSpan?)null)
                   ?? TimeSpan.FromSeconds(attempt == 1 ? 3 : 9);
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait;
    }

    private HttpRequestMessage Prepare(HttpRequestMessage request, string? token)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Lang", _options.Language);
        request.Headers.TryAddWithoutValidation("X-Website", _options.Website);
        request.Headers.TryAddWithoutValidation("X-Api", _options.ApiVersion);
        request.Headers.TryAddWithoutValidation("X-Device", _options.Device);
        request.Headers.TryAddWithoutValidation("X-Version", _options.AppVersion);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var account = _account.Current;
        var now = _clock.UtcNow;
        if (_tokens.Current(now, account.SessionKey) is { } cached) return cached;

        await _tokens.Gate.WaitAsync(ct);
        try
        {
            if (_tokens.Current(_clock.UtcNow, account.SessionKey) is { } fresh) return fresh;

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(Prepare(new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login")
                {
                    Content = JsonContent.Create(new { email = account.Email, password = account.Password })
                }, token: null), ct);
            }
            catch (HttpRequestException ex)
            {
                throw new M5aznSignInException($"Could not reach M5azn to sign in ({ex.HttpRequestError}).");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new M5aznSignInException("M5azn did not answer the sign-in in time.");
            }

            using (response)
            {
                var root = await ReadJsonAsync(response, ct);
                var token = Text(root?["data"]?["accessToken"]) ?? Text(root?["data"]?["access_token"]);

                if (!response.IsSuccessStatusCode || IsFalse(root?["success"]) || string.IsNullOrWhiteSpace(token))
                {
                    _logger.LogCritical(
                        "M5azn sign-in failed ({Status}): {Message}. Check the seller account on the M5azn page.",
                        (int)response.StatusCode, Text(root?["message"]) ?? "no message");
                    throw new M5aznSignInException($"M5azn sign-in failed ({(int)response.StatusCode}).");
                }

                _tokens.Set(token, ExpiryOf(token, _clock.UtcNow), account.SessionKey);
                return token;
            }
        }
        finally
        {
            _tokens.Gate.Release();
        }
    }

    /// <summary>The token's own expiry less a margin, or a day when it cannot be read.</summary>
    public static DateTime ExpiryOf(string jwt, DateTime now)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length >= 2)
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var node = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                var exp = node?["exp"]?.GetValue<double>();
                if (exp is > 0)
                {
                    var at = DateTime.UnixEpoch.AddSeconds(exp.Value).AddMinutes(-10);
                    if (at > now) return at < now.AddDays(7) ? at : now.AddDays(7);
                }
            }
        }
        catch (Exception)
        {
            // Not a JWT we can read; fall through.
        }

        return now.AddDays(1);
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- reading loose answers

    private static readonly string[] OrderIdKeys = ["order_id", "orderId", "order_number", "m5azn_order_id"];
    private static readonly string[] StatusKeys =
        ["order_status_name", "status_name", "status_text", "order_status", "shipping_status", "status"];
    private static readonly string[] TrackingKeys =
        ["tracking_number", "trackingNumber", "tracking_no", "awb", "awb_number", "airway_bill", "waybill", "shipment_number"];
    private static readonly string[] TrackingUrlKeys = ["tracking_url", "trackingUrl", "tracking_link", "shipment_url"];
    private static readonly string[] CarrierKeys =
        ["shipping_company_name", "shipping_company", "carrier", "courier_name", "courier", "shipping_method"];

    private static JsonNode? Data(JsonNode? root)
    {
        var data = root is JsonObject obj && obj.ContainsKey("data") ? obj["data"] : root;
        if (data is JsonObject { Count: 1 } single && single["order"] is JsonObject order) return order;
        if (data is JsonObject wrapped && wrapped["order"] is JsonObject inner && !wrapped.ContainsKey("status")) return inner;
        return data;
    }

    private static JsonArray? AsArray(JsonNode? node) => node switch
    {
        JsonArray a => a,
        JsonObject o when o["data"] is JsonArray a => a,
        JsonObject o when o["products"] is JsonArray a => a,
        JsonObject o when o["items"] is JsonArray a => a,
        _ => null
    };

    /// <summary>
    /// The first key found, level by level: the object itself first, then objects inside it (never
    /// arrays, whose lines carry their own ids and statuses).
    /// </summary>
    internal static string? Find(JsonNode? node, string[] keys, bool nested)
    {
        if (node is not JsonObject root) return null;

        var level = new List<JsonObject> { root };
        for (var depth = 0; depth < (nested ? 3 : 1) && level.Count > 0; depth++)
        {
            foreach (var key in keys)
            {
                foreach (var obj in level)
                {
                    if (obj.TryGetPropertyValue(key, out var value) && Text(value) is { Length: > 0 } text) return text;
                }
            }

            level = level.SelectMany(o => o.Select(p => p.Value).OfType<JsonObject>()).ToList();
        }

        return null;
    }

    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        JsonObject o => Text(o["name"]) ?? Text(o["title"]),
        _ => null
    };

    private static bool TryQuantity(JsonObject obj, out int quantity)
    {
        var text = Find(obj, ["qty", "quantity", "stock", "available_qty", "available_quantity", "stock_quantity"], false);
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
        {
            quantity = (int)Math.Clamp(Math.Floor(d), int.MinValue, int.MaxValue);
            return true;
        }

        quantity = 0;
        return false;
    }

    // ---------------------------------------------------------------- categories

    public async Task<M5aznCategoryList> GetCategoriesAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "api/v1/catalog/categories"), ct);
        response.EnsureSuccessStatusCode();

        var root = await ReadJsonAsync(response, ct);
        var found = FindList(root, LooksLikeCategory, ["links", "meta", "filters", "products", "brands"]);
        if (found is null)
        {
            _logger.LogWarning("M5azn: the category list came in an unknown shape: {Shape}", Snippet(root));
            return new M5aznCategoryList([], $"M5azn's category list came in a shape DealGo does not know yet: {Snippet(root, 1500)}");
        }

        var categories = new List<M5aznCategory>();
        void Walk(JsonArray list, long? parent, int depth)
        {
            foreach (var node in list.OfType<JsonObject>())
            {
                if (!long.TryParse(Find(node, ["id", "category_id"], false), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                var name = Find(node, CategoryNameKeys, false);
                if (name is null) continue;

                long? parentId = parent;
                if (parentId is null && long.TryParse(Find(node, ["parent_id", "parentId", "parent"], false),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0 && p != id)
                {
                    parentId = p;
                }

                var image = Find(node, ["image", "icon", "image_url", "photo", "thumbnail", "banner"], false);
                categories.Add(new M5aznCategory(id, name, parentId, image is null ? null : Absolute(image)));

                if (depth < 4)
                {
                    foreach (var key in new[] { "children", "sub_categories", "subcategories", "subCategories", "childs", "sub" })
                    {
                        if (node[key] is JsonArray children) Walk(children, id, depth + 1);
                    }
                }
            }
        }

        Walk(found.Value.List, null, 0);
        return new M5aznCategoryList(categories.DistinctBy(c => c.Id).ToList(), null);
    }

    // Arabic first: the store is shown in Arabic.
    private static readonly string[] CategoryNameKeys = ["name_ar", "name", "title_ar", "title", "name_en", "title_en"];

    private static bool LooksLikeCategory(JsonObject o) =>
        Find(o, ["id", "category_id"], false) is not null && Find(o, CategoryNameKeys, false) is not null;

    // ---------------------------------------------------------------- products

    private static readonly string[] IdKeys = ["product_id", "productId", "id"];
    private static readonly string[] SkuKeys = ["sku", "product_sku", "barcode"];
    private static readonly string[] NameKeys = ["name", "title", "product_name", "name_ar", "name_en"];
    private static readonly string[] DescriptionKeys =
        ["description", "long_description", "details", "desc", "description_ar", "description_en", "short_description"];

    // What the seller pays M5azn comes first; the plain price is the fallback (docs/M5AZN.md).
    private static readonly string[] PriceKeys = ["seller_price", "wholesale_price", "cost_price", "price", "sale_price"];
    private static readonly string[] ImageListKeys = ["images", "gallery", "product_images", "photos", "pictures", "media"];
    private static readonly string[] ImageKeys = ["image", "main_image", "image_url", "thumbnail", "cover", "photo"];
    private static readonly string[] ImageUrlKeys = ["url", "image", "src", "path", "original", "large", "image_url", "full"];
    private static readonly string[] CategoryKeys = ["category_name", "category", "sub_category", "main_category", "category_title"];
    private static readonly string[] DeliveryKeys =
        ["delivery_time", "shipping_time", "delivery_days", "delivery_period", "delivery_duration", "estimated_delivery"];

    /// <summary>
    /// One product of a list or a details answer. A seller's list row may carry the product inside
    /// it (<c>{ id, price, product: { ... } }</c>), so both are read: the row first, then the
    /// product. Null when no product id can be found.
    /// </summary>
    internal M5aznCatalogProduct? ParseProduct(JsonObject item)
    {
        var sources = new List<JsonObject> { item };
        if (item["product"] is JsonObject inner) sources.Add(inner);

        string? First(string[] keys) => sources.Select(src => Find(src, keys, nested: false)).FirstOrDefault(v => v is not null);

        var idText = Find(item, ["product_id", "productId"], false)
                     ?? (sources.Count > 1 ? Find(sources[1], ["id"], false) : null)
                     ?? Find(item, ["id"], false);
        if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) return null;

        var sku = First(SkuKeys);
        if (sku is null)
        {
            var variants = sources.Select(src => AsArray(src["variants"]) ?? AsArray(src["skus"])).FirstOrDefault(v => v is { Count: > 0 });
            sku = variants?.OfType<JsonObject>().Select(v => Find(v, SkuKeys, false)).FirstOrDefault(v => v is not null);
        }

        decimal? price = decimal.TryParse(First(PriceKeys), NumberStyles.Number, CultureInfo.InvariantCulture, out var pr) ? pr : null;

        int? quantity = null;
        foreach (var src in sources)
        {
            if (TryQuantity(src, out var q)) { quantity = q; break; }
        }

        // A category may come as a name, as an object ({ id, name_ar, parent }), or only as an id.
        string? category = null, parentCategory = null;
        long? categoryId = null;
        foreach (var src in sources)
        {
            foreach (var key in CategoryKeys.Append("categories"))
            {
                var value = src[key] is JsonArray arr ? arr.FirstOrDefault() : src[key];
                switch (value)
                {
                    case JsonObject obj:
                        category ??= Find(obj, CategoryNameKeys, false);
                        if (categoryId is null && long.TryParse(Find(obj, ["id", "category_id"], false), out var oid)) categoryId = oid;
                        if (obj["parent"] is JsonObject parent) parentCategory ??= Find(parent, CategoryNameKeys, false);
                        break;
                    case JsonValue v when v.TryGetValue<long>(out var numeric):
                        categoryId ??= numeric;
                        break;
                    case JsonValue v when Text(v) is { } text:
                        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var textId)) categoryId ??= textId;
                        else category ??= text;
                        break;
                }
            }

            if (categoryId is null && long.TryParse(Find(src, ["category_id", "categoryId", "sub_category_id", "main_category_id"], false),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var cid) && cid > 0)
            {
                categoryId = cid;
            }

            if (category is not null) break;
        }

        return new M5aznCatalogProduct(
            id,
            sku,
            First(NameKeys),
            PlainText(First(DescriptionKeys)),
            price,
            quantity,
            Images(sources),
            category,
            First(DeliveryKeys),
            categoryId,
            parentCategory);
    }

    /// <summary>Every picture of the product, main one first, without repeats; at most twelve.</summary>
    private IReadOnlyList<string> Images(IEnumerable<JsonObject> sources)
    {
        var urls = new List<string>();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var url = Absolute(value.Trim());
            if (url is not null && !urls.Contains(url, StringComparer.OrdinalIgnoreCase)) urls.Add(url);
        }

        var all = sources.ToList();
        foreach (var src in all) Add(Find(src, ImageKeys, false));

        foreach (var src in all)
        {
            foreach (var key in ImageListKeys)
            {
                if (src[key] is not JsonArray list) continue;
                foreach (var entry in list)
                {
                    Add(entry is JsonObject o ? Find(o, ImageUrlKeys, false) : Text(entry));
                }
            }
        }

        return urls.Take(12).ToList();
    }

    /// <summary>An http(s) address as it is; a path made absolute against M5azn's address; anything else dropped.</summary>
    private string? Absolute(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var abs))
        {
            return abs.Scheme is "http" or "https" ? abs.ToString() : null;
        }

        if (value.StartsWith("//", StringComparison.Ordinal)) return "https:" + value;
        return _http.BaseAddress is { } baseUri && Uri.TryCreate(baseUri, value, out var rel) ? rel.ToString() : null;
    }

    /// <summary>Descriptions often come as HTML; the app shows text, so tags go and paragraphs become lines.</summary>
    internal static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<\s*(br|/p|/div|/li|/h\d)\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');   // &nbsp; is a space to the reader
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\n\s*", "\n").Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// The first list of objects that look like products, searched a few levels down, and the object
    /// holding it (where a paginator keeps <c>last_page</c> and the like). M5azn's seller list is
    /// <c>{ data: { records: { current_page, data: [ ... ] } } }</c>; other wrappers are found the same way.
    /// </summary>
    internal static (JsonArray List, JsonObject? Holder)? FindProductList(JsonNode? root) =>
        FindList(root, LooksLikeProduct, ["categories", "brands", "links", "meta", "filters"]);

    /// <summary>The first non-empty list whose items look like <paramref name="looksRight"/>, and the object holding it.</summary>
    internal static (JsonArray List, JsonObject? Holder)? FindList(
        JsonNode? root, Func<JsonObject, bool> looksRight, string[]? skip = null)
    {
        bool IsList(JsonArray a) => a.Count > 0 && a.Any(e => e is JsonObject eo && looksRight(eo));

        if (root is JsonArray top && IsList(top)) return (top, null);

        // Lists named like products first, so a "categories" or "brands" list is never taken for them.
        string[] preferred = ["data", "records", "products", "items", "product_list", "list", "result", "rows"];
        string[] never = skip ?? ["links", "meta", "filters"];

        var level = new List<JsonObject>();
        if (root is JsonObject r) level.Add(r);

        for (var depth = 0; depth < 6 && level.Count > 0; depth++)
        {
            var next = new List<JsonObject>();
            foreach (var holder in level)
            {
                var keys = preferred.Where(holder.ContainsKey)
                    .Concat(holder.Select(kv => kv.Key).Where(k => !preferred.Contains(k) && !never.Contains(k)));

                foreach (var key in keys)
                {
                    switch (holder[key])
                    {
                        case JsonArray a when IsList(a):
                            return (a, holder);
                        case JsonObject o:
                            next.Add(o);
                            break;
                    }
                }
            }

            level = next;
        }

        return null;
    }

    private static bool LooksLikeProduct(JsonObject o) =>
        Find(o, IdKeys, false) is not null && (Find(o, NameKeys, false) is not null || o["product"] is JsonObject
                                               || Find(o, SkuKeys, false) is not null);

    /// <summary>
    /// An empty list where the products would be (<c>data: []</c>, <c>data.records.data: []</c>, ...):
    /// a shop with nothing in it, which is not the same as an answer that cannot be read.
    /// </summary>
    private static (JsonArray List, JsonObject? Holder)? EmptyProductList(JsonNode? root)
    {
        if (root is JsonArray { Count: 0 } top) return (top, null);

        JsonObject? node = root as JsonObject;
        for (var depth = 0; depth < 4 && node is not null; depth++)
        {
            foreach (var key in new[] { "data", "records", "products", "items" })
            {
                if (node[key] is JsonArray { Count: 0 } empty) return (empty, node);
            }

            node = node["data"] as JsonObject ?? node["records"] as JsonObject;
        }

        return null;
    }

    /// <summary>Whether a later page exists, from the usual paging fields. None of them: one page only.</summary>
    private static bool HasMorePages(JsonNode? root, JsonObject? listHolder, int page, int count)
    {
        if (count == 0) return false;

        foreach (var holder in new[] { listHolder, root, root?["data"], root?["data"]?["records"], root?["meta"], root?["data"]?["meta"], root?["pagination"], root?["data"]?["pagination"] })
        {
            if (holder is not JsonObject h) continue;

            if (int.TryParse(Find(h, ["last_page", "total_pages", "lastPage", "pages"], false), out var last)) return page < last;

            if (h.ContainsKey("next_page_url")) return Text(h["next_page_url"]) is not null;
            if (h["links"] is JsonObject links && links.ContainsKey("next")) return Text(links["next"]) is not null;
        }

        return false;
    }

    private static bool IsFalse(JsonNode? node) =>
        node is JsonValue v && ((v.TryGetValue<bool>(out var b) && !b) || (v.TryGetValue<string>(out var s) && s == "false"));

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue v && ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<string>(out var s) && s == "true"));

    private static string Snippet(JsonNode? root, int max = 300)
    {
        var text = root?.ToJsonString() ?? "no body";
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>M5azn's example time has no zone; a Saudi supplier reads it as Saudi time.</summary>
    internal static string ToRiyadhText(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Riyadh)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static TimeZoneInfo FindRiyadh()
    {
        foreach (var id in new[] { "Asia/Riyadh", "Arab Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.CreateCustomTimeZone("Riyadh", TimeSpan.FromHours(3), "Riyadh", "Riyadh");
    }

    private sealed class M5aznSignInException(string message) : Exception(message);
}
