using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>A payment Paymob has opened for us: where to send the customer to pay it.</summary>
public sealed record PaymobCheckout(string IntentionId, long OrderId, string CheckoutUrl);

public sealed record PaymobCustomer(string FirstName, string LastName, string Phone, string Email);

public interface IPaymobClient
{
    bool IsEnabled { get; }

    /// <summary>Whether the server can ask Paymob about a transaction (needs the API key).</summary>
    bool CanInquire { get; }

    /// <summary>
    /// Opens a payment for <paramref name="amount"/> riyals with Paymob's intention API and
    /// returns its Paymob order id and the Unified Checkout page for it.
    /// </summary>
    Task<PaymobCheckout> CreateCheckoutAsync(
        decimal amount,
        string currency,
        string reference,
        string description,
        PaymobCustomer customer,
        string notificationUrl,
        string redirectionUrl,
        CancellationToken ct = default);

    /// <summary>
    /// Asks Paymob, server to server, for the latest transaction on a Paymob order. Null when the
    /// order has no transaction yet (the customer has not paid). The answer comes from Paymob's
    /// API over TLS with our credentials, so unlike a browser redirect it can be acted on.
    /// </summary>
    Task<JsonElement?> InquireByOrderAsync(long orderId, CancellationToken ct = default);
}

