using System.Security.Cryptography;
using System.Text;
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

public sealed record WebhookResult(bool Accepted, bool AlreadyProcessed, string? Message);

public interface IPaymentWebhookService
{
    /// <summary>
    /// Verifies the signature, records the raw event and applies it exactly once. Redelivery of the
    /// same provider event is a no-op that still returns 200 so the provider stops retrying.
    /// </summary>
    Task<WebhookResult> HandleAsync(
        string provider, string rawBody, string? signature, CancellationToken ct = default);
}

public sealed class PaymentWebhookService : IPaymentWebhookService
{
    private readonly ApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly IDateTimeProvider _clock;
    private readonly PaymentOptions _options;
    private readonly ILogger<PaymentWebhookService> _logger;

    public PaymentWebhookService(
        ApplicationDbContext db,
        IWalletService wallets,
        IDateTimeProvider clock,
        IOptions<PaymentOptions> options,
        ILogger<PaymentWebhookService> logger)
    {
        _db = db;
        _wallets = wallets;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<WebhookResult> HandleAsync(
        string provider, string rawBody, string? signature, CancellationToken ct = default)
    {
        var signatureValid = VerifySignature(rawBody, signature);

        if (!signatureValid)
        {
            _logger.LogWarning("Rejected a {Provider} webhook with an invalid signature.", provider);
            return new WebhookResult(false, false, "Invalid signature.");
        }

        WebhookPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Empty payload.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Malformed {Provider} webhook payload.", provider);
            return new WebhookResult(false, false, "Malformed payload.");
        }

        if (string.IsNullOrWhiteSpace(payload.EventId))
        {
            return new WebhookResult(false, false, "eventId is required.");
        }

        // Persist the raw event first: the unique index on (Provider, ProviderEventId) is what makes
        // repeated delivery safe.
        var record = new PaymentWebhookEvent
        {
            Provider = provider,
            ProviderEventId = payload.EventId,
            ProviderTransactionId = payload.TransactionId,
            Payload = rawBody,
            Signature = signature,
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
            _logger.LogInformation(
                "Webhook {EventId} from {Provider} was already received; ignoring the redelivery.",
                payload.EventId, provider);
            return new WebhookResult(true, true, "Already processed.");
        }

        try
        {
            await ApplyAsync(provider, payload, record, ct);
            return new WebhookResult(true, false, null);
        }
        catch (Exception ex)
        {
            record.ProcessingError = ex.Message;
            await _db.SaveChangesAsync(ct);
            _logger.LogError(ex, "Failed to apply webhook {EventId} from {Provider}.", payload.EventId, provider);
            throw;
        }
    }

    private async Task ApplyAsync(
        string provider, WebhookPayload payload, PaymentWebhookEvent record, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        if (!string.Equals(payload.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            record.Processed = true;
            record.ProcessedAt = now;
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (payload.UserId is not { } userId || payload.Amount is not { } amount || amount <= 0)
        {
            record.Processed = true;
            record.ProcessedAt = now;
            record.ProcessingError = "Missing userId or amount.";
            await _db.SaveChangesAsync(ct);
            return;
        }

        var currency = payload.Currency ?? Money.DefaultCurrency;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Deposits land in the Main wallet only; the Shopping wallet is never externally funded.
        var result = await _wallets.CreditAsync(new WalletMovement
        {
            UserId = userId,
            WalletType = WalletType.Main,
            Amount = amount,
            Currency = currency,
            TransactionType = WalletTransactionType.Deposit,
            ReferenceType = "PaymentWebhook",
            Description = $"{provider} deposit",
            IdempotencyKey = $"webhook:{provider}:{payload.EventId}"
        }, ct);

        if (!result.WasDuplicate)
        {
            _db.Payments.Add(new Payment
            {
                UserId = userId,
                PaymentType = PaymentType.Deposit,
                Amount = Money.Round(amount),
                Currency = currency,
                Provider = provider,
                ProviderTransactionId = payload.TransactionId,
                Status = PaymentStatus.Completed,
                IdempotencyKey = $"webhook:{provider}:{payload.EventId}",
                CreatedAt = now,
                CompletedAt = now
            });
        }

        record.Processed = true;
        record.ProcessedAt = now;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(
            "Applied {Provider} webhook {EventId}: credited {Amount} {Currency} to user {UserId}.",
            provider, payload.EventId, amount, currency, userId);
    }

    /// <summary>HMAC-SHA256 over the raw body, compared in constant time.</summary>
    private bool VerifySignature(string rawBody, string? signature)
    {
        // A short or placeholder secret ("not-configured" was the production value) can be guessed,
        // and a guessed secret signs a credit to any wallet. Refuse outright.
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret) || _options.WebhookSecret.Length < 32)
        {
            _logger.LogWarning("No usable webhook secret is configured; rejecting the webhook.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(signature)) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
        var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)));

        var provided = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signature[7..]
            : signature;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(provided.ToUpperInvariant()));
    }

    private sealed record WebhookPayload(
        string EventId,
        string? TransactionId,
        string? Status,
        Guid? UserId,
        decimal? Amount,
        string? Currency);
}
