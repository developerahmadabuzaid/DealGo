using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Identity;

/// <summary>
/// Issues and verifies one-time codes.
///
/// Two kinds of code pass through here, and the difference is only about who holds the secret:
///
///   * **A mobile number, with Tawked configured.** Tawked issues the code, delivers the SMS and
///     checks it. It generates the code itself and will not accept one of ours, so there is no
///     hash of ours to keep — the row records which verification at Tawked answers for this code.
///   * **Anything else.** The code is ours: generated with a cryptographic RNG, stored only as a
///     salted hash the way refresh tokens are, and compared in constant time. This is the path an
///     email destination takes, because Tawked serves Saudi mobile numbers only, and the path
///     everything takes when no key is configured.
///
/// What a code is *allowed to authorise* never moves. The purpose, the account it belongs to and
/// the operation it is bound to are checked here, against our own row, before the code itself is
/// ever put to the provider.
/// </summary>
public sealed partial class OtpService : IOtpService
{
    private readonly ApplicationDbContext _db;
    private readonly IOtpSender _sender;
    private readonly ITawkedVerifyClient _tawked;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUserService _currentUser;
    private readonly OtpOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<OtpService> _logger;

    public OtpService(
        ApplicationDbContext db,
        IOtpSender sender,
        ITawkedVerifyClient tawked,
        IDateTimeProvider clock,
        ICurrentUserService currentUser,
        IOptions<OtpOptions> options,
        IHostEnvironment environment,
        ILogger<OtpService> logger)
    {
        _db = db;
        _sender = sender;
        _tawked = tawked;
        _clock = clock;
        _currentUser = currentUser;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task<OtpChallengeDto> RequestAsync(OtpRequest request, CancellationToken ct = default)
    {
        var (destination, channel) = Normalise(request.Destination);
        var now = _clock.UtcNow;

        await EnforceRequestLimitsAsync(destination, request.Purpose, now, ct);

        // Only one code may be live per destination and purpose: requesting a new one retires the
        // old, so an intercepted earlier code is dead the moment the user asks again.
        await _db.OtpCodes
            .Where(o => o.Destination == destination &&
                        o.Purpose == request.Purpose &&
                        o.ConsumedAt == null &&
                        o.InvalidatedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.InvalidatedAt, now)
                .SetProperty(o => o.InvalidatedReason, "Superseded by a newer code"), ct);

        // A configured test number signs in with its own fixed code: nothing is sent and nothing
        // is charged. See TestCodeFor for why this is limited to signing in.
        var testCode = TestCodeFor(destination, channel, request.Purpose);

        // Tawked delivers to Saudi mobile numbers. An email destination, a test number, and
        // everything at all when no key is configured, keeps the code on our side.
        var useProvider = testCode is null && channel == OtpChannel.Sms && _tawked.IsEnabled;

        // The row's id is settled before anything is sent, so it can serve as the idempotency key
        // at the provider too: a retry after a timeout returns the first answer instead of
        // sending — and charging for — a second message.
        var id = Guid.NewGuid();

        string? providerVerificationId = null;
        string? code = null;
        var codeHash = string.Empty;
        var expiresAt = now.AddSeconds(_options.ValidForSeconds);

        if (useProvider)
        {
            // Deliberately before the row is written. If this throws, nothing was sent and nothing
            // is recorded, so the user may try again at once; writing the row first would leave a
            // dead code behind and the resend cooldown would then refuse the retry.
            var verification = await _tawked.StartAsync(
                destination,
                request.Language,
                reference: $"{request.Purpose}:{id:N}",
                clientIp: _currentUser.IpAddress,
                idempotencyKey: id.ToString("N"),
                ct);

            providerVerificationId = verification.Id;

            // The provider owns the lifetime, so the row records theirs rather than ours.
            expiresAt = verification.ExpiresAt;
        }
        else
        {
            // The fixed code is stored exactly as a generated one would be — a salted hash — so
            // checking it is the ordinary check, with its attempt limit and its expiry.
            code = testCode ?? GenerateCode(_options.CodeLength);
            codeHash = Hash(code, destination, request.Purpose);
        }

        _db.OtpCodes.Add(new OtpCode
        {
            Id = id,
            Destination = destination,
            Channel = channel,
            Purpose = request.Purpose,
            UserId = request.UserId,
            CodeHash = codeHash,
            ProviderVerificationId = providerVerificationId,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            OperationBinding = request.OperationBinding,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent
        });

        await _db.SaveChangesAsync(ct);

        if (testCode is not null)
        {
            // Every use is said out loud, so a test number signing in on the live site is never
            // something nobody can see.
            _logger.LogWarning(
                "Test number {Masked} asked for a sign-in code; its fixed code applies and nothing was sent.",
                Mask(destination, channel));
        }
        else if (code is not null)
        {
            await _sender.SendAsync(
                destination, channel, request.Purpose, code,
                TimeSpan.FromSeconds(_options.ValidForSeconds), request.Language, ct);
        }

        _logger.LogInformation(
            "Issued a {Purpose} code to {Channel} destination {Masked} via {Issuer}, valid until {ExpiresAt:O}.",
            request.Purpose, channel, Mask(destination, channel),
            useProvider ? "Tawked" : "this server", expiresAt);

        // Echoing the code keeps the flow testable without a provider. The decision is made here,
        // once, rather than at each call site, so a new endpoint cannot leak it by forgetting to
        // strip it — and there is nothing to echo when the provider holds the code.
        // Never for a test number: its code is a password, and the answer to a request is not
        // the place to repeat one.
        var exposeCode = code is not null
                         && testCode is null
                         && _options.ExposeCodeInDevelopment
                         && !_environment.IsProduction();

        return new OtpChallengeDto(
            Mask(destination, channel), channel, expiresAt,
            _options.CodeLength, _options.ResendCooldownSeconds,
            DevelopmentCode: exposeCode ? code : null);
    }

