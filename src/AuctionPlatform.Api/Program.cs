using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AuctionPlatform.Api.BackgroundServices;
using AuctionPlatform.Api.Hubs;
using AuctionPlatform.Api.Middleware;
using AuctionPlatform.Api.Services;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Realtime;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Infrastructure;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- logging

builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "AuctionPlatform.Api")
        .WriteTo.Console();

    /*
     * Serilog's file sink throws while the host is being built if it cannot open the file, and a
     * shared host gives the application pool identity no guarantee that it may create a folder
     * inside the site. That turns "no log file" into "no application at all", which is a far worse
     * outcome than the one being avoided. So the folder is attempted, and failing that the app
     * still starts on the console sink — the only sink that always works — and says why in SelfLog.
     */
    try
    {
        var logDirectory = Path.Combine(context.HostingEnvironment.ContentRootPath, "logs");
        Directory.CreateDirectory(logDirectory);

        configuration.WriteTo.File(
            Path.Combine(logDirectory, "api-.log"),
            rollingInterval: Serilog.RollingInterval.Day,
            retainedFileCountLimit: 14);
    }
    catch (Exception ex)
    {
        Serilog.Debugging.SelfLog.WriteLine(
            "File logging is disabled for this deployment: {0}", ex.Message);
    }
});

// ---------------------------------------------------------------- configuration

var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtSecret = jwtSection.GetValue<string>("Secret");

if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret.Length < 32)
{
    if (builder.Environment.IsProduction())
    {
        // Refusing to start beats silently signing tokens with a guessable key.
        throw new InvalidOperationException(
            "Jwt:Secret must be supplied (min 32 chars) via environment variables or user secrets in Production.");
    }

    jwtSecret = "development-only-signing-key-change-me-please-32+";
    builder.Configuration["Jwt:Secret"] = jwtSecret;
}

/*
 * Can this deployment actually send a one-time code?
 *
 * Signing in is by one-time code, and so is every withdrawal. Without a provider the API answers
 * a code request with a perfectly normal 200 and sends nothing, so the failure is invisible from
 * the outside: the customer simply waits for an SMS that never arrives, and nothing in the log
 * says the platform has no way to sign anybody in.
 *
 * Refusing to start says it once, loudly, at the only moment somebody is watching.
 */
var tawkedKey = builder.Configuration.GetValue<string>($"{TawkedOptions.SectionName}:ApiKey");

if (string.IsNullOrWhiteSpace(tawkedKey) && builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "Tawked:ApiKey must be supplied in Production, or no user can sign in and no withdrawal " +
        "can be confirmed. Set Tawked__ApiKey from the application's Keys tab at tawked.com " +
        "(tk_live_… for production, tk_test_… for the sandbox).");
}

// ---------------------------------------------------------------- services

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

// SignalR is the delivery mechanism for state PostgreSQL has already committed.
builder.Services.AddScoped<IAuctionRealtimeNotifier, SignalRAuctionNotifier>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        // Enums travel as their names so the mobile client is not coupled to numeric values.
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

var signalR = builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
})
.AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// The Redis backplane is what lets a bid accepted on one instance reach clients on another.
var redisSection = builder.Configuration.GetSection(RedisOptions.SectionName);
if (redisSection.GetValue("Enabled", true))
{
    var redisConnection = redisSection.GetValue<string>("ConnectionString") ?? "localhost:6379";
    signalR.AddStackExchangeRedis(redisConnection, options =>
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("auction-signalr"));
}

// ---------------------------------------------------------------- authentication

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
    options.SaveToken = true;

    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSection.GetValue<string>("Issuer") ?? "auction-platform",
        ValidAudience = jwtSection.GetValue<string>("Audience") ?? "auction-platform-clients",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    // Browsers cannot set headers on a WebSocket handshake, so SignalR passes the token in the
    // query string. It is accepted only for the hub path.
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments(AuctionHub.Route))
            {
                context.Token = accessToken;
            }

            return Task.CompletedTask;
        }
    };
});

// ---------------------------------------------------------------- authorization

builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

