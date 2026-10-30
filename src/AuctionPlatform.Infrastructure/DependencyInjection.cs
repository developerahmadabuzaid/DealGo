using AuctionPlatform.Application.Admin;
using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Application.Prizes;
using AuctionPlatform.Application.Site;
using AuctionPlatform.Application.Store;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Identity;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AuctionPlatform.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuctionOptions>(configuration.GetSection(AuctionOptions.SectionName));
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<RedisOptions>(configuration.GetSection(RedisOptions.SectionName));
        services.Configure<FirebaseOptions>(configuration.GetSection(FirebaseOptions.SectionName));
        services.Configure<PaymentOptions>(configuration.GetSection(PaymentOptions.SectionName));
        services.Configure<OtpOptions>(configuration.GetSection(OtpOptions.SectionName));
        services.Configure<TawkedOptions>(configuration.GetSection(TawkedOptions.SectionName));
        services.Configure<PaymobOptions>(configuration.GetSection(PaymobOptions.SectionName));

        AddPersistence(services, configuration);
        AddIdentity(services);
        AddRedis(services, configuration);
        AddApplicationServices(services);

        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? "Host=localhost;Port=5432;Database=auctions;Username=postgres;Password=postgres";

        services.AddDbContext<ApplicationDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                // Deliberately no EnableRetryOnFailure. The bid engine, wallet movements and
                // checkout all run inside explicit transactions that take row locks, and a
                // retrying execution strategy refuses to wrap user-initiated transactions.
                // Transient-failure recovery is instead the caller's job, which is safe because
                // every mutating operation is idempotency-keyed.
                npgsql.CommandTimeout(30);
            });

            // Queries are projected to DTOs, so tracking is opt-in per query.
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<DatabaseSeeder>();
    }

    private static void AddIdentity(IServiceCollection services)
    {
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                // Accounts created by phone sign-in have no email, and Identity rejects a missing
                // email while this is on. Registration checks for a duplicate email itself.
                options.User.RequireUniqueEmail = false;

                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.MaxFailedAccessAttempts = 10;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        services.AddScoped<IOtpService, OtpService>();

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAccountDeletionService, AccountDeletionService>();
        services.AddScoped<IUserAdminService, UserAdminService>();
    }

    private static void AddRedis(IServiceCollection services, IConfiguration configuration)
    {
        var redisSection = configuration.GetSection(RedisOptions.SectionName);
        var enabled = redisSection.GetValue("Enabled", true);
        var connectionString = redisSection.GetValue<string>("ConnectionString") ?? "localhost:6379";
        var prefix = redisSection.GetValue<string>("InstanceName") ?? "auction:";

        if (!enabled)
        {
            // Single-instance fallback so the API can run without Redis in development.
            services.AddSingleton<IDistributedLockService, InMemoryDistributedLockService>();
            services.AddSingleton<IRateLimiterService, InMemoryRateLimiterService>();
            services.AddSingleton<ICacheService, InMemoryCacheService>();
            return;
        }

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var config = ConfigurationOptions.Parse(connectionString);
            config.AbortOnConnectFail = false;
            config.ConnectRetry = 5;
            return ConnectionMultiplexer.Connect(config);
        });

        services.AddSingleton<IDistributedLockService, RedisDistributedLockService>();
        services.AddSingleton<IRateLimiterService, RedisRateLimiterService>();
        services.AddSingleton<ICacheService>(sp => new RedisCacheService(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            prefix,
            sp.GetRequiredService<ILogger<RedisCacheService>>()));
    }

    private static void AddApplicationServices(IServiceCollection services)
    {
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<ILocalizationService, LocalizationService>();

        /*
         * One-time codes.
         *
         * Tawked issues, delivers and checks the code for a Saudi mobile number. It is always
         * registered, and answers IsEnabled false when no key is configured, so the decision lives
         * in one place instead of in a nullable dependency.
         *
         * IOtpSender is what remains for a code we generate ourselves — an email destination,
         * which Tawked does not serve. Its only implementation still writes to the log rather than
         * sending anything, so email delivery is not solved by this and must not be relied on.
         */
        services.AddHttpClient<ITawkedVerifyClient, TawkedVerifyClient>();

        services.AddSingleton<IOtpSender>(sp => new LoggingOtpSender(
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<ILogger<LoggingOtpSender>>(),
            logCode: !sp.GetRequiredService<IHostEnvironment>().IsProduction()));

        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<INumberGenerator, NumberGenerator>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddHttpClient<IPushNotificationSender, FirebasePushNotificationSender>();

        services.AddScoped<IWalletService, WalletService>();
        services.AddScoped<IAdminWalletService, AdminWalletService>();
        services.AddScoped<IAdminWithdrawalService, AdminWithdrawalService>();

        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IAuctionService, AuctionService>();
        services.AddScoped<IAuctionAdminService, AuctionAdminService>();
        services.AddScoped<IParticipationService, ParticipationService>();
        services.AddScoped<IBidService, BidService>();
        services.AddScoped<IAuctionCompletionService, AuctionCompletionService>();

        services.AddScoped<IPrizeClaimService, PrizeClaimService>();

        services.AddScoped<IStoreCatalogService, StoreCatalogService>();
        services.AddScoped<StoreOrderService>();
        services.AddScoped<IStoreOrderService>(sp => sp.GetRequiredService<StoreOrderService>());
        services.AddScoped<IStoreAdminService, StoreAdminService>();
        services.AddScoped<ISiteContentService, SiteContentService>();
        services.AddScoped<IAppAdService, AppAdService>();

        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IPaymentWebhookService, PaymentWebhookService>();

        // Wallet top-ups by card, mada and Apple Pay through Paymob.
        services.AddHttpClient<IPaymobClient, PaymobClient>();
        services.AddScoped<IWalletTopUpService, WalletTopUpService>();
        services.AddScoped<IPaymentMonitorService, PaymentMonitorService>();
    }
}