    public async Task<OtpVerificationResult> VerifyAsync(
        OtpVerification verification, CancellationToken ct = default)
    {
        var (destination, _) = Normalise(verification.Destination);
        var now = _clock.UtcNow;

        if (string.IsNullOrWhiteSpace(verification.Code))
        {
            throw new DomainException(ErrorCodes.OtpRequired, "A verification code is required.");
        }

        var candidate = await _db.OtpCodes
            .Where(o => o.Destination == destination && o.Purpose == verification.Purpose)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (candidate is null)
        {
            throw new DomainException(ErrorCodes.InvalidOtp, "Invalid verification code.");
        }

        if (candidate.ConsumedAt is not null)
        {
            throw new DomainException(ErrorCodes.OtpAlreadyUsed, "This code has already been used.");
        }

        if (candidate.AttemptCount >= _options.MaxAttempts || candidate.InvalidatedAt is not null)
        {
            throw new DomainException(
                ErrorCodes.OtpTooManyAttempts, "This code is no longer valid. Please request a new one.");
        }

        if (now >= candidate.ExpiresAt)
        {
            throw new DomainException(ErrorCodes.OtpExpired, "This code has expired. Please request a new one.");
        }

        /*
         * What the code is allowed to authorise is settled first, before the code itself is put to
         * anybody.
         *
         * These two checks used to run after the code was compared. They now run before it, for
         * one reason: when a provider holds the code, comparing it means spending one of a small
         * number of attempts at that provider. Neither of these is a secret being guessed — both
         * are derived from the caller's own request — so refusing early gives an attacker nothing
         * and stops a mismatched request from burning the real user's attempts.
         */

        // A code bound to an operation only approves that operation: a code obtained for a small
        // transfer to one account cannot approve a large one to another.
        if (candidate.OperationBinding is not null &&
            candidate.OperationBinding != verification.OperationBinding)
        {
            throw new DomainException(
                ErrorCodes.InvalidOtp,
                "This code was issued for a different request. Please request a new one.");
        }

        // Requesting a code while signed in binds it to that account.
        if (candidate.UserId is not null &&
            verification.UserId is not null &&
            candidate.UserId != verification.UserId)
        {
            throw new DomainException(ErrorCodes.InvalidOtp, "Invalid verification code.");
        }

        if (candidate.ProviderVerificationId is { Length: > 0 } providerVerificationId)
        {
            await VerifyAtProviderAsync(candidate, providerVerificationId, verification, destination, now, ct);
        }
        else
        {
            await VerifyLocallyAsync(candidate, verification, destination, now, ct);
        }

        candidate.ConsumedAt = now;
        await _db.SaveChangesAsync(ct);

        return new OtpVerificationResult(candidate.Id, candidate.UserId, candidate.Purpose);
    }