builder.Services.AddAuthorization(options =>
{
    // One policy per permission. SUPER_ADMIN satisfies every policy without an explicit claim.
    foreach (var permission in Permissions.All)
    {
        options.AddPolicy(permission, policy =>
            policy.RequireAssertion(context =>
                context.User.IsInRole(Roles.SuperAdmin) ||
                context.User.HasClaim(Permissions.ClaimType, permission)));
    }

    options.AddPolicy("AdminArea", policy =>
        policy.RequireAssertion(context => Roles.AdminRoles.Any(context.User.IsInRole)));
});

// ---------------------------------------------------------------- rate limiting

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            $"{{\"code\":\"{ErrorCodes.RateLimited}\",\"message\":\"Too many requests. Please slow down.\",\"details\":[]}}",
            ct);
    };

    // Bidding is the hot path and the most attractive endpoint to abuse, so it gets its own,
    // much tighter bucket keyed on the caller.
    options.AddPolicy("bidding", context => RateLimitPartition.GetTokenBucketLimiter(
        PartitionKey(context),
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 10,
            TokensPerPeriod = 5,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));

    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        PartitionKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    static string PartitionKey(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anon"
            : context.Connection.RemoteIpAddress?.ToString() ?? "anon";
});

// ---------------------------------------------------------------- cors

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:3000", "http://localhost:5173", "http://localhost:8080" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("Default", policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        // Required for the SignalR WebSocket handshake.
        .AllowCredentials());
});

// ---------------------------------------------------------------- observability

builder.Services.AddHealthChecks()
    .AddNpgSql(
        builder.Configuration.GetConnectionString("Postgres")
            ?? "Host=localhost;Port=5432;Database=auctions;Username=postgres;Password=postgres",
        name: "postgres",
        tags: new[] { "ready" });

if (redisSection.GetValue("Enabled", true))
{
    builder.Services.AddHealthChecks().AddRedis(
        redisSection.GetValue<string>("ConnectionString") ?? "localhost:6379",
        name: "redis",
        tags: new[] { "ready" });
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("auction-platform-api"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(o => o.RecordException = true)
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());

// ---------------------------------------------------------------- background workers

builder.Services.AddHostedService<AuctionSchedulerService>();
builder.Services.AddHostedService<AuctionExpiryService>();
builder.Services.AddHostedService<AuctionCompletionWorker>();
builder.Services.AddHostedService<ShoppingCreditExpiryWorker>();
builder.Services.AddHostedService<PaymobReconciliationWorker>();

// ---------------------------------------------------------------- swagger

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Auction Platform API",
        Version = "v1",
        Description =
            "Real-time auction platform. The backend is authoritative for price, time, rank, " +
            "winners, balances and stock; clients only display server state."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token returned by /api/v1/auth/login."
    });

    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        { new OpenApiSecuritySchemeReference("Bearer"), new List<string>() }
    });

    var xmlPath = Path.Combine(AppContext.BaseDirectory, "AuctionPlatform.Api.xml");
    if (File.Exists(xmlPath)) options.IncludeXmlComments(xmlPath);
});

/*
 * Where uploaded pictures are kept.
 *
 * A container's disk goes away with the container, so production writes them to a storage account
 * instead; a developer with no account configured keeps the old behaviour of a folder under
 * wwwroot. The choice is one setting, and nothing above the interface knows which was made.
 */
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));

if (builder.Configuration
    .GetSection(StorageOptions.SectionName)
    .Get<StorageOptions>()?.UsesAzureBlob == true)
{
    builder.Services.AddSingleton<IMediaStorage, BlobMediaStorage>();
}
else
{
    builder.Services.AddSingleton<IMediaStorage, LocalMediaStorage>();
}

var app = builder.Build();

