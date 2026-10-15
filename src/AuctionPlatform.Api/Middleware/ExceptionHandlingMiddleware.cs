using System.Text.Json;
using AuctionPlatform.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Api.Middleware;

/// <summary>The single error envelope every endpoint returns.</summary>
public sealed record ApiError(string Code, string Message, IReadOnlyList<string> Details)
{
    public static ApiError Create(string code, string message, IReadOnlyList<string>? details = null) =>
        new(code, message, details ?? Array.Empty<string>());
}

/// <summary>
/// Translates exceptions into the documented error contract. Business rule violations carry their
/// own stable code; anything unexpected is logged in full and reported as a generic internal error
/// so internals never leak to clients.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        if (context.Response.HasStarted)
        {
            _logger.LogError(exception, "The response had already started; cannot write an error body.");
            return;
        }

        var (status, error) = Map(exception);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled error on {Method} {Path}.",
                context.Request.Method, context.Request.Path);
        }
        else
        {
            _logger.LogInformation("{Code} on {Method} {Path}: {Message}",
                error.Code, context.Request.Method, context.Request.Path, error.Message);
        }

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(error, JsonOptions));
    }

    private (int Status, ApiError Error) Map(Exception exception) => exception switch
    {
        DomainException domain => (StatusForCode(domain.Code),
            ApiError.Create(domain.Code, domain.Message, domain.Details)),

        UnauthorizedAccessException => (StatusCodes.Status403Forbidden,
            ApiError.Create(ErrorCodes.Forbidden, "You do not have permission to perform this action.")),

        // A unique-index violation that escaped a service means a genuine conflict.
        DbUpdateException db when IsUniqueViolation(db) => (StatusCodes.Status409Conflict,
            ApiError.Create(ErrorCodes.Conflict, "The request conflicts with existing data.")),

        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict,
            ApiError.Create(ErrorCodes.Conflict, "The record was modified by another request. Please retry.")),

        OperationCanceledException => (499, ApiError.Create("REQUEST_CANCELLED", "The request was cancelled.")),

        // The database is not answering — usually because it is still starting, or has been
        // stopped. That is not the caller's mistake and it is not a bug in the request, so it is
        // reported as a service that is temporarily away rather than as an internal failure.
        _ when IsDatabaseUnreachable(exception) => (StatusCodes.Status503ServiceUnavailable,
            ApiError.Create(
                ErrorCodes.ServiceUnavailable,
                "The service is temporarily unavailable. Please try again in a moment.")),

        _ => (StatusCodes.Status500InternalServerError, ApiError.Create(
            ErrorCodes.InternalError,
            _environment.IsDevelopment() ? exception.Message : "An unexpected error occurred."))
    };

    private static int StatusForCode(string code) => code switch
    {
        ErrorCodes.AuctionNotFound or ErrorCodes.ParticipantNotFound or ErrorCodes.ProductNotFound
            or ErrorCodes.OrderNotFound or ErrorCodes.CategoryNotFound or ErrorCodes.WalletNotFound
            or ErrorCodes.PrizeClaimNotFound or ErrorCodes.NotFound
            => StatusCodes.Status404NotFound,

        ErrorCodes.Unauthorized or ErrorCodes.InvalidCredentials or ErrorCodes.InvalidRefreshToken
            => StatusCodes.Status401Unauthorized,

        ErrorCodes.Forbidden or ErrorCodes.AccountDisabled
            or ErrorCodes.ShoppingWalletOperationNotAllowed
            => StatusCodes.Status403Forbidden,

        ErrorCodes.AlreadyJoined or ErrorCodes.DuplicateRequest or ErrorCodes.Conflict
            or ErrorCodes.TargetAlreadyReached or ErrorCodes.EmailAlreadyRegistered
            or ErrorCodes.DuplicateSku or ErrorCodes.AuctionFull
            or ErrorCodes.ConsecutiveBidNotAllowed
            => StatusCodes.Status409Conflict,

        ErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,

        // Something the platform depends on is not answering — the database, or the provider that
        // sends verification codes. A service that is briefly away is not a bad request, and a
        // client that retries on 503 but gives up on 400 has to be able to tell them apart.
        ErrorCodes.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,

        // Everything else is a well-formed request the platform's rules decline: 400.
        _ => StatusCodes.Status400BadRequest
    };

    /// <summary>
    /// True when the failure is "could not reach PostgreSQL" rather than anything about the data.
    ///
    /// Npgsql wraps this differently depending on where it happens — a broken socket, a pool that
    /// cannot open a connection, or EF's transient-failure wrapper — so the whole chain is walked
    /// rather than matching one type.
    /// </summary>
    private static bool IsDatabaseUnreachable(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            var name = ex.GetType().Name;

            if (ex is System.Net.Sockets.SocketException) return true;
            if (name is "NpgsqlException" or "PostgresException" &&
                ex.InnerException is System.Net.Sockets.SocketException) return true;
            if (name == "NpgsqlException" && ex.Message.Contains("Failed to connect")) return true;
        }

        return false;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.GetType().Name == "PostgresException" &&
        (ex.InnerException.Data["SqlState"] as string) == "23505";
}