    /// <summary>
    /// Puts the code to the provider that issued it.
    ///
    /// The provider counts the attempts, because it is the only side that can: it holds the code.
    /// Our own attempt counter is still advanced, so the row tells the same story either way and
    /// the audit trail does not go quiet just because the secret moved.
    /// </summary>
    private async Task VerifyAtProviderAsync(
        OtpCode candidate,
        string providerVerificationId,
        OtpVerification verification,
        string destination,
        DateTime now,
        CancellationToken ct)
    {
        // Sent exactly as typed: the provider compares it verbatim, so trimming here would make a
        // code with a stray space pass our side and fail theirs.
        var outcome = await _tawked.CheckAsync(providerVerificationId, verification.Code, ct);

        if (outcome == TawkedCheckOutcome.Verified) return;

        candidate.AttemptCount += 1;

        var (retire, reason, error) = outcome switch
        {
            TawkedCheckOutcome.InvalidCode => (
                candidate.AttemptCount >= _options.MaxAttempts, "Too many failed attempts",
                new DomainException(ErrorCodes.InvalidOtp, "Invalid verification code.")),

            TawkedCheckOutcome.TooManyAttempts => (
                true, "Too many failed attempts",
                new DomainException(
                    ErrorCodes.OtpTooManyAttempts,
                    "This code is no longer valid. Please request a new one.")),

            TawkedCheckOutcome.Expired => (
                true, "Expired at the provider",
                new DomainException(
                    ErrorCodes.OtpExpired, "This code has expired. Please request a new one.")),

            // Cancelled, the send failed, or the provider said something we do not recognise.
            // There is no code behind this row any more, so it is retired rather than left to be
            // guessed at.
            _ => (
                true, $"The provider closed the verification ({outcome})",
                new DomainException(
                    ErrorCodes.InvalidOtp, "This code is no longer valid. Please request a new one."))
        };

        if (retire)
        {
            candidate.InvalidatedAt = now;
            candidate.InvalidatedReason = reason;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Tawked refused a {Purpose} code for {Masked}: {Outcome}. Attempt {Attempt}/{Max}.",
            verification.Purpose, Mask(destination, candidate.Channel), outcome,
            candidate.AttemptCount, _options.MaxAttempts);

        throw error;
    }

    /// <summary>Compares the code against our own salted hash, in constant time.</summary>
    private async Task VerifyLocallyAsync(
        OtpCode candidate,
        OtpVerification verification,
        string destination,
        DateTime now,
        CancellationToken ct)
    {
        var expected = Hash(verification.Code.Trim(), destination, verification.Purpose);

        if (CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(candidate.CodeHash)))
        {
            return;
        }

        // Count the wrong guess, and retire the code once the budget is spent.
        candidate.AttemptCount += 1;

        if (candidate.AttemptCount >= _options.MaxAttempts)
        {
            candidate.InvalidatedAt = now;
            candidate.InvalidatedReason = "Too many failed attempts";
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Failed {Purpose} code attempt {Attempt}/{Max} for {Masked}.",
            verification.Purpose, candidate.AttemptCount, _options.MaxAttempts,
            Mask(destination, candidate.Channel));

