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
public sealed record PaymobCheckout(string IntentionId, long? OrderId, string CheckoutUrl);

public sealed record PaymobCustomer(string FirstName, string LastName, string Phone, string Email);

public interface IPaymobClient
{
    bool IsEnabled { get; }

    /// <summary>
    /// Opens a payment for <paramref name="amount"/> riyals with Paymob's intention API and
    /// returns the address of Paymob's checkout page for it. <paramref name="reference"/> comes
    /// back on every callback for the payment, which is how a callback finds its top-up.
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
}

/// <summary>
/// Paymob's Saudi API (ksa.paymob.com): the unified intention API to open a payment, and its
/// unified checkout page to take it.
///
/// Amounts go to Paymob in halalas. The secret key is sent as "Token &lt;key&gt;"; it never leaves
/// the server. The public key is not a secret: it is part of the checkout address.
/// </summary>
public sealed class PaymobClient : IPaymobClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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

        if (_options.IsConfigured)
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Token", _options.SecretKey);
        }
    }

    public bool IsEnabled => _options.IsConfigured;

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
            Items: [new IntentionItem(description, halalas, description, 1)],
            BillingData: new BillingData(
                FirstName: customer.FirstName,
                LastName: customer.LastName,
                PhoneNumber: customer.Phone,
                Email: customer.Email,
                Country: "SA"),
            Customer: new IntentionCustomer(customer.FirstName, customer.LastName, customer.Email),
            SpecialReference: reference,
            NotificationUrl: notificationUrl,
            RedirectionUrl: redirectionUrl,
            Extras: new Dictionary<string, string> { ["payment_id"] = reference });

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("v1/intention/", body, Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Paymob could not be reached to open payment {Reference}.", reference);
            throw new DomainException(ErrorCodes.ServiceUnavailable, "The payment service is not answering.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // Paymob's error bodies describe the request, not the card; nothing secret in them.
                _logger.LogError(
                    "Paymob refused to open payment {Reference}: {Status} {Body}",
                    reference, (int)response.StatusCode, Truncate(text));
                throw new DomainException(ErrorCodes.ServiceUnavailable, "The payment service refused the request.");
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var clientSecret = root.TryGetProperty("client_secret", out var cs) ? cs.GetString() : null;
            var intentionId = root.TryGetProperty("id", out var id) ? id.ToString() : string.Empty;
            long? orderId = root.TryGetProperty("intention_order_id", out var oid) && oid.TryGetInt64(out var o)
                ? o
                : null;

            if (string.IsNullOrWhiteSpace(clientSecret))
            {
                _logger.LogError("Paymob opened payment {Reference} but sent no client secret.", reference);
                throw new DomainException(ErrorCodes.ServiceUnavailable, "The payment service answered oddly.");
            }

            var checkout = $"{_options.BaseUrl.TrimEnd('/')}/unifiedcheckout/" +
                           $"?publicKey={Uri.EscapeDataString(_options.PublicKey)}" +
                           $"&clientSecret={Uri.EscapeDataString(clientSecret)}";

            _logger.LogInformation(
                "Paymob opened payment {Reference} as intention {IntentionId}, order {OrderId}.",
                reference, intentionId, orderId);

            return new PaymobCheckout(intentionId, orderId, checkout);
        }
    }

    public static long ToHalalas(decimal amount) =>
        (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    // ------------------------------------------------------------------ callback signatures

    /// <summary>
    /// The transaction fields Paymob signs, in the order it concatenates them. Paymob sends the
    /// HMAC-SHA512 of their values, joined with nothing between, keyed with the account's HMAC
    /// secret, as the <c>hmac</c> query parameter.
    /// </summary>
    public static readonly string[] SignedFields =
    [
        "amount_cents", "created_at", "currency", "error_occured", "has_parent_transaction", "id",
        "integration_id", "is_3d_secure", "is_auth", "is_capture", "is_refunded",
        "is_standalone_payment", "is_voided", "order.id", "owner", "pending",
        "source_data.pan", "source_data.sub_type", "source_data.type", "success"
    ];

    /// <summary>The HMAC of a server-to-server callback, whose fields are in the JSON "obj".</summary>
    public static string ComputeHmac(JsonElement transaction, string secret) =>
        Sign(SignedFields.Select(f => ValueAt(transaction, f)), secret);

    /// <summary>
    /// The HMAC of the customer's return to the site, whose fields are query parameters. The
    /// order id arrives as "order" there rather than "order.id".
    /// </summary>
    public static string ComputeHmac(IReadOnlyDictionary<string, string> query, string secret) =>
        Sign(SignedFields.Select(f => query.TryGetValue(f == "order.id" ? "order" : f, out var v) ? v : string.Empty),
            secret);

    public static bool HmacMatches(string computed, string? provided) =>
        !string.IsNullOrWhiteSpace(provided) &&
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
            JsonValueKind.Number => current.GetRawText(),
            _ => current.GetRawText()
        };
    }

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
