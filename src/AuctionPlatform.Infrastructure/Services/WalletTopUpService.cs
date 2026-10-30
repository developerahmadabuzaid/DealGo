using System.Text.Json;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Services;

public sealed record TopUpStartDto(Guid PaymentId, decimal Amount, string Currency, string CheckoutUrl);

public sealed record TopUpStatusDto(
    Guid PaymentId, decimal Amount, string Currency, PaymentStatus Status, string? FailureReason);

/// <summary>What the customer's return to the site said, once its signature checked out.</summary>
public sealed record TopUpReturn(bool Valid, bool Succeeded, Guid? PaymentId);

public interface IWalletTopUpService
{
    /// <summary>Opens a Paymob payment for a top-up of the Main wallet.</summary>
    Task<TopUpStartDto> StartAsync(Guid userId, decimal amount, string siteBaseUrl, CancellationToken ct = default);

    Task<TopUpStatusDto> GetAsync(Guid userId, Guid paymentId, CancellationToken ct = default);

    /// <summary>Paymob's server-to-server "transaction processed" callback.</summary>
    Task<WebhookResult> HandleCallbackAsync(string rawBody, string? hmac, CancellationToken ct = default);

    /// <summary>The customer coming back from Paymob's checkout, with the result in the query.</summary>
    Task<TopUpReturn> HandleReturnAsync(IReadOnlyDictionary<string, string> query, CancellationToken ct = default);
}

/// <summary>
/// Topping up the Main wallet with a card, mada or Apple Pay through Paymob.
///
/// The order of things is what keeps money safe:
///   1. A Pending payment is recorded first, and its id is the reference Paymob carries.
///   2. The customer pays on Paymob's own page; card details never reach this server.
///   3. Paymob calls back, signed. Only a correctly signed, successful, non-pending transaction
///      whose amount and currency match the recorded payment credits the wallet.
///   4. The credit is keyed by the payment, so the server callback and the customer's return —
///      which carry the same result and may arrive in either order, or both — credit it once.
/// Nothing the app sends can mark a top-up paid; only Paymob's signature can.
/// </summary>
public sealed class WalletTopUpService : IWalletTopUpService
{
    public const string Provider = "PAYMOB";

    private readonly ApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IPaymobClient _paymob;
    private readonly IDateTimeProvider _clock;
    private readonly PaymobOptions _options;
    private readonly ILogger<WalletTopUpService> _logger;

    public WalletTopUpService(
        ApplicationDbContext db,
        IWalletService wallets,
        IPaymobClient paymob,
        IDateTimeProvider clock,
        IOptions<PaymobOptions> options,
        ILogger<WalletTopUpService> logger)
    {
        _db = db;
        _wallets = wallets;
        _paymob = paymob;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TopUpStartDto> StartAsync(
        Guid userId, decimal amount, string siteBaseUrl, CancellationToken ct = default)
    {
        if (!_paymob.IsEnabled)
        {
            throw new DomainException(ErrorCodes.ServiceUnavailable, "Online payment is not set up yet.");
        }

        amount = Money.Round(amount);
        if (amount < _options.MinTopUp || amount > _options.MaxTopUp)
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed,
                $"A top-up must be between {PaymobFormat.Riyals(_options.MinTopUp)} and {PaymobFormat.Riyals(_options.MaxTopUp)} SAR.");
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");

        var payment = new Payment
        {
            UserId = userId,
            PaymentType = PaymentType.Deposit,
            Amount = amount,
            Currency = Money.DefaultCurrency,
            Provider = Provider,
            Status = PaymentStatus.Pending,
            CreatedAt = _clock.UtcNow
        };
        payment.IdempotencyKey = $"paymob-topup:{payment.Id:N}";
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);

        var baseUrl = (string.IsNullOrWhiteSpace(_options.CallbackBaseUrl) ? siteBaseUrl : _options.CallbackBaseUrl)
            .TrimEnd('/');

        PaymobCheckout checkout;
        try
        {
            checkout = await _paymob.CreateCheckoutAsync(
                amount,
                payment.Currency,
                reference: payment.Id.ToString("N"),
                description: "شحن محفظة ديلقو",
                customer: CustomerFor(user),
                notificationUrl: $"{baseUrl}/api/v1/webhooks/paymob",
                redirectionUrl: $"{baseUrl}/api/v1/payments/paymob/return",
                ct);
        }
        catch
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = "Paymob did not open the payment.";
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        // Kept so a callback that brings only Paymob's order id still finds this payment.
        payment.Metadata = checkout.OrderId is { } orderId ? OrderMarker(orderId) : null;
        payment.Status = PaymentStatus.Processing;
        await _db.SaveChangesAsync(ct);