/*
 * Why the API stopped.
 *
 * A request that fails is written to the log by the middleware below, but a process that dies
 * leaves nothing behind unless someone asks for it: the last line of the file is simply whatever
 * happened to be running. These three hooks make the ending legible — a crash, a background task
 * nobody awaited, or an ordinary shutdown — so "the server stopped" can be answered from the log
 * rather than guessed at.
 */
{
    var lifetimeLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Lifetime");

    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        lifetimeLog.LogCritical(
            e.ExceptionObject as Exception,
            "The API is stopping because of an unhandled error. Terminating: {Terminating}.",
            e.IsTerminating);

    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        lifetimeLog.LogError(e.Exception, "A background task failed and nobody was waiting for it.");
        e.SetObserved();
    };

    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
        lifetimeLog.LogInformation("The API was asked to stop; shutting down normally."));

    // Which of the two OTP paths this deployment is on, said once at startup, so "nobody got a
    // code" can be answered from the first page of the log.
    var tawked = app.Services.GetRequiredService<IOptions<TawkedOptions>>().Value;

    lifetimeLog.LogInformation(
        tawked.IsConfigured
            ? "One-time codes to mobile numbers go through Tawked ({Mode} key). Email codes are not delivered."
            : "No Tawked key is configured, so no one-time code is delivered to anyone. {Mode}",
        tawked.IsConfigured
            ? (tawked.IsSandbox ? "sandbox" : "live")
            : "Sign-in and withdrawals will not work.");

    // Test numbers sign in with a fixed code, so being on is worth a line in every startup log —
    // they are meant for review and testing, and nobody should discover them by accident.
    var testNumbers = app.Services.GetRequiredService<IOptions<OtpOptions>>().Value.TestAccounts;
    if (testNumbers.Count > 0)
    {
        lifetimeLog.LogWarning(
            "{Count} test phone number(s) can sign in with a fixed code and no SMS: {Numbers}. " +
            "Remove Otp__TestAccounts__* to turn this off.",
            testNumbers.Count,
            string.Join(", ", testNumbers.Keys.Select(n => n.Length > 4 ? "***" + n[^4..] : "***")));
    }
}

// ---------------------------------------------------------------- pipeline

app.UseSerilogRequestLogging();

// The error envelope must wrap everything downstream, so it goes first.
app.UseMiddleware<ExceptionHandlingMiddleware>();

/*
 * The scheme and the client address, as the outside world saw them.
 *
 * Shared hosting terminates TLS at IIS, so by the time a request reaches the application it
 * arrived over plain HTTP and every address reads as 127.0.0.1. HSTS and the HTTPS redirect below
 * then see an insecure request that was already secure, and answer it with a redirect to the very
 * address it came from — a loop a browser can only leave by giving up.
 *
 * IIS publishes the original scheme in X-Forwarded-Proto, and that is what this reads to rebuild
 * the request as the client made it.
 *
 * The default trusted ranges are cleared because on this deployment the application is reachable
 * only through IIS, never directly, so there is no untrusted peer that could set the header to
 * forge a scheme. Behind a load balancer that is reachable in its own right, the proxy's address
 * has to be listed again instead of trusting everything.
 */
app.UseForwardedHeaders(BuildForwardedHeadersOptions());

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-XSS-Protection"] = "0";
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

/*
 * The API reference is a development tool. Swashbuckle builds the whole document from the
 * application's surface on first use and holds it, which costs memory a shared host does not have
 * to spare, and a public catalogue of every endpoint is not something to hand out. The admin
 * dashboard is the interface production uses. Api:EnableSwagger turns it back on where it is
 * wanted, and does not need a rebuild.
 */
if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Api:EnableSwagger", false))
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Auction Platform API v1");
        options.DocumentTitle = "Auction Platform API";
    });
}

// The admin dashboard is served as static files from wwwroot/admin.
//
// no-cache, not no-store: the browser may keep its copy, but it must ask whether that copy is
// still current before using it. Without this the console ships inside the API image while
// browsers carry on running the previous app.js, and the only cure is a manual hard reload.
// These files are a few hundred kilobytes, so a revalidation request costs nothing worth saving.
// Before the static files, not after.
//
// Uploaded pictures are served from here and are read by clients on another origin. A plain
// <img> can display a cross-origin image without permission, but a client that fetches the bytes
// itself — Flutter on the web does, because it draws them onto a canvas — needs the response to
// say who may read it. Placed after UseStaticFiles this policy never ran for them, so a picture
// that loaded perfectly well in the console failed silently in the app.
//
// Nothing is loosened: the same allowlist of origins applies, and the API's own endpoints are
// unaffected by the move.
app.UseCors("Default");

