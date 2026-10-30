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

/// <summary>What the customer's return page should say. Nothing on it moves money.</summary>
public sealed record TopUpReturn(bool Valid, PaymentStatus? Status, Guid? PaymentId);

/// <summary>What one reconciliation pass found and did.</summary>
public sealed record TopUpReconciliation(
    int EventsRetried, int PaymentsChecked, int Credited, int Failed, int Expired, int StillOpen, int Errors);

public interface IWalletTopUpService
{
    /// <summary>Opens a Paymob payment for a top-up of the Main wallet.</summary>
    Task<TopUpStartDto> StartAsync(Guid userId, decimal amount, string siteBaseUrl, CancellationToken ct = default);

    Task<TopUpStatusDto> GetAsync(Guid userId, Guid paymentId, CancellationToken ct = default);

    /// <summary>Paymob's server-to-server "transaction processed" callback.</summary>
    Task<WebhookResult> HandleCallbackAsync(string rawBody, string? hmac, CancellationToken ct = default);

    /// <summary>
    /// The customer coming back from Paymob's checkout. Per Paymob's documentation this "response
    /// callback" is never used to update a payment; it only finds the payment, and asks Paymob's
    /// API about it when the processed callback has not arrived yet.
    /// </summary>
    Task<TopUpReturn> HandleReturnAsync(IReadOnlyDictionary<string, string> query, CancellationToken ct = default);

    /// <summary>
    /// Recovers anything a callback did not settle: callbacks recorded but not applied (a failure or
    /// a restart in between), and top-ups still open, which are checked with Paymob's inquiry API.
    /// Never credits anything Paymob's API or a verified callback has not confirmed.
    /// </summary>
    Task<TopUpReconciliation> ReconcileAsync(CancellationToken ct = default);
}

/// <summary>
/// Topping up the Main wallet with a card, mada or Apple Pay through Paymob.
///
/// Money enters a wallet only when all of this holds, checked under a lock on the payment row:
///   * the transaction came from Paymob: a processed callback whose HMAC verifies, or Paymob's own
///     inquiry API answering the server;
///   * it belongs to this payment: its order.id is the Paymob order this payment opened, and the
///     merchant reference, when present, is this payment's id;
///   * it succeeded and is not pending, voided or refunded;
///   * its amount in halalas and its currency are exactly what was recorded when it was opened.
/// The credit is keyed by the payment, and the payment row is locked, so the same payment is
/// credited once however many callbacks, retries or inquiries report it, in whatever order.
/// Nothing the app or the customer's browser sends can mark a top-up paid.
/// </summary>
public sealed class WalletTopUpService : IWalletTopUpService
{
    public const string Provider = "PAYMOB";

    // Prefixes on FailureReason / ProcessingError that monitoring counts.
    public const string AmountMismatch = "AMOUNT_MISMATCH";
    public const string ReferenceMismatch = "REFERENCE_MISMATCH";
    public const string NoMatch = "NO_MATCH";
    public const string InvalidHmac = "INVALID_HMAC";
    public const string RefundedAfterCredit = "REFUNDED_AFTER_CREDIT";
    public const string Expired = "EXPIRED";

    /// <summary>Rejected callbacks are recorded for monitoring, up to this many an hour.</summary>
    private const int MaxRejectedRecordsPerHour = 200;

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

    // ================================================================== starting a top-up

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

        // Each open top-up is an order at Paymob. A client looping on this endpoint would open
        // thousands, so the number one user may have open at once is capped.
        var since = _clock.UtcNow.AddSeconds(-_options.IntentionExpirySeconds);
        var open = await _db.Payments.CountAsync(p =>
            p.UserId == userId && p.Provider == Provider && p.PaymentType == PaymentType.Deposit &&
            (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Processing) &&
            p.CreatedAt >= since, ct);
        if (open >= _options.MaxOpenTopUps)
        {
            throw new DomainException(ErrorCodes.RateLimited, "Too many unpaid top-ups are open. Finish or wait for one first.");
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
            // Nothing was opened at Paymob, so nothing can ever be paid against this row.
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = "Paymob did not open the payment.";
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        payment.ProviderOrderId = checkout.OrderId;
        payment.Metadata = $"paymob-intention:{checkout.IntentionId}";
        payment.Status = PaymentStatus.Processing;
        await _db.SaveChangesAsync(ct);

        return new TopUpStartDto(payment.Id, payment.Amount, payment.Currency, checkout.CheckoutUrl);
    }