        return new TopUpStartDto(payment.Id, payment.Amount, payment.Currency, checkout.CheckoutUrl);
    }

    public async Task<TopUpStatusDto> GetAsync(Guid userId, Guid paymentId, CancellationToken ct = default)
    {
        var p = await _db.Payments.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == paymentId && x.UserId == userId && x.Provider == Provider, ct)
                ?? throw new DomainException(ErrorCodes.NotFound, "Top-up not found.");

        return new TopUpStatusDto(p.Id, p.Amount, p.Currency, p.Status, p.FailureReason);
    }

    public async Task<WebhookResult> HandleCallbackAsync(string rawBody, string? hmac, CancellationToken ct = default)
    {
        if (!_paymob.IsEnabled) return new WebhookResult(false, false, "Paymob is not configured.");

        JsonElement transaction;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            // Only transaction callbacks move money; Paymob also sends others (tokens, for one).
            if (root.TryGetProperty("type", out var type) &&
                !string.Equals(type.GetString(), "TRANSACTION", StringComparison.OrdinalIgnoreCase))
            {
                return new WebhookResult(true, false, "Not a transaction.");
            }

            transaction = (root.TryGetProperty("obj", out var obj) ? obj : root).Clone();
        }
        catch (JsonException)
        {
            return new WebhookResult(false, false, "Malformed payload.");
        }

        if (!PaymobClient.HmacMatches(PaymobClient.ComputeHmac(transaction, _options.HmacSecret), hmac))
        {
            _logger.LogWarning("Rejected a Paymob callback with a wrong signature.");
            return new WebhookResult(false, false, "Invalid signature.");
        }

        var info = TransactionInfo.FromJson(transaction);

        // Recorded first, keyed by the transaction and its state, so a redelivery is recognised and
        // a later "pending → succeeded" for the same transaction is still applied.
        var record = new PaymentWebhookEvent
        {
            Provider = Provider,
            ProviderEventId = $"{info.TransactionId}:{info.Success}:{info.Pending}",
            ProviderTransactionId = info.TransactionId,
            Payload = rawBody,
            Signature = hmac,
            SignatureValid = true,
            ReceivedAt = _clock.UtcNow
        };
        _db.PaymentWebhookEvents.Add(record);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.Entry(record).State = EntityState.Detached;
            return new WebhookResult(true, true, "Already processed.");
        }

        await ApplyAsync(info, "callback", ct);

        record.Processed = true;
        record.ProcessedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new WebhookResult(true, false, null);
    }

    public async Task<TopUpReturn> HandleReturnAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken ct = default)
    {
        if (!_paymob.IsEnabled) return new TopUpReturn(false, false, null);

        query.TryGetValue("hmac", out var hmac);
        if (!PaymobClient.HmacMatches(PaymobClient.ComputeHmac(query, _options.HmacSecret), hmac))
        {
            _logger.LogWarning("A return from Paymob carried a wrong signature; nothing applied.");
            return new TopUpReturn(false, false, null);
        }

        var info = TransactionInfo.FromQuery(query);
        var payment = await ApplyAsync(info, "return", ct);
        return new TopUpReturn(true, info.Success && !info.Pending, payment?.Id);
    }

    /// <summary>Applies a verified transaction result to its top-up, at most once.</summary>
    private async Task<Payment?> ApplyAsync(TransactionInfo info, string source, CancellationToken ct)
    {
        var payment = await FindAsync(info, ct);
        if (payment is null)
        {
            _logger.LogWarning(
                "Paymob {Source} for transaction {TransactionId} (order {OrderId}, reference {Reference}) matches no top-up.",
                source, info.TransactionId, info.OrderId, info.Reference);
            return null;
        }

        if (payment.Status == PaymentStatus.Completed) return payment;

        if (info.Pending) return payment;

        if (!info.Success || info.Voided || info.Refunded)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = string.IsNullOrWhiteSpace(info.Message) ? "Payment declined." : info.Message;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Top-up {PaymentId} failed at Paymob: {Reason}", payment.Id, payment.FailureReason);
            return payment;
        }

        // The amount Paymob took must be the amount recorded. A mismatch is never credited: it means
        // the payment was tampered with or something is badly wrong, and a person must look.
        if (info.AmountCents != PaymobClient.ToHalalas(payment.Amount) ||
            !string.Equals(info.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
        {
            payment.FailureReason =
                $"Paymob reported {info.AmountCents} {info.Currency} for a top-up of {PaymobFormat.Riyals(payment.Amount)} {payment.Currency}.";
            await _db.SaveChangesAsync(ct);
            _logger.LogCritical("Top-up {PaymentId} not credited: {Reason}", payment.Id, payment.FailureReason);
            return payment;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var credit = await _wallets.CreditAsync(new WalletMovement
        {
            UserId = payment.UserId,
            WalletType = WalletType.Main,
            Amount = payment.Amount,
            Currency = payment.Currency,
            TransactionType = WalletTransactionType.Deposit,
            ReferenceType = "Payment",
            ReferenceId = payment.Id,
            Description = "شحن المحفظة عبر Paymob",
            IdempotencyKey = payment.IdempotencyKey
        }, ct);

        payment.Status = PaymentStatus.Completed;
        payment.ProviderTransactionId = info.TransactionId;
        payment.FailureReason = null;
        payment.CompletedAt ??= _clock.UtcNow;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (!credit.WasDuplicate)
        {
            _logger.LogInformation(
                "Top-up {PaymentId}: credited {Amount} {Currency} to user {UserId} from Paymob transaction {TransactionId} ({Source}).",
                payment.Id, PaymobFormat.Riyals(payment.Amount), payment.Currency, payment.UserId, info.TransactionId, source);
        }

        return payment;
    }

    private async Task<Payment?> FindAsync(TransactionInfo info, CancellationToken ct)
    {
        var top = _db.Payments.Where(p => p.Provider == Provider && p.PaymentType == PaymentType.Deposit);

        if (Guid.TryParseExact(info.Reference, "N", out var id) || Guid.TryParse(info.Reference, out id))
        {
            var byReference = await top.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (byReference is not null) return byReference;
        }

        if (info.OrderId is { } orderId)
        {
            var marker = OrderMarker(orderId);
            return await top.FirstOrDefaultAsync(p => p.Metadata == marker, ct);
        }

        return null;
    }

    private static string OrderMarker(long orderId) => $"paymob-order:{orderId}";

    /// <summary>Paymob needs a name, a phone and an email for the card holder.</summary>
    private static PaymobCustomer CustomerFor(ApplicationUser user)
    {
        var parts = (user.FullName ?? string.Empty).Trim()
            .Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var first = parts.Length > 0 ? parts[0] : "DealGo";
        var last = parts.Length > 1 ? parts[1] : "Customer";

        // Accounts are made with a phone number; many have no email, and Paymob requires one.
        var email = string.IsNullOrWhiteSpace(user.Email) ? $"{user.Id:N}@customers.dealgosa.com" : user.Email;
        var phone = string.IsNullOrWhiteSpace(user.PhoneNumber) ? "NA" : user.PhoneNumber;

        return new PaymobCustomer(first, last, phone, email);
    }

    /// <summary>The parts of a Paymob transaction this service acts on.</summary>
    private sealed record TransactionInfo(
        string TransactionId,
        long? OrderId,
        string? Reference,
        long AmountCents,
        string Currency,
        bool Success,
        bool Pending,
        bool Voided,
        bool Refunded,
        string? Message)
    {
        public static TransactionInfo FromJson(JsonElement t)
        {
            static JsonElement? At(JsonElement e, params string[] path)
            {
                foreach (var p in path)
                {
                    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
                }
                return e;
            }

            static bool Flag(JsonElement? e) => e is { ValueKind: JsonValueKind.True };
            static string? Text(JsonElement? e) => e switch
            {
                { ValueKind: JsonValueKind.String } s => s.GetString(),
                { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
                _ => null
            };

            // Our reference: the order's merchant id, or the extra we sent, or the reference itself.
            var reference = Text(At(t, "order", "merchant_order_id"))
                            ?? Text(At(t, "payment_key_claims", "extra", "payment_id"))
                            ?? Text(At(t, "special_reference"));

            return new TransactionInfo(
                TransactionId: Text(At(t, "id")) ?? string.Empty,
                OrderId: long.TryParse(Text(At(t, "order", "id")), out var o) ? o : null,
                Reference: reference,
                AmountCents: long.TryParse(Text(At(t, "amount_cents")), out var a) ? a : -1,
                Currency: Text(At(t, "currency")) ?? string.Empty,
                Success: Flag(At(t, "success")),
                Pending: Flag(At(t, "pending")),
                Voided: Flag(At(t, "is_voided")),
                Refunded: Flag(At(t, "is_refunded")),
                Message: Text(At(t, "data", "message")));
        }

        public static TransactionInfo FromQuery(IReadOnlyDictionary<string, string> q)
        {
            string? Get(string k) => q.TryGetValue(k, out var v) ? v : null;
            static bool True(string? v) => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

            return new TransactionInfo(
                TransactionId: Get("id") ?? string.Empty,
                OrderId: long.TryParse(Get("order"), out var o) ? o : null,
                Reference: Get("merchant_order_id"),
                AmountCents: long.TryParse(Get("amount_cents"), out var a) ? a : -1,
                Currency: Get("currency") ?? string.Empty,
                Success: True(Get("success")),
                Pending: True(Get("pending")),
                Voided: True(Get("is_voided")),
                Refunded: True(Get("is_refunded")),
                Message: Get("data.message"));
        }
    }
}
