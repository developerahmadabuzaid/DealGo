using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Application.Prizes;
using AuctionPlatform.Application.Realtime;
using AuctionPlatform.Application.Site;
using AuctionPlatform.Application.Store;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Identity;
using AuctionPlatform.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// A throwaway PostgreSQL instance for the concurrency tests.
///
/// These tests cannot run against an in-memory provider: the guarantees under test are
/// SELECT ... FOR UPDATE and unique indexes, which only a real database provides.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("auctions_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        ConnectionString = _postgres.GetConnectionString();

        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    /// <summary>
    /// Each scope gets its own DbContext and its own connection, which is what lets a test issue
    /// genuinely parallel requests the way separate HTTP calls would.
    /// </summary>
    public AsyncServiceScope CreateScope(Guid? actingUserId = null, bool failWalletCredits = false)
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(ConnectionString), ServiceLifetime.Scoped);

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        // The seeder creates roles and promotes accounts, which needs the identity stores.
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddScoped<DatabaseSeeder>();

        services.Configure<AuctionOptions>(o =>
        {
            // Rate limiting is a separate concern; these tests are about database correctness.
            o.BidRateLimitPerSecond = int.MaxValue;
            o.BidRateLimitPerMinute = int.MaxValue;
        });
        services.Configure<JwtOptions>(o => o.Secret = "integration-test-signing-key-at-least-32-chars");
        services.Configure<OtpOptions>(o => o.AllowSignUpOnLogin = true);

        // The tests run as Development, so the OTP service returns the code and the flow can be
        // exercised without an SMS or email provider.
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment
        {
            EnvironmentName = Environments.Development,
            ApplicationName = "AuctionPlatform.Tests"
        });
        services.AddSingleton<IOtpSender, NoopOtpSender>();

        // No provider here, so codes stay on our side and come back on the challenge, which is
        // what these tests assert against. The provider path has its own unit tests.
        services.AddSingleton<ITawkedVerifyClient, DisabledTawkedClient>();

        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IDistributedLockService, InMemoryDistributedLockService>();
        services.AddSingleton<IRateLimiterService, NoopRateLimiter>();
        services.AddSingleton<ICacheService, InMemoryCacheService>();
        services.AddSingleton<IPushNotificationSender, NoopPushSender>();
        services.AddSingleton<IAuctionRealtimeNotifier, NoopRealtimeNotifier>();

        services.AddScoped<ICurrentUserService>(_ => new TestCurrentUser(actingUserId));
        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAccountDeletionService, AccountDeletionService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<INumberGenerator, NumberGenerator>();
        services.AddScoped<INotificationService, NotificationService>();

        if (failWalletCredits)
        {
            // A database failure in the middle of crediting a wallet, for the recovery tests.
            services.AddScoped<WalletService>();
            services.AddScoped<IWalletService>(sp => new FailingCreditWalletService(sp.GetRequiredService<WalletService>()));
        }
        else
        {
            services.AddScoped<IWalletService, WalletService>();
        }
        services.AddScoped<IAuctionService, AuctionService>();
        services.AddScoped<IParticipationService, ParticipationService>();
        services.AddScoped<IBidService, BidService>();
        services.AddScoped<IAuctionCompletionService, AuctionCompletionService>();
        services.AddScoped<IAuctionAdminService, AuctionAdminService>();
        services.AddScoped<IAppAdService, AppAdService>();
        services.AddScoped<IStoreCatalogService, StoreCatalogService>();
        services.AddScoped<IStoreAdminService, StoreAdminService>();
        services.AddScoped<StoreOrderService>();
        services.AddScoped<IStoreOrderService>(sp => sp.GetRequiredService<StoreOrderService>());

        // Top-ups run for real against the database; only the call out to Paymob is replaced.
        services.Configure<PaymobOptions>(o =>
        {
            o.SecretKey = "test-secret";
            o.PublicKey = "test-public";
            o.HmacSecret = PaymobHmacSecret;
            o.IntegrationIds = [1001];
            o.Mode = "test";
            o.ApiKey = "test-api-key";
        });
        services.AddSingleton<IPaymobClient, FakePaymobClient>();
        services.AddScoped<IWalletTopUpService, WalletTopUpService>();
        services.AddScoped<IPaymentMonitorService, PaymentMonitorService>();
        services.AddScoped<IPrizeClaimService, PrizeClaimService>();
        services.AddScoped<IAdminWalletService, AdminWalletService>();

        return services.BuildServiceProvider().CreateAsyncScope();
    }

    /// <summary>The HMAC secret the fixture's Paymob is configured with, for signing test callbacks.</summary>
    public const string PaymobHmacSecret = "test-hmac-secret";

    // ------------------------------------------------------------------ test doubles

    /// <summary>
    /// Paymob as far as opening a payment goes: it answers with a checkout page and an order id
    /// derived from the reference, so a test can sign a callback that names the same order.
    /// </summary>
    public sealed class FakePaymobClient : IPaymobClient
    {
        /// <summary>What Paymob's inquiry API answers, per order id. Absent: no transaction yet.</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> InquiryAnswers = new();

        /// <summary>Makes opening a payment fail as if Paymob timed out, for the next call.</summary>
        public static volatile bool FailNextCheckout;

        public bool IsEnabled => true;
        public bool CanInquire => true;

        public static long OrderIdFor(string reference) =>
            Math.Abs(BitConverter.ToInt64(Guid.Parse(reference).ToByteArray(), 0) % 1_000_000_000L) + 1;

        public Task<PaymobCheckout> CreateCheckoutAsync(decimal amount, string currency, string reference,
            string description, PaymobCustomer customer, string notificationUrl, string redirectionUrl,
            CancellationToken ct = default)
        {
            if (FailNextCheckout)
            {
                FailNextCheckout = false;
                throw new DomainException(ErrorCodes.ServiceUnavailable, "The payment service is not answering.");
            }

            return Task.FromResult(new PaymobCheckout(
                "pi_test_" + reference, OrderIdFor(reference), "https://checkout.test/" + reference));
        }

        public Task<System.Text.Json.JsonElement?> InquireByOrderAsync(long orderId, CancellationToken ct = default)
        {
            if (!InquiryAnswers.TryGetValue(orderId, out var json)) return Task.FromResult<System.Text.Json.JsonElement?>(null);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return Task.FromResult<System.Text.Json.JsonElement?>(doc.RootElement.Clone());
        }
    }

    private sealed class TestCurrentUser : ICurrentUserService
    {
        public TestCurrentUser(Guid? userId) => UserId = userId;

        public Guid? UserId { get; }
        public string? Email => UserId is null ? null : $"{UserId}@test.local";
        public bool IsAuthenticated => UserId is not null;
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public IReadOnlyCollection<string> PermissionClaims => Array.Empty<string>();
        public string? IpAddress => "127.0.0.1";
        public string? UserAgent => "tests";
        public bool HasPermission(string permission) => true;
    }
    /// <summary>Every credit fails as a dropped database connection would; everything else works.</summary>
    private sealed class FailingCreditWalletService(IWalletService inner) : IWalletService
    {
        public Task<WalletsDto> GetWalletsAsync(Guid userId, CancellationToken ct = default) => inner.GetWalletsAsync(userId, ct);
        public Task EnsureWalletsExistAsync(Guid userId, string currency, CancellationToken ct = default) => inner.EnsureWalletsExistAsync(userId, currency, ct);
        public Task<WalletOperationResult> CreditAsync(WalletMovement movement, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated database failure while crediting.");
        public Task<WalletOperationResult> DebitAsync(WalletMovement movement, CancellationToken ct = default) => inner.DebitAsync(movement, ct);
        public Task<PagedResult<WalletTransactionDto>> GetTransactionsAsync(Guid userId, WalletTransactionQuery query, CancellationToken ct = default) => inner.GetTransactionsAsync(userId, query, ct);
        public Task<WalletOperationResult> DepositAsync(Guid userId, DepositRequest request, CancellationToken ct = default) => inner.DepositAsync(userId, request, ct);
        public Task<OtpChallengeDto> RequestWithdrawalOtpAsync(Guid userId, RequestWithdrawalOtpRequest request, CancellationToken ct = default) => inner.RequestWithdrawalOtpAsync(userId, request, ct);
        public Task<WalletOperationResult> WithdrawAsync(Guid userId, WithdrawRequest request, CancellationToken ct = default) => inner.WithdrawAsync(userId, request, ct);
        public Task<ShoppingCreditSummaryDto> GetShoppingCreditSummaryAsync(string currency, CancellationToken ct = default) => inner.GetShoppingCreditSummaryAsync(currency, ct);
    }

    private sealed class NoopRateLimiter : IRateLimiterService
    {
        public Task<bool> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// A provider that is switched off, exactly as an unconfigured deployment sees it. Calling it
    /// would mean the service ignored IsEnabled, so both calls fail loudly rather than returning
    /// something plausible.
    /// </summary>
    private sealed class DisabledTawkedClient : ITawkedVerifyClient
    {
        public bool IsEnabled => false;

        public Task<TawkedVerification> StartAsync(string phoneE164, string language, string reference,
            string? clientIp, string idempotencyKey, CancellationToken ct = default) =>
            throw new InvalidOperationException("The provider is disabled and must not be called.");

        public Task<TawkedCheckOutcome> CheckAsync(string verificationId, string code,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("The provider is disabled and must not be called.");
    }

    /// <summary>Delivery is out of scope here; the code comes back on the challenge.</summary>
    private sealed class NoopOtpSender : IOtpSender
    {
        public Task SendAsync(string destination, OtpChannel channel, OtpPurpose purpose,
            string code, TimeSpan validFor, string language, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopPushSender : IPushNotificationSender
    {
        public Task SendAsync(IReadOnlyCollection<string> tokens, string title, string body,
            IReadOnlyDictionary<string, string>? data = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>SignalR is not exercised here; broadcasting is a delivery concern, not a rule.</summary>
    private sealed class NoopRealtimeNotifier : IAuctionRealtimeNotifier
    {
        public Task ParticipantCountChangedAsync(Guid a, int c, int r, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionReadyAsync(Guid a, int c, DateTime t, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionStartScheduledAsync(Guid a, DateTime s, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionStartTimeChangedAsync(Guid a, DateTime? p, DateTime n, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionStartedAsync(Guid a, DateTime s, DateTime e, decimal p, CancellationToken ct = default) => Task.CompletedTask;
        public Task BidPlacedAsync(Guid a, BidBroadcast b, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionPriceChangedAsync(Guid a, decimal c, decimal t, decimal p, CancellationToken ct = default) => Task.CompletedTask;
        public Task ParticipantRankChangedAsync(Guid a, Guid u, int r, decimal h, CancellationToken ct = default) => Task.CompletedTask;
        public Task LeaderboardUpdatedAsync(Guid a, IReadOnlyList<LeaderboardEntryDto> l, CancellationToken ct = default) => Task.CompletedTask;
        public Task FinalPhaseStartedAsync(Guid a, decimal c, decimal t, int? r, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionFinishedAsync(Guid a, decimal p, bool t, DateTime f, CancellationToken ct = default) => Task.CompletedTask;
        public Task WinnersDeclaredAsync(Guid a, IReadOnlyList<WinnerDto> w, CancellationToken ct = default) => Task.CompletedTask;
        public Task PrizeClaimAvailableAsync(Guid u, Guid c, Guid a, decimal p, string cur, CancellationToken ct = default) => Task.CompletedTask;
        public Task ShoppingCreditAddedAsync(Guid u, decimal amt, decimal bal, string cur, Guid? a, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionCancelledAsync(Guid a, string? r, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionPausedAsync(Guid a, CancellationToken ct = default) => Task.CompletedTask;
        public Task AuctionResumedAsync(Guid a, DateTime? e, CancellationToken ct = default) => Task.CompletedTask;
    }
}

[CollectionDefinition("database")]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;

/// <summary>Builders that create the minimum consistent data each scenario needs.</summary>
public static class TestData
{
    public static async Task<ApplicationUser> CreateUserAsync(
        ApplicationDbContext db, string name, decimal mainBalance = 0m, decimal shoppingBalance = 0m)
    {
        var user = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@test.local",
            Email = $"{Guid.NewGuid():N}@test.local",
            NormalizedEmail = $"{Guid.NewGuid():N}@TEST.LOCAL",
            NormalizedUserName = $"{Guid.NewGuid():N}@TEST.LOCAL",
            FullName = name,
            SecurityStamp = Guid.NewGuid().ToString(),
            IsActive = true
        };
        db.Users.Add(user);

        db.Wallets.Add(new Wallet
        {
            UserId = user.Id, WalletType = WalletType.Main, Balance = mainBalance, Currency = Money.DefaultCurrency
        });
        db.Wallets.Add(new Wallet
        {
            UserId = user.Id, WalletType = WalletType.Shopping, Balance = shoppingBalance, Currency = Money.DefaultCurrency
        });

        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<Domain.Entities.Auction> CreateLiveAuctionAsync(
        ApplicationDbContext db,
        decimal currentPrice = 2480m,
        decimal targetPrice = 2500m,
        decimal increment = 20m,
        int requiredParticipants = 100)
    {
        var product = new Product { Name = "Test Prize", Price = 5000m, Currency = Money.DefaultCurrency };
        var extras = Enumerable.Range(2, 3)
            .Select(i => new Product { Name = $"Prize {i}", Price = 1000m * i, Currency = Money.DefaultCurrency })
            .ToList();

        db.Products.Add(product);
        db.Products.AddRange(extras);
        await db.SaveChangesAsync();

        var auction = new Domain.Entities.Auction
        {
            AuctionNumber = $"AUC-TEST-{Guid.NewGuid():N}"[..24],
            Title = "Concurrency test auction",
            MainProductId = product.Id,
            MainProductNameSnapshot = product.Name,
            OriginalPrice = 5000m,
            StartingPrice = 1000m,
            CurrentPrice = currentPrice,
            TargetPercentage = 50m,
            TargetPrice = targetPrice,
            FinalPhasePercentage = 40m,
            FinalPhasePrice = 2000m,
            BidIncrement = increment,
            EntryFee = 20m,
            Currency = Money.DefaultCurrency,
            RequiredParticipants = requiredParticipants,
            DurationSeconds = 3600,
            Status = AuctionStatus.Live,
            ActualStartedAt = DateTime.UtcNow,
            ExpirationPolicy = ExpirationPolicy.EndAndDistribute,
            RefundLosingParticipantsEntryFee = true,
            RefundDestinationWallet = WalletType.Shopping
        };
        db.Auctions.Add(auction);

        db.AuctionPrizes.Add(new AuctionPrize
        {
            AuctionId = auction.Id, ProductId = product.Id, Rank = 1,
            OriginalPrice = product.Price, ProductNameSnapshot = product.Name
        });

        for (var i = 0; i < 3; i++)
        {
            db.AuctionPrizes.Add(new AuctionPrize
            {
                AuctionId = auction.Id, ProductId = extras[i].Id, Rank = i + 2,
                OriginalPrice = extras[i].Price, ProductNameSnapshot = extras[i].Name
            });
        }

        await db.SaveChangesAsync();
        return auction;
    }

    public static async Task<AuctionParticipant> AddParticipantAsync(
        ApplicationDbContext db, Guid auctionId, Guid userId, decimal entryFee = 20m)
    {
        var participant = new AuctionParticipant
        {
            AuctionId = auctionId,
            UserId = userId,
            EntryFeePaid = entryFee,
            Currency = Money.DefaultCurrency,
            Status = ParticipantStatus.Active
        };

        db.AuctionParticipants.Add(participant);

        var auction = await db.Auctions.FirstAsync(a => a.Id == auctionId);
        auction.ParticipantCount += 1;

        await db.SaveChangesAsync();
        return participant;
    }

    public static async Task<StoreProduct> CreateStoreProductAsync(
        ApplicationDbContext db, decimal price, int stock, StorePaymentMode mode = StorePaymentMode.Both)
    {
        var product = new StoreProduct
        {
            Name = "Test Item",
            Slug = $"test-item-{Guid.NewGuid():N}",
            Sku = $"SKU-{Guid.NewGuid():N}"[..20],
            Price = price,
            Currency = Money.DefaultCurrency,
            StockQuantity = stock,
            Status = StoreProductStatus.Active,
            PaymentMode = mode,
            RequiresShipping = false,
            ShippingFee = 0m
        };

        db.StoreProducts.Add(product);

        if (!await db.StoreSettings.AnyAsync())
        {
            db.StoreSettings.Add(new StoreSettings { ShippingEnabled = false });
        }

        await db.SaveChangesAsync();
        return product;
    }
}