/// <summary>
/// Paymob's KSA APIs, as its developer documentation describes them:
///   * POST {BaseUrl}/v1/intention/ with "Authorization: Token &lt;secret key&gt;"; amounts in cents
///     (halalas); returns the intention id, the Paymob order id (intention_order_id) and the
///     client secret.
///   * The customer pays on {CheckoutUrl}/?publicKey=…&amp;clientSecret=… (Unified Checkout).
///   * POST {BaseUrl}/api/auth/tokens {api_key} gives a token for
///     POST {BaseUrl}/api/ecommerce/orders/transaction_inquiry {auth_token, order_id}.
///   * Callbacks carry an HMAC-SHA512 of a fixed list of fields (see <see cref="SignedFields"/>).
/// The secret key, HMAC secret and API key never leave the server and are never logged.
/// </summary>
public sealed class PaymobClient : IPaymobClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Auth tokens are reused for a while rather than requested per inquiry. The typed client is
    // created per request, so the cache is shared across instances.
    private static readonly SemaphoreSlim TokenGate = new(1, 1);
    private static string? _token;
    private static DateTime _tokenExpiresAt;

    private readonly HttpClient _http;
    private readonly PaymobOptions _options;
    private readonly ILogger<PaymobClient> _logger;

    public PaymobClient(HttpClient http, IOptions<PaymobOptions> options, ILogger<PaymobClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = http;

        _http.BaseAddress ??= new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public bool IsEnabled => _options.IsConfigured;
    public bool CanInquire => _options.CanInquire;

    public async Task<PaymobCheckout> CreateCheckoutAsync(
        decimal amount,
        string currency,
        string reference,
        string description,
        PaymobCustomer customer,
        string notificationUrl,
        string redirectionUrl,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            throw new DomainException(ErrorCodes.ServiceUnavailable, "Online payment is not set up.");
        }

        var halalas = ToHalalas(amount);

        var body = new IntentionRequest(
            Amount: halalas,
            Currency: currency,
            PaymentMethods: _options.IntegrationIds,
            Items: [new IntentionItem(Cut(description, 50), halalas, Cut(description, 255), 1)],
            BillingData: new BillingData(
                FirstName: Cut(customer.FirstName, 50),
                LastName: Cut(customer.LastName, 50),
                PhoneNumber: customer.Phone,
                Email: customer.Email,
                Country: "SA"),
            Customer: new IntentionCustomer(Cut(customer.FirstName, 50), Cut(customer.LastName, 50), customer.Email),
            SpecialReference: reference,
            Expiration: _options.IntentionExpirySeconds,
            NotificationUrl: notificationUrl,
            RedirectionUrl: redirectionUrl,
            Extras: new Dictionary<string, string> { ["payment_id"] = reference });

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/intention/")
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", _options.SecretKey);

        using var response = await SendAsync(request, $"open payment {reference}", ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Paymob's error bodies describe the request (a missing field, an unknown integration
            // id), never a card. Logged so a misconfiguration can be read and fixed.
            _logger.LogError("Paymob refused to open payment {Reference}: {Status} {Body}",
                reference, (int)response.StatusCode, Truncate(text));
            throw new DomainException(ErrorCodes.ServiceUnavailable, "The payment service refused the request.");
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var clientSecret = root.TryGetProperty("client_secret", out var cs) ? cs.GetString() : null;
        var intentionId = root.TryGetProperty("id", out var id) ? id.ToString() : string.Empty;
        var orderId = root.TryGetProperty("intention_order_id", out var oid) && oid.TryGetInt64(out var o) ? o : 0;

        if (string.IsNullOrWhiteSpace(clientSecret) || orderId <= 0)
        {
            _logger.LogError("Paymob opened payment {Reference} without a client secret or order id.", reference);
            throw new DomainException(ErrorCodes.ServiceUnavailable, "The payment service answered oddly.");
        }

        // Paymob says whether each payment method is live. One that disagrees with the configured
        // mode means test keys on a live site or live keys on a test one: refuse before the
        // customer can pay.
        if (root.TryGetProperty("payment_methods", out var methods) && methods.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in methods.EnumerateArray())
            {
                if (m.TryGetProperty("live", out var live) &&
                    live.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    live.GetBoolean() != _options.IsLiveMode)
                {
                    _logger.LogCritical(
                        "Paymob payment methods are {Actual} but Paymob:Mode is {Mode}; refusing payment {Reference}.",
                        live.GetBoolean() ? "live" : "test", _options.Mode, reference);
                    throw new DomainException(ErrorCodes.ServiceUnavailable, "Online payment is misconfigured.");
                }
            }
        }

        var checkout = $"{_options.CheckoutUrl.TrimEnd('/')}/" +
                       $"?publicKey={Uri.EscapeDataString(_options.PublicKey)}" +
                       $"&clientSecret={Uri.EscapeDataString(clientSecret)}";

        _logger.LogInformation("Paymob opened payment {Reference} as intention {IntentionId}, order {OrderId}.",
            reference, intentionId, orderId);

        return new PaymobCheckout(intentionId, orderId, checkout);
    }

    public async Task<JsonElement?> InquireByOrderAsync(long orderId, CancellationToken ct = default)
    {
        if (!CanInquire) return null;

        var token = await AuthTokenAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/ecommerce/orders/transaction_inquiry")
        {
            Content = JsonContent.Create(new { auth_token = token, order_id = orderId })
        };
        using var response = await SendAsync(request, $"inquire about order {orderId}", ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if ((int)response.StatusCode == 404) return null;
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Paymob inquiry for order {OrderId} failed: {Status} {Body}",
                orderId, (int)response.StatusCode, Truncate(text));
            throw new DomainException(ErrorCodes.ServiceUnavailable, "The payment service did not answer the inquiry.");
        }

        using var doc = JsonDocument.Parse(text);
        // A transaction has an id and an amount; anything else ("no transactions yet") is no answer.
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty("id", out _) &&
               doc.RootElement.TryGetProperty("amount_cents", out _)
            ? doc.RootElement.Clone()
            : null;
    }

    private async Task<string> AuthTokenAsync(CancellationToken ct)
    {
        await TokenGate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpiresAt) return _token;

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/tokens")
            {
                Content = JsonContent.Create(new { api_key = _options.ApiKey })
            };
            using var response = await SendAsync(request, "get an auth token", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Paymob refused an auth token: {Status}", (int)response.StatusCode);
                throw new DomainException(ErrorCodes.ServiceUnavailable, "The payment service refused our credentials.");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _token = doc.RootElement.GetProperty("token").GetString()
                     ?? throw new DomainException(ErrorCodes.ServiceUnavailable, "No auth token returned.");
            // Paymob tokens last about an hour; renew well before.
            _tokenExpiresAt = DateTime.UtcNow.AddMinutes(45);
            return _token;
        }
        finally
        {
            TokenGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Paymob could not be reached to {What}.", what);
            throw new DomainException(ErrorCodes.ServiceUnavailable, "The payment service is not answering.");
        }
    }

    public static long ToHalalas(decimal amount) =>
        (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    // ------------------------------------------------------------------ callback signatures

    /// <summary>
    /// The transaction fields Paymob signs, in the order its documentation lists them ("HMAC
    /// Transaction Callback"). The HMAC is the SHA-512 HMAC of their values joined with nothing
    /// between, keyed with the account's HMAC secret, sent as the <c>hmac</c> query parameter.
    /// </summary>
    public static readonly string[] SignedFields =
    [
        "amount_cents", "created_at", "currency", "error_occured", "has_parent_transaction", "id",
        "integration_id", "is_3d_secure", "is_auth", "is_capture", "is_refunded",
        "is_standalone_payment", "is_voided", "order.id", "owner", "pending",
        "source_data.pan", "source_data.sub_type", "source_data.type", "success"
    ];

    /// <summary>The HMAC of a transaction as JSON: the processed callback's "obj", or an inquiry.</summary>
    public static string ComputeHmac(JsonElement transaction, string secret) =>
        Sign(SignedFields.Select(f => ValueAt(transaction, f)), secret);

    /// <summary>
    /// The HMAC of the response callback (the customer's return), whose fields are query
    /// parameters. Paymob's list names the order id "order_id" there; its sample sends "order".
    /// Either is accepted.
    /// </summary>
    public static string ComputeHmac(IReadOnlyDictionary<string, string> query, string secret) =>
        Sign(SignedFields.Select(f => f == "order.id"
                ? (query.TryGetValue("order_id", out var oid) ? oid : query.TryGetValue("order", out var o) ? o : string.Empty)
                : query.TryGetValue(f, out var v) ? v : string.Empty),
            secret);

    public static bool HmacMatches(string computed, string? provided) =>
        !string.IsNullOrWhiteSpace(provided) &&
        provided.Trim().Length == computed.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(provided.Trim().ToLowerInvariant()));

    private static string Sign(IEnumerable<string> values, string secret)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(string.Concat(values)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>A field of the transaction as Paymob writes it into the signed string.</summary>
    private static string ValueAt(JsonElement element, string path)
    {
        var current = element;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return string.Empty;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => current.GetString() ?? string.Empty,
            _ => current.GetRawText()
        };
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];

    // ------------------------------------------------------------------ request shapes

    private sealed record IntentionRequest(
        long Amount,
        string Currency,
        int[] PaymentMethods,
        IntentionItem[] Items,
        BillingData BillingData,
        IntentionCustomer Customer,
        string SpecialReference,
        int Expiration,
        string NotificationUrl,
        string RedirectionUrl,
        Dictionary<string, string> Extras);

    private sealed record IntentionItem(string Name, long Amount, string Description, int Quantity);

    private sealed record BillingData(
        string FirstName, string LastName, string PhoneNumber, string Email, string Country);

    private sealed record IntentionCustomer(string FirstName, string LastName, string Email);
}

/// <summary>Formats amounts for log lines and errors without the culture's separators.</summary>
internal static class PaymobFormat
{
    public static string Riyals(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
