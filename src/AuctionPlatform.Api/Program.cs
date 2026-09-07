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
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- logging

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "AuctionPlatform.Api")
    .WriteTo.Console()
    .WriteTo.File("logs/api-.log", rollingInterval: Serilog.RollingInterval.Day, retainedFileCountLimit: 14));

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

var app = builder.Build();

// ---------------------------------------------------------------- pipeline

app.UseSerilogRequestLogging();

// The error envelope must wrap everything downstream, so it goes first.
app.UseMiddleware<ExceptionHandlingMiddleware>();

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

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Auction Platform API v1");
    options.DocumentTitle = "Auction Platform API";
});

// The admin dashboard is served as static files from wwwroot/admin.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("Default");
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<AuctionHub>(AuctionHub.Route);

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");

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

/// <summary>Exposed so the integration test host can reference the entry point assembly.</summary>
public partial class Program { }
