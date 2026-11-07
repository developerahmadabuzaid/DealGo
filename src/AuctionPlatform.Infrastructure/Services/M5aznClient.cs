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

    public async Task<M5aznProductInfo?> GetProductAsync(long productId, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"api/v1/product/details/{productId}"), ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var root = await ReadJsonAsync(response, ct);
        if (root is null || IsFalse(root["success"])) return null;
        var data = Data(root);
        if (data is JsonObject { Count: 1 } wrapper && wrapper["product"] is JsonObject inner) data = inner;
        if (data is not JsonObject product) return null;

        // What the seller pays M5azn comes first; the plain price is the fallback (docs/M5AZN.md).
        decimal? price = decimal.TryParse(Find(product, ["seller_price", "wholesale_price", "cost_price", "price", "sale_price"], false),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : null;
        int? quantity = TryQuantity(product, out var q) ? q : null;

        return new M5aznProductInfo(
            productId,
            Find(product, ["name", "title", "name_ar", "name_en"], false),
            Find(product, ["sku"], false),
            price,
            quantity,
            Find(product, ["image", "thumbnail", "image_url", "main_image"], false));
    }

    // ---------------------------------------------------------------- transport

    /// <summary>Sends with the fixed headers and the seller token, signing in again once if the token is refused.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        if (!IsEnabled) throw new InvalidOperationException("M5azn is not enabled.");

        var token = await GetTokenAsync(ct);
        var response = await _http.SendAsync(Prepare(build(), token), ct);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        _tokens.Clear(token);
        token = await GetTokenAsync(ct);
        return await _http.SendAsync(Prepare(build(), token), ct);
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

    private static bool IsFalse(JsonNode? node) =>
        node is JsonValue v && ((v.TryGetValue<bool>(out var b) && !b) || (v.TryGetValue<string>(out var s) && s == "false"));

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue v && ((v.TryGetValue<bool>(out var b) && b) || (v.TryGetValue<string>(out var s) && s == "true"));

    private static string Snippet(JsonNode? root)
    {
        var text = root?.ToJsonString() ?? "no body";
        return text.Length <= 300 ? text : text[..300] + "…";
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
