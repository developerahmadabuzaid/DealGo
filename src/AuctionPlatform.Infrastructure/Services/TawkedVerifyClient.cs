using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>What <c>POST /v1/verify/start</c> gives back: an id to check the code against.</summary>
public sealed record TawkedVerification(string Id, DateTime ExpiresAt);

/// <summary>
/// The outcome of <c>POST /v1/verify/check</c>.
///
/// Tawked answers <c>200</c> for all of these — a wrong code is an answer, not an HTTP error —
/// so the caller must read the status rather than the status code.
/// </summary>
public enum TawkedCheckOutcome
{
    Verified,
    InvalidCode,
    TooManyAttempts,
    Expired,
    Canceled,

    /// <summary>The send itself failed, or the attempts were already spent.</summary>
    Failed,

    /// <summary>Unknown id, or an id belonging to another account.</summary>
    NotFound
}

public interface ITawkedVerifyClient
{
    /// <summary>False when no API key is configured, which sends codes back down the local path.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Asks Tawked to send a code to a Saudi mobile number and returns the id it will be checked
    /// against. <paramref name="idempotencyKey"/> makes a retry after a timeout safe: the same key
    /// returns the first answer without sending or charging again.
    /// </summary>
    Task<TawkedVerification> StartAsync(
        string phoneE164,
        string language,
        string reference,
        string? clientIp,
        string idempotencyKey,
        CancellationToken ct = default);

    Task<TawkedCheckOutcome> CheckAsync(string verificationId, string code, CancellationToken ct = default);
}

/// <summary>
/// Talks to Tawked Verify (https://docs.tawked.com).
///
/// Two calls: one sends a code, one checks it. Tawked generates the code, so this is not a
/// transport sitting behind a code of ours — for an SMS destination Tawked holds the code, while
/// what the code is allowed to authorise stays entirely on our side of the line.
///
/// Every failure that is *our* problem — no balance, a revoked key, an application still in
/// review, a paused application — is reported to the caller as "temporarily unavailable" and
/// logged as critical. The person trying to sign in must never read our billing state, and we
/// must never learn about it from a customer.
/// </summary>
public sealed class TawkedVerifyClient : ITawkedVerifyClient
{
    /// <summary>Tawked speaks snake_case: `expires_at`, `attempts_remaining`, `client_ip`.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly TawkedOptions _options;
    private readonly ILogger<TawkedVerifyClient> _logger;