        throw new DomainException(ErrorCodes.InvalidOtp, "Invalid verification code.");
    }

    /// <summary>
    /// Throttles both the resend gap and the hourly volume, so the endpoint cannot be used to spam
    /// someone's phone or run up an SMS bill.
    /// </summary>
    private async Task EnforceRequestLimitsAsync(
        string destination, OtpPurpose purpose, DateTime now, CancellationToken ct)
    {
        var latest = await _db.OtpCodes
            .AsNoTracking()
            .Where(o => o.Destination == destination && o.Purpose == purpose)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => (DateTime?)o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (latest is { } last)
        {
            var elapsed = (now - last).TotalSeconds;
            if (elapsed < _options.ResendCooldownSeconds)
            {
                throw new DomainException(
                    ErrorCodes.OtpRequestTooSoon,
                    $"Please wait {Math.Ceiling(_options.ResendCooldownSeconds - elapsed)} seconds before requesting another code.");
            }
        }

        var hourAgo = now.AddHours(-1);
        var recent = await _db.OtpCodes
            .AsNoTracking()
            .CountAsync(o => o.Destination == destination && o.CreatedAt >= hourAgo, ct);

        if (recent >= _options.MaxRequestsPerHour)
        {
            throw new DomainException(
                ErrorCodes.RateLimited, "Too many verification codes requested. Please try again later.");
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The fixed code for a configured test number, or null for everyone else.
    ///
    /// <para>
    /// <b>Signing in only.</b> A withdrawal, a prize payment or a change of contact details from a
    /// test number still needs a real code. A test account's code is written in a settings file and
    /// handed to store reviewers; if it could also approve a withdrawal, anyone holding it could
    /// move money.
    /// </para>
    /// <para>
    /// Numbers are compared in canonical form, so 0500000001, 500000001 and +966500000001 in the
    /// settings all mean the same number. An entry that is not a Saudi mobile, or whose code is not
    /// 4 to 8 digits, is ignored rather than trusted.
    /// </para>
    /// </summary>
    private string? TestCodeFor(string destination, OtpChannel channel, OtpPurpose purpose)
    {
        if (channel != OtpChannel.Sms || purpose != OtpPurpose.Login) return null;

        foreach (var (number, code) in _options.TestAccounts)
        {
            if (!SaudiPhone.TryNormalise(number, out var canonical) || canonical != destination) continue;

            var trimmed = code?.Trim() ?? string.Empty;
            if (trimmed.Length is >= 4 and <= 8 && trimmed.All(char.IsAsciiDigit)) return trimmed;

            _logger.LogError(
                "Test number {Masked} is configured with a code that is not 4 to 8 digits; ignoring it.",
                Mask(destination, channel));
            return null;
        }

        return null;
    }

    /// <summary>Cryptographically random digits, uniform across the range (no modulo bias).</summary>
    private static string GenerateCode(int length)
    {
        var digits = new char[length];
        for (var i = 0; i < length; i++)
        {
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }
        return new string(digits);
    }

    /// <summary>
    /// Salting with the destination and purpose means an identical code for a different
    /// destination or purpose hashes differently, so hashes cannot be matched across records.
    /// </summary>
    private static string Hash(string code, string destination, OtpPurpose purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{purpose}:{destination}:{code}"));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Decides the channel from the destination's shape and normalises it, so
    /// "+966 50 123 4567" and "+966501234567" are the same destination.
    /// </summary>
    internal static (string Destination, OtpChannel Channel) Normalise(string raw)
    {
        var value = (raw ?? string.Empty).Trim();

        if (value.Length == 0)
        {
            throw new DomainException(
                ErrorCodes.OtpDestinationInvalid, "An email address or phone number is required.");
        }

        if (value.Contains('@'))
        {
            var email = value.ToLowerInvariant();
            if (!EmailPattern().IsMatch(email))
            {
                throw new DomainException(ErrorCodes.OtpDestinationInvalid, "That email address is not valid.");
            }
            return (email, OtpChannel.Email);
        }

        // Only Saudi mobile numbers are accepted, and always in one canonical form.
        return (SaudiPhone.Normalise(value), OtpChannel.Sms);
    }

    /// <summary>Masks the destination for display and logs, so a full address never leaks.</summary>
    internal static string Mask(string destination, OtpChannel channel)
    {
        if (channel == OtpChannel.Email)
        {
            var at = destination.IndexOf('@');
            if (at <= 0) return "***";

            var name = destination[..at];
            var domain = destination[at..];
            var head = name.Length <= 2 ? name[..1] : name[..2];

            return $"{head}{new string('*', Math.Max(3, name.Length - head.Length))}{domain}";
        }

        return destination.Length <= 4
            ? new string('*', destination.Length)
            : $"{new string('*', destination.Length - 4)}{destination[^4..]}";
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
