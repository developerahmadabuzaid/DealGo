using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Application.Realtime;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Api.BackgroundServices;

/// <summary>
/// Starts auctions whose scheduled time has arrived.
///
/// Several API instances run this loop simultaneously. Two things keep them from double-starting an
/// auction: a distributed lock around each candidate, and — decisively — a conditional UPDATE that
/// only succeeds for whichever instance gets there first.
/// </summary>
public sealed class AuctionSchedulerService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuctionSchedulerService> _logger;

    public AuctionSchedulerService(IServiceScopeFactory scopeFactory, ILogger<AuctionSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Auction scheduler started.");

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auction scheduler iteration failed.");
            }
        }

        _logger.LogInformation("Auction scheduler stopped.");
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var locks = scope.ServiceProvider.GetRequiredService<IDistributedLockService>();
        var realtime = scope.ServiceProvider.GetRequiredService<IAuctionRealtimeNotifier>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var now = clock.UtcNow;

        var due = await db.Auctions
            .AsNoTracking()
            .Where(a => (a.Status == AuctionStatus.WaitingForStart || a.Status == AuctionStatus.Ready) &&
                        a.ScheduledStartAt != null &&
                        a.ScheduledStartAt <= now)
            .Select(a => a.Id)
            .Take(50)
            .ToListAsync(ct);

        foreach (var auctionId in due)
        {
            await using var handle = await locks.AcquireAsync(
                $"auction:start:{auctionId}", TimeSpan.FromSeconds(30), ct);

            if (handle is null) continue;

            // The conditional UPDATE is the real guard: it matches only while the auction is still
            // unstarted, so exactly one instance can flip it to LIVE.
            var started = await db.Auctions
                .Where(a => a.Id == auctionId &&
                            (a.Status == AuctionStatus.WaitingForStart || a.Status == AuctionStatus.Ready) &&
                            a.ActualStartedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AuctionStatus.Live)
                    .SetProperty(a => a.ActualStartedAt, now)
                    .SetProperty(a => a.UpdatedAt, now), ct);

            if (started == 0) continue;

            var auction = await db.Auctions.AsNoTracking().FirstAsync(a => a.Id == auctionId, ct);

            db.AuctionEvents.Add(new AuctionEvent
            {
                AuctionId = auctionId,
                EventType = AuctionEventType.AuctionStarted,
                Timestamp = now
            });
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Auction {AuctionId} started on schedule.", auctionId);

            await SafeAsync(() => realtime.AuctionStartedAsync(
                auctionId, now, now.AddSeconds(auction.DurationSeconds), auction.CurrentPrice, ct));

            var userIds = await db.AuctionParticipants
                .AsNoTracking()
                .Where(p => p.AuctionId == auctionId)
                .Select(p => p.UserId)
                .ToListAsync(ct);

            await SafeAsync(() => notifications.NotifyManyAsync(userIds, new NotificationTemplate(
                NotificationType.AuctionStarted,
                NotificationKeys.AuctionStartedTitle,
                NotificationKeys.AuctionStartedBody,
                null,
                new Dictionary<string, string>
                {
                    ["type"] = "AUCTION_STARTED",
                    ["auctionId"] = auctionId.ToString()
                }), ct));
        }
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Scheduler side effect failed."); }
    }
}

/// <summary>
/// Applies the configured expiration policy to auctions whose maximum duration has elapsed without
/// the target price being reached.
/// </summary>
public sealed class AuctionExpiryService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuctionExpiryService> _logger;

    public AuctionExpiryService(IServiceScopeFactory scopeFactory, ILogger<AuctionExpiryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Auction expiry worker started.");

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auction expiry iteration failed.");
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var completion = scope.ServiceProvider.GetRequiredService<IAuctionCompletionService>();

        var now = clock.UtcNow;

        // EF cannot translate the interval arithmetic, so the candidate set is narrowed in SQL and
        // the exact deadline is checked in memory.
        var candidates = await db.Auctions
            .AsNoTracking()
            .Where(a => (a.Status == AuctionStatus.Live || a.Status == AuctionStatus.FinalPhase) &&
                        a.ActualStartedAt != null)
            .Select(a => new { a.Id, a.ActualStartedAt, a.DurationSeconds })
            .Take(200)
            .ToListAsync(ct);

        foreach (var candidate in candidates)
        {
            var endsAt = candidate.ActualStartedAt!.Value.AddSeconds(candidate.DurationSeconds);
            if (now < endsAt) continue;

            await completion.HandleExpiryAsync(candidate.Id, ct);
        }
    }
}