    public TawkedVerifyClient(
        HttpClient http,
        IOptions<TawkedOptions> options,
        ILogger<TawkedVerifyClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = http;

        _http.BaseAddress ??= new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        if (_options.IsConfigured)
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public bool IsEnabled => _options.IsConfigured;

    public async Task<TawkedVerification> StartAsync(
        string phoneE164,
        string language,
        string reference,
        string? clientIp,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var body = new StartRequest(
            To: phoneE164,
            Channel: "sms",
            // Only the literal "en" selects English; everything else is Arabic, which is our default.
            Lang: language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar",
            Application: _options.Application,
            Reference: reference,
            // The one signal that lets Tawked's guard tell many users apart from one abuser. It is
            // never inferred from the request on their side, so it has to be sent explicitly.
            ClientIp: clientIp,
            Autofill: string.IsNullOrWhiteSpace(_options.Autofill) ? null : _options.Autofill);

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/verify/start")
        {
            Content = JsonContent.Create(body, options: Json)
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        var response = await SendAsync(request, "start", ct);

        var started = await ReadAsync<StartResponse>(response, "start", ct);

        if (string.IsNullOrWhiteSpace(started.Id))
        {
            _logger.LogCritical("Tawked accepted a code request but returned no id. {Request}", RequestId(response));
            throw Unavailable();
        }

        _logger.LogInformation(
            "Tawked is sending a code, verification {VerificationId}{Replayed}.",
            started.Id,
            response.Headers.Contains("Idempotent-Replayed") ? " (a replay, nothing re-sent)" : string.Empty);

        return new TawkedVerification(
            started.Id,
            // A missing expiry would make the code look immortal, so fall back to their shortest
            // configurable lifetime rather than to DateTime.MinValue or to "never".
            started.ExpiresAt ?? DateTime.UtcNow.AddMinutes(5));
    }

    public async Task<TawkedCheckOutcome> CheckAsync(
        string verificationId, string code, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/verify/check")
        {
            Content = JsonContent.Create(
                new CheckRequest(verificationId, code, _options.Application), options: Json)
        };

        var response = await SendAsync(request, "check", ct, allowNotFound: true);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Tawked does not know verification {VerificationId}.", verificationId);
            return TawkedCheckOutcome.NotFound;
        }

        var result = await ReadAsync<CheckResponse>(response, "check", ct);

        // `verified` is the decision; `status` only says why not. The boolean is read first on
        // purpose: a status name we have never seen must never read as a successful verification.
        if (result.Verified) return TawkedCheckOutcome.Verified;

        switch (result.Status)
        {
            case "invalid_code": return TawkedCheckOutcome.InvalidCode;
            case "too_many_attempts": return TawkedCheckOutcome.TooManyAttempts;
            case "expired": return TawkedCheckOutcome.Expired;
            case "canceled": return TawkedCheckOutcome.Canceled;
            case "failed": return TawkedCheckOutcome.Failed;

            default:
                // Anything unrecognised is a refusal, never a pass.
                _logger.LogWarning(
                    "Tawked returned the unrecognised check status {Status}; treating it as a refusal.",
                    result.Status);
                return TawkedCheckOutcome.Failed;
        }
    }

    // ------------------------------------------------------------------ transport

    /// <summary>
    /// Sends the request and turns everything that is not a usable answer into a
    /// <see cref="DomainException"/> the API already knows how to render.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string operation, CancellationToken ct, bool allowNotFound = false)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException(
                "Tawked is not configured; check ITawkedVerifyClient.IsEnabled before calling it.");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A timeout is the case the idempotency key exists for: the code may well have gone
            // out, so a retry must not send a second one.
            _logger.LogError(ex, "Could not reach Tawked to {Operation} a code.", operation);
            throw Unavailable();
        }

        if (response.IsSuccessStatusCode) return response;
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return response;

        var error = await ReadErrorAsync(response, ct);
        throw Translate(response.StatusCode, error, operation, RequestId(response));
    }

    /// <summary>
    /// Maps a Tawked refusal onto our own error contract, and says so in the log.
    ///
    /// The split that matters is *whose* problem it is. A bad destination, or one phone asking for
    /// too many codes, is the caller's, and the answer says exactly that. An empty balance, an
    /// expired key or an application still in review is ours: the caller is told only that the
    /// service is briefly away, and the log is told loudly, because nobody is coming to fix it
    /// otherwise.
    /// </summary>
    private DomainException Translate(
        HttpStatusCode status, string? error, string operation, string requestId)
    {
        var (forCaller, ourFault) = Classify(status, error);

        if (ourFault)
        {
            _logger.LogCritical(
                "Tawked refused to {Operation} a code: {Status} {Error}. No verification code can be " +
                "sent until this is fixed — it is our account's problem, not the caller's. {Hint} {RequestId}",
                operation, (int)status, error ?? "no error code", Hint(error), requestId);
        }
        else
        {
            _logger.LogWarning(
                "Tawked refused to {Operation} a code: {Status} {Error}. {RequestId}",
                operation, (int)status, error ?? "no error code", requestId);
        }

        return forCaller;
    }

    private static (DomainException ForCaller, bool OurFault) Classify(
        HttpStatusCode status, string? error) => (status, error) switch
    {
        /*
         * The sandbox's own limits, which are about our key and never about the caller.
         *
         * A test key reaches exactly one phone — the account owner's — and sends at most ten codes
         * in its life. Both arrive as ordinary 422 and 429 answers, and they used to be read as
         * "that number is not valid", so a customer with a perfectly good number was told it was
         * wrong. They are matched by name, before any rule that looks only at the status.
         */
        (_, "sandbox_unverified_destination") or (_, "sandbox_quota_exceeded")
            or (_, "sandbox_unsupported_channel") => (Unavailable(), true),

        // Only the provider's explicit verdict on the number itself. Any other 422 is a refusal
        // whose reason is not the caller's number, and saying it was would send them to fix a
        // number that is fine.
        (_, "invalid_destination") =>
            (new DomainException(
                ErrorCodes.OtpDestinationInvalid, "That mobile number is not a valid Saudi number."), false),

        // Our daily spend cap, which also arrives as a 429 — so it has to be matched before the
        // general one below. The caller did not ask for too many codes; we ran out of room. Saying
        // "too many requests" here would blame them for our setting.
        (_, "spend_cap_reached") => (Unavailable(), true),

        // This phone has asked for too many codes, or this IP has. The caller can act on it.
        (_, "rate_limited") or (_, "ip_rate_limited") or (_, "resend_limit_reached") =>
            (TooManyCodes(), false),

        // Our own key hitting its per-minute ceiling is our capacity problem, but waiting is still
        // the only thing the caller can do about it.
        (HttpStatusCode.TooManyRequests, _) => (TooManyCodes(), true),

        // Everything left — no balance, a dead key, an unapproved or paused application, a 5xx —
        // is ours, and none of it is any of the caller's business.
        _ => (Unavailable(), true)
    };

    /// <summary>What the person reading the log at 2am needs to do next.</summary>
    private static string Hint(string? error) => error switch
    {
        "insufficient_credits" => "Top up the Tawked balance under Wallet in the console.",
        "sandbox_unverified_destination" =>
            "This is a test key (tk_test_): it can only send to the phone the Tawked account was " +
            "opened with. Use a live key (tk_live_) to reach any other number.",
        "sandbox_quota_exceeded" =>
            "The test key has used its 10 lifetime sends. Use a live key (tk_live_).",
        "sandbox_unsupported_channel" => "A test key sends SMS only.",
        "spend_cap_reached" => "The daily spend cap is reached; raise it in the Tawked console.",
        "application_paused" =>
            "Tawked auto-paused the application after a spike in new destinations; resume it in the console.",
        "account_not_active" => "The application is not approved yet, or the account is suspended.",
        "unauthorized" or "key_expired" => "The API key is unknown, revoked or expired; issue a new one.",
        "ip_not_allowed" => "This server's outbound IP is not on the key's allowlist.",
        "insufficient_scope" => "The key does not have the scope for this call.",
        "service_required" or "service_not_found" =>
            "A partner key needs Tawked:Application set to a provisioned application.",
        _ => "See https://docs.tawked.com/errors."
    };

    private static DomainException TooManyCodes() => new(
        ErrorCodes.RateLimited, "Too many verification codes requested. Please try again later.");

    private static DomainException Unavailable() => new(
        ErrorCodes.ServiceUnavailable,
        "Verification codes are briefly unavailable. Please try again in a moment.");

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new JsonException("The body was empty.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex,
                "Could not read Tawked's answer to {Operation}. {RequestId}", operation, RequestId(response));
            throw Unavailable();
        }
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<ErrorResponse>(Json, ct))?.Error;
        }
        catch
        {
            // A non-JSON body (a gateway's HTML error page) tells us nothing more than the status.
            return null;
        }
    }

    /// <summary>Tawked's own id for the exchange. Support asks for it, so it goes in every log line.</summary>
    private static string RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Request-Id", out var values)
            ? $"Tawked request {string.Join(',', values)}."
            : "No Tawked request id.";

    // ------------------------------------------------------------------ wire shapes

    private sealed record StartRequest(
        string To,
        string Channel,
        string Lang,
        string? Application,
        string Reference,
        string? ClientIp,
        string? Autofill);

    private sealed record StartResponse(string Id, string? Status, DateTime? ExpiresAt);

    private sealed record CheckRequest(string Id, string Code, string? Application);

    private sealed record CheckResponse(bool Verified, string? Status, int? AttemptsRemaining);

    private sealed record ErrorResponse(string? Error, string? Message);
}