    public async Task<TopUpStatusDto> GetAsync(Guid userId, Guid paymentId, CancellationToken ct = default)
    {
        // Scoped to the caller: one user cannot read another's top-up by guessing its id.
        var p = await _db.Payments.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == paymentId && x.UserId == userId && x.Provider == Provider, ct)
                ?? throw new DomainException(ErrorCodes.NotFound, "Top-up not found.");

        return new TopUpStatusDto(p.Id, p.Amount, p.Currency, p.Status, PublicReason(p));
    }

    // ================================================================== Paymob's processed callback

    public async Task<WebhookResult> HandleCallbackAsync(string rawBody, string? hmac, CancellationToken ct = default)
    {
        if (!_paymob.IsEnabled) return new WebhookResult(false, false, "Paymob is not configured.");

        JsonElement transaction;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            // Only transaction callbacks move money; the same URL also receives card tokens.
            if (root.TryGetProperty("type", out var type) &&
                !string.Equals(type.GetString(), "TRANSACTION", StringComparison.OrdinalIgnoreCase))
            {
                return new WebhookResult(true, false, "Not a transaction.");
            }

            transaction = (root.TryGetProperty("obj", out var obj) ? obj : root).Clone();
        }
        catch (JsonException)
        {
            await RecordRejectedAsync(rawBody, hmac, "Malformed payload", ct);
            return new WebhookResult(false, false, "Malformed payload.");
        }

        if (!PaymobClient.HmacMatches(PaymobClient.ComputeHmac(transaction, _options.HmacSecret), hmac))
        {
            _logger.LogWarning("Rejected a Paymob callback with a wrong signature.");
            await RecordRejectedAsync(rawBody, hmac, "Signature does not verify", ct);
            return new WebhookResult(false, false, "Invalid signature.");
        }

        var info = TransactionInfo.FromJson(transaction);
        var eventId = $"txn:{info.TransactionId}:{info.Success}:{info.Pending}:{info.Voided}:{info.Refunded}";

        // Recorded before anything is applied, so a confirmation survives a crash or a restart:
        // a recorded, unapplied event is applied again by the next delivery or by reconciliation.
        var record = await _db.PaymentWebhookEvents
            .FirstOrDefaultAsync(e => e.Provider == Provider && e.ProviderEventId == eventId, ct);

        if (record is { Processed: true })
        {
            return new WebhookResult(true, true, "Already processed.");
        }

        if (record is null)
        {
            record = new PaymentWebhookEvent
            {
                Provider = Provider,
                ProviderEventId = eventId,
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
                // The same delivery arrived twice at once and the other copy recorded it first. It
                // is applied there; this one changes nothing.
                _db.Entry(record).State = EntityState.Detached;
                return new WebhookResult(true, true, "Already being processed.");
            }
        }

        try
        {
            var outcome = await ApplyAsync(info, "callback", ct);
            await MarkEventAsync(record.Id, processed: true, outcome.Error, ct);
            return new WebhookResult(true, false, outcome.Error);
        }
        catch (Exception ex)
        {
            // Left unprocessed on purpose: Paymob's retry, or the next reconciliation, applies it.
            _logger.LogError(ex, "Applying Paymob transaction {TransactionId} failed; it will be retried.", info.TransactionId);
            await MarkEventAsync(record.Id, processed: false, Truncate(ex.Message, 1900), CancellationToken.None);
            throw;
        }
    }

    // ================================================================== the customer's return

    public async Task<TopUpReturn> HandleReturnAsync(
        IReadOnlyDictionary<string, string> query, CancellationToken ct = default)
    {
        if (!_paymob.IsEnabled) return new TopUpReturn(false, null, null);

        query.TryGetValue("hmac", out var hmac);
        if (!PaymobClient.HmacMatches(PaymobClient.ComputeHmac(query, _options.HmacSecret), hmac))
        {
            _logger.LogWarning("A return from Paymob carried a wrong signature.");
            return new TopUpReturn(false, null, null);
        }

        var info = TransactionInfo.FromQuery(query);
        var payment = await FindAsync(info, track: false, ct);
        if (payment is null) return new TopUpReturn(true, null, null);

        // Not trusted to change anything. If the processed callback is late, Paymob's API is asked.
        if (payment.Status is PaymentStatus.Processing or PaymentStatus.Pending && _paymob.CanInquire)
        {
            try
            {
                await InquireAndApplyAsync(payment.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inquiry on return for top-up {PaymentId} failed; reconciliation will retry.", payment.Id);
            }
        }

        var status = await _db.Payments.AsNoTracking()
            .Where(p => p.Id == payment.Id).Select(p => p.Status).FirstAsync(ct);
        return new TopUpReturn(true, status, payment.Id);
    }

    // ================================================================== reconciliation

    public async Task<TopUpReconciliation> ReconcileAsync(CancellationToken ct = default)
    {
        if (!_paymob.IsEnabled) return new TopUpReconciliation(0, 0, 0, 0, 0, 0, 0);

        int retried = 0, checkedCount = 0, credited = 0, failed = 0, expired = 0, open = 0, errors = 0;
        var now = _clock.UtcNow;

        // 1. Callbacks that were verified and recorded but never applied.
        var stale = now.AddMinutes(-1);
        var pendingEvents = await _db.PaymentWebhookEvents.AsNoTracking()
            .Where(e => e.Provider == Provider && e.SignatureValid && !e.Processed && e.ReceivedAt <= stale)
            .OrderBy(e => e.ReceivedAt)
            .Take(100)
            .Select(e => new { e.Id, e.Payload })
            .ToListAsync(ct);

        foreach (var e in pendingEvents)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.Payload);
                var t = doc.RootElement.TryGetProperty("obj", out var obj) ? obj : doc.RootElement;
                var outcome = await ApplyAsync(TransactionInfo.FromJson(t), "retry", ct);
                await MarkEventAsync(e.Id, processed: true, outcome.Error, ct);
                retried++;
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogError(ex, "Retrying recorded Paymob event {EventId} failed.", e.Id);
                await MarkEventAsync(e.Id, processed: false, Truncate(ex.Message, 1900), CancellationToken.None);
            }
        }

        // 2. Top-ups still open after a few minutes: ask Paymob.
        var quiet = now.AddMinutes(-3);
        var openPayments = await _db.Payments.AsNoTracking()
            .Where(p => p.Provider == Provider && p.PaymentType == PaymentType.Deposit &&
                        (p.Status == PaymentStatus.Processing || p.Status == PaymentStatus.Pending) &&
                        p.CreatedAt <= quiet)
            .OrderBy(p => p.CreatedAt)
            .Take(50)
            .Select(p => p.Id)
            .ToListAsync(ct);

        foreach (var id in openPayments)
        {
            checkedCount++;
            try
            {
                var after = await InquireAndApplyAsync(id, ct);
                switch (after)
                {
                    case PaymentStatus.Completed: credited++; break;
                    case PaymentStatus.Failed: failed++; break;
                    case PaymentStatus.Cancelled: expired++; break;
                    default: open++; break;
                }
            }
            catch (Exception ex)
            {
                errors++;
                _logger.LogWarning(ex, "Reconciling top-up {PaymentId} failed; it stays open.", id);
            }
        }

        if (retried + credited + failed + expired + errors > 0)
        {
            _logger.LogInformation(
                "Paymob reconciliation: {Retried} events retried, {Checked} top-ups checked, {Credited} credited, {Failed} failed, {Expired} expired, {Open} still open, {Errors} errors.",
                retried, checkedCount, credited, failed, expired, open, errors);
        }

        return new TopUpReconciliation(retried, checkedCount, credited, failed, expired, open, errors);
    }

    /// <summary>
    /// Asks Paymob's API for the latest transaction on the payment's order and applies it. A top-up
    /// with no transaction long after its checkout expired is closed as expired; nothing was paid.
    /// </summary>
    private async Task<PaymentStatus> InquireAndApplyAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await _db.Payments.AsNoTracking().FirstAsync(p => p.Id == paymentId, ct);
        if (payment.Status is not (PaymentStatus.Processing or PaymentStatus.Pending)) return payment.Status;

        var now = _clock.UtcNow;
        var expiredAt = payment.CreatedAt.AddSeconds(_options.IntentionExpirySeconds).AddMinutes(30);

        if (payment.ProviderOrderId is not { } orderId)
        {
            // Paymob never opened an order for it, so it can never be paid.
            if (now > expiredAt) await CloseAsExpiredAsync(paymentId, "no Paymob order", ct);
            return payment.Status;
        }

        if (!_paymob.CanInquire)
        {
            // Without the API key the processed callback is the only confirmation; an open top-up
            // is left for it (or for a person with the Paymob dashboard), never guessed at.
            return payment.Status;
        }

        var transaction = await _paymob.InquireByOrderAsync(orderId, ct);
        if (transaction is { } t)
        {
            await ApplyAsync(TransactionInfo.FromJson(t), "inquiry", ct);
        }
        else if (now > expiredAt)
        {
            await CloseAsExpiredAsync(paymentId, "no transaction at Paymob after the checkout expired", ct);
        }

        return await _db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => p.Status).FirstAsync(ct);
    }

    private async Task CloseAsExpiredAsync(Guid paymentId, string why, CancellationToken ct)
    {
        await _db.Payments
            .Where(p => p.Id == paymentId && (p.Status == PaymentStatus.Processing || p.Status == PaymentStatus.Pending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PaymentStatus.Cancelled)
                .SetProperty(p => p.FailureReason, $"{Expired}: {why}"), ct);
    }

    // ================================================================== applying a verified result

    private sealed record ApplyOutcome(Guid? PaymentId, string? Error);

    /// <summary>
    /// Applies a transaction already proven to come from Paymob (a verified callback, or Paymob's
    /// API) to the payment it belongs to, at most once, under a lock on that payment's row.
    /// </summary>
    private async Task<ApplyOutcome> ApplyAsync(TransactionInfo info, string source, CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);

        var payment = await FindAsync(info, track: true, ct);
        if (payment is null)
        {
            // Paymob confirmed something we did not open (or not with this order). Recorded and
            // reported; never credited to anyone.
            _logger.LogWarning(
                "Paymob {Source} for transaction {TransactionId}, order {OrderId}, reference {Reference} matches no top-up.",
                source, info.TransactionId, info.OrderId, info.Reference);
            await tx.CommitAsync(ct);
            return new ApplyOutcome(null, $"{NoMatch}: order {info.OrderId}, reference {info.Reference}");
        }

        // The merchant reference, when Paymob sends one, must be this payment's id. An order id that
        // matched but a reference that names another payment means something is badly wrong.
        if (!string.IsNullOrWhiteSpace(info.Reference) &&
            !string.Equals(info.Reference, payment.Id.ToString("N"), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(info.Reference, payment.Id.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogCritical(
                "Paymob transaction {TransactionId} for order {OrderId} names reference {Reference}, not top-up {PaymentId}; not applied.",
                info.TransactionId, info.OrderId, info.Reference, payment.Id);
            await tx.CommitAsync(ct);
            return new ApplyOutcome(payment.Id, $"{ReferenceMismatch}: {info.Reference}");
        }

        if (payment.Status == PaymentStatus.Completed)
        {
            if (info.Refunded || info.Voided)
            {
                // Money already credited and then returned at Paymob. Taking it back out of the
                // wallet is a decision for a person (it may already be spent): flagged, not automated.
                _logger.LogCritical(
                    "Paymob reports top-up {PaymentId} (transaction {TransactionId}) refunded or voided after it was credited.",
                    payment.Id, info.TransactionId);
                await tx.CommitAsync(ct);
                return new ApplyOutcome(payment.Id, $"{RefundedAfterCredit}: transaction {info.TransactionId}");
            }

            await tx.CommitAsync(ct);
            return new ApplyOutcome(payment.Id, null);
        }

        if (payment.Status is PaymentStatus.Failed or PaymentStatus.Cancelled && !info.Success)
        {
            await tx.CommitAsync(ct);
            return new ApplyOutcome(payment.Id, null);
        }

        if (info.Pending)
        {
            await tx.CommitAsync(ct);
            return new ApplyOutcome(payment.Id, null);
        }

        if (!info.Success || info.Voided || info.Refunded)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = Truncate(string.IsNullOrWhiteSpace(info.Message) ? "Payment declined." : info.Message, 1000);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogInformation("Top-up {PaymentId} failed at Paymob ({Source}): {Reason}", payment.Id, source, payment.FailureReason);
            return new ApplyOutcome(payment.Id, null);
        }

        // The amount and currency Paymob took must be exactly those recorded. A mismatch is never
        // credited: the customer may have paid something, and a person must look.
        if (info.AmountCents != PaymobClient.ToHalalas(payment.Amount) ||
            !string.Equals(info.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason =
                $"{AmountMismatch}: Paymob reported {info.AmountCents} {info.Currency} for a top-up of {PaymobFormat.Riyals(payment.Amount)} {payment.Currency}.";
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _logger.LogCritical("Top-up {PaymentId} not credited: {Reason}", payment.Id, payment.FailureReason);
            return new ApplyOutcome(payment.Id, payment.FailureReason);
        }

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
            Metadata = $"paymob-transaction:{info.TransactionId}",
            IdempotencyKey = payment.IdempotencyKey
        }, ct);

        payment.Status = PaymentStatus.Completed;
        payment.ProviderTransactionId ??= info.TransactionId;
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

        return new ApplyOutcome(payment.Id, null);
    }

    /// <summary>
    /// The payment a transaction belongs to: by Paymob's order id first, as its documentation
    /// says, then by our reference. With <paramref name="track"/> the row is locked FOR UPDATE.
    /// </summary>
    private async Task<Payment?> FindAsync(TransactionInfo info, bool track, CancellationToken ct)
    {
        if (info.OrderId is { } orderId)
        {
            var byOrder = track
                ? await _db.Payments.FromSqlRaw(
                        """SELECT * FROM "Payments" WHERE "Provider" = {0} AND "ProviderOrderId" = {1} FOR UPDATE""",
                        Provider, orderId)
                    .FirstOrDefaultAsync(ct)
                : await _db.Payments.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Provider == Provider && p.ProviderOrderId == orderId, ct);
            if (byOrder is not null) return byOrder;
        }

        if (Guid.TryParseExact(info.Reference, "N", out var id) || Guid.TryParse(info.Reference, out id))
        {
            // By reference only when the payment has no Paymob order recorded: otherwise the order
            // id above was authoritative, and a transaction for another order must not settle it.
            var byReference = track
                ? await _db.Payments.FromSqlRaw(
                        """SELECT * FROM "Payments" WHERE "Id" = {0} AND "Provider" = {1} FOR UPDATE""",
                        id, Provider)
                    .FirstOrDefaultAsync(ct)
                : await _db.Payments.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == id && p.Provider == Provider, ct);
            if (byReference is not null && (byReference.ProviderOrderId is null || byReference.ProviderOrderId == info.OrderId))
            {
                return byReference;
            }
        }

        return null;
    }

    // ================================================================== records

    private async Task MarkEventAsync(Guid eventId, bool processed, string? error, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _db.PaymentWebhookEvents
            .Where(e => e.Id == eventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Processed, processed)
                .SetProperty(e => e.ProcessedAt, processed ? now : null)
                .SetProperty(e => e.ProcessingError, error == null ? null : Truncate(error, 1900)), ct);
    }

    /// <summary>
    /// Keeps a refused callback for monitoring, without trusting anything in it. Capped, so a
    /// flood of junk cannot fill the database.
    /// </summary>
    private async Task RecordRejectedAsync(string rawBody, string? hmac, string why, CancellationToken ct)
    {
        try
        {
            var hourAgo = _clock.UtcNow.AddHours(-1);
            var recent = await _db.PaymentWebhookEvents.CountAsync(
                e => e.Provider == Provider && !e.SignatureValid && e.ReceivedAt >= hourAgo, ct);
            if (recent >= MaxRejectedRecordsPerHour) return;

            _db.PaymentWebhookEvents.Add(new PaymentWebhookEvent
            {
                Provider = Provider,
                ProviderEventId = $"rejected:{Guid.NewGuid():N}",
                Payload = Truncate(rawBody, 4000),
                Signature = hmac is null ? null : Truncate(hmac, 512),
                SignatureValid = false,
                Processed = true,
                ProcessingError = $"{InvalidHmac}: {why}",
                ReceivedAt = _clock.UtcNow,
                ProcessedAt = _clock.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record a rejected Paymob callback.");
        }
    }

    /// <summary>What the customer may read about a failure; internal markers stay internal.</summary>
    private static string? PublicReason(Payment p) =>
        p.FailureReason is null ? null
        : p.FailureReason.StartsWith(AmountMismatch, StringComparison.Ordinal) ||
          p.FailureReason.StartsWith(ReferenceMismatch, StringComparison.Ordinal) ? "Under review."
        : p.FailureReason.StartsWith(Expired, StringComparison.Ordinal) ? "Expired."
        : p.FailureReason;

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

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

            // order is an object with an id in callbacks and inquiries.
            var orderId = Text(At(t, "order", "id")) ?? Text(At(t, "order"));

            return new TransactionInfo(
                TransactionId: Text(At(t, "id")) ?? string.Empty,
                OrderId: long.TryParse(orderId, out var o) ? o : null,
                Reference: Text(At(t, "order", "merchant_order_id"))
                           ?? Text(At(t, "merchant_order_id"))
                           ?? Text(At(t, "payment_key_claims", "extra", "payment_id")),
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
                OrderId: long.TryParse(Get("order_id") ?? Get("order"), out var o) ? o : null,
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