app.UseDefaultFiles();

// Hands over the `.br` / `.gz` copy the build made, when the browser accepts one. The app built
// for the web is megabytes of JavaScript and WebAssembly; uncompressed it is a slow first load on
// a phone.
app.UsePrecompressedStaticFiles();

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = new PrecompressedStaticFiles.ContentTypeProvider(),
    OnPrepareResponse = ctx =>
    {
        // An uploaded file is named after a fresh GUID, so that address can never point at
        // different bytes later. It is safe to cache for a long time. The console's own files
        // keep their address across releases, so those must be revalidated every time.
        ctx.Context.Response.Headers.CacheControl =
            ctx.Context.Request.Path.StartsWithSegments("/uploads")
                ? "public, max-age=31536000, immutable"
                : "no-cache, must-revalidate";
    }
});

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<AuctionHub>(AuctionHub.Route);

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");

// The mobile app, built for the web and served from here as an installable PWA. Its files live
// in wwwroot/app; `scripts/build-pwa` puts them there. Like the console, any address inside it
// resolves to its own index.html so a refresh deep in the app still works.
app.MapFallbackToFile("/app/{*path:nonfile}", "app/index.html");

// ---------------------------------------------------------------- startup work

await using (var scope = app.Services.CreateAsyncScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    if (app.Configuration.GetValue("Database:AutoMigrate", true))
    {
        try
        {
            await seeder.MigrateAsync();
            await seeder.SeedEssentialsAsync();

            // The only way a fresh Production deployment gets its first administrator.
            await seeder.PromoteBootstrapSuperAdminAsync(
                app.Configuration.GetValue<string>("Bootstrap:SuperAdminEmail"),
                app.Configuration.GetValue<string>("Bootstrap:SuperAdminPhone"));

            // The products the store actually sells. Unlike the demonstration data this runs in
            // Production — it is the catalogue, not a sample of one — and it is safe to run on
            // every start: it adds only what is missing and replaces the old shelves just once.
            if (app.Configuration.GetValue("Catalog:SeedWd", true))
            {
                await seeder.SeedWdCatalogAsync();
            }

            // The first sub-department, once: شواحن وكوابل السيارة under كيابل الشحن.
            if (app.Configuration.GetValue("Catalog:SeedCarCables", true))
            {
                await seeder.SeedCarCablesAsync();
            }

            // ADOMO CARE, beside it. Adds only; never takes anything off the shelf.
            if (app.Configuration.GetValue("Catalog:SeedAdomo", true))
            {
                await seeder.SeedAdomoCatalogAsync();
            }

            // Once: three main departments — مستحضرات تجميل، كوابل شحن، سماعات أذن.
            if (app.Configuration.GetValue("Catalog:SeedDepartmentLayout", true))
            {
                await seeder.SeedDepartmentLayoutAsync();
            }

            // Demo accounts use a well-known password, so they are never created in Production.
            if (!app.Environment.IsProduction() &&
                app.Configuration.GetValue("Database:SeedDevelopmentData", true))
            {
                await seeder.SeedDevelopmentDataAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialisation failed. The API will start, but requests that " +
                                "touch the database will fail until this is resolved.");
        }
    }
}

app.Run();

/*
 * The forwarded-header policy, in one place.
 *
 * The trusted ranges are emptied on purpose. They exist to stop a client that can reach the
 * application directly from claiming it arrived over HTTPS; on this deployment only IIS can, so
 * there is no such client. Behind a load balancer that is itself reachable, that proxy's address
 * belongs here instead of trusting every peer.
 */
static ForwardedHeadersOptions BuildForwardedHeadersOptions()
{
    var options = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor
    };

    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();

    return options;
}

/// <summary>Exposed so the integration test host can reference the entry point assembly.</summary>
public partial class Program { }