/// <summary>
/// Retries entry-fee refunds that failed on their first attempt, and finishes completion work for
/// auctions that ended while an instance was restarting.
/// </summary>
public sealed class AuctionCompletionWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuctionCompletionWorker> _logger;

    public AuctionCompletionWorker(IServiceScopeFactory scopeFactory, ILogger<AuctionCompletionWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();

                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var completion = scope.ServiceProvider.GetRequiredService<IAuctionCompletionService>();

                var pending = await db.Auctions
                    .AsNoTracking()
                    .Where(a => a.Status == AuctionStatus.Finished ||
                                (a.WinnersDeclared && !a.RefundsProcessed))
                    .Select(a => a.Id)
                    .Take(20)
                    .ToListAsync(stoppingToken);

                foreach (var auctionId in pending)
                {
                    // Safe to repeat: winner declaration and each refund are individually guarded.
                    await completion.CompleteAuctionAsync(auctionId, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auction completion worker iteration failed.");
            }
        }
    }
}

/// <summary>
/// Expires shopping credit when the store is configured to do so. Credit is only ever expired
/// through an explicit ledger entry, so a user's history always explains their balance.
/// </summary>
public sealed class ShoppingCreditExpiryWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ShoppingCreditExpiryWorker> _logger;

    public ShoppingCreditExpiryWorker(IServiceScopeFactory scopeFactory, ILogger<ShoppingCreditExpiryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();

                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var locks = scope.ServiceProvider.GetRequiredService<IDistributedLockService>();
                var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();

                var settings = await db.StoreSettings.AsNoTracking().FirstOrDefaultAsync(stoppingToken);
                if (settings is not { ShoppingCreditExpirationEnabled: true }) continue;

                await using var handle = await locks.AcquireAsync(
                    "credit:expiry", TimeSpan.FromMinutes(10), stoppingToken);
                if (handle is null) continue;

                var now = clock.UtcNow;

                var expiring = await db.WalletTransactions
                    .AsNoTracking()
                    .Where(t => t.WalletType == WalletType.Shopping &&
                                t.Amount > 0 &&
                                t.ExpiresAt != null &&
                                t.ExpiresAt <= now)
                    .Select(t => new { t.Id, t.UserId, t.Amount, t.Currency })
                    .Take(200)
                    .ToListAsync(stoppingToken);

                if (expiring.Count == 0) continue;

                var wallets = scope.ServiceProvider.GetRequiredService<IWalletService>();

                foreach (var credit in expiring)
                {
                    // Only the part still unspent can expire, so a user who already used the credit
                    // is never pushed negative.
                    var balance = await db.Wallets
                        .AsNoTracking()
                        .Where(w => w.UserId == credit.UserId && w.WalletType == WalletType.Shopping)
                        .Select(w => w.Balance)
                        .FirstOrDefaultAsync(stoppingToken);

                    var amount = Math.Min(credit.Amount, balance);

                    if (amount > 0)
                    {
                        await wallets.DebitAsync(new WalletMovement
                        {
                            UserId = credit.UserId,
                            WalletType = WalletType.Shopping,
                            Amount = amount,
                            Currency = credit.Currency,
                            TransactionType = WalletTransactionType.Expiration,
                            ReferenceType = nameof(WalletTransaction),
                            ReferenceId = credit.Id,
                            Description = "Shopping credit expired",
                            IdempotencyKey = $"credit-expiry:{credit.Id}"
                        }, stoppingToken);
                    }

                    // Clearing the marker keeps the entry out of the next sweep.
                    await db.WalletTransactions
                        .Where(t => t.Id == credit.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, (DateTime?)null), stoppingToken);
                }

                _logger.LogInformation("Expired {Count} shopping credit entries.", expiring.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shopping credit expiry iteration failed.");
            }
        }
    }
}
