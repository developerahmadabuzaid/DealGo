using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Application.Realtime;
using AuctionPlatform.Application.Store;
using AuctionPlatform.Application.Wallets;
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
    public AsyncServiceScope CreateScope(Guid? actingUserId = null)
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(ConnectionString), ServiceLifetime.Scoped);

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        services.Configure<AuctionOptions>(o =>
        {
            // Rate limiting is a separate concern; these tests are about database correctness.
            o.BidRateLimitPerSecond = int.MaxValue;
            o.BidRateLimitPerMinute = int.MaxValue;
        });
        services.Configure<JwtOptions>(_ => { });
        services.Configure<OtpOptions>(_ => { });

        // The tests run as Development, so the OTP service returns the code and the flow can be
        // exercised without an SMS or email provider.
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment
        {
            EnvironmentName = Environments.Development,
            ApplicationName = "AuctionPlatform.Tests"
        });
        services.AddSingleton<IOtpSender, NoopOtpSender>();

        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IDistributedLockService, InMemoryDistributedLockService>();
        services.AddSingleton<IRateLimiterService, NoopRateLimiter>();
        services.AddSingleton<ICacheService, InMemoryCacheService>();
        services.AddSingleton<IPushNotificationSender, NoopPushSender>();
        services.AddSingleton<IAuctionRealtimeNotifier, NoopRealtimeNotifier>();

        services.AddScoped<ICurrentUserService>(_ => new TestCurrentUser(actingUserId));
        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<INumberGenerator, NumberGenerator>();
        services.AddScoped<INotificationService, NotificationService>();

        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<IAuctionService, AuctionService>();
        services.AddScoped<IParticipationService, ParticipationService>();
        services.AddScoped<IBidService, BidService>();
        services.AddScoped<IAuctionCompletionService, AuctionCompletionService>();
        services.AddScoped<IStoreCatalogService, StoreCatalogService>();
        services.AddScoped<StoreOrderService>();
        services.AddScoped<IStoreOrderService>(sp => sp.GetRequiredService<StoreOrderService>());

        return services.BuildServiceProvider().CreateAsyncScope();
    }

    // ------------------------------------------------------------------ test doubles

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

    private sealed class NoopRateLimiter : IRateLimiterService
    {
        public Task<bool> TryAcquireAsync(string key, int limit, TimeSpan window, CancellationToken ct = default) =>
            Task.FromResult(true);
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
            UserId = user.Id, WalletType = WalletType.Main, Balance = mainBalance, Currency = "USD"
        });
        db.Wallets.Add(new Wallet
        {
            UserId = user.Id, WalletType = WalletType.Shopping, Balance = shoppingBalance, Currency = "USD"
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
        var product = new Product { Name = "Test Prize", Price = 5000m, Currency = "USD" };
        var extras = Enumerable.Range(2, 3)
            .Select(i => new Product { Name = $"Prize {i}", Price = 1000m * i, Currency = "USD" })
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
            Currency = "USD",
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
            Currency = "USD",
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
            Currency = "USD",
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
