using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Identity;

/// <summary>
/// Issues and verifies one-time codes.
///
/// The design mirrors how refresh tokens are handled: only a hash is stored, the code is
/// single-use, and it is scoped so it cannot be replayed for a different purpose. Codes are
/// generated with a cryptographic RNG and compared in constant time.
/// </summary>
public sealed partial class OtpService : IOtpService
{
    private readonly ApplicationDbContext _db;
    private readonly IOtpSender _sender;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUserService _currentUser;
    private readonly OtpOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<OtpService> _logger;

    public OtpService(
        ApplicationDbContext db,
        IOtpSender sender,
        IDateTimeProvider clock,
        ICurrentUserService currentUser,
        IOptions<OtpOptions> options,
        IHostEnvironment environment,
        ILogger<OtpService> logger)
    {
        _db = db;
        _sender = sender;
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

        var code = GenerateCode(_options.CodeLength);
        var expiresAt = now.AddSeconds(_options.ValidForSeconds);

        _db.OtpCodes.Add(new OtpCode
        {
            Destination = destination,
            Channel = channel,
            Purpose = request.Purpose,
            UserId = request.UserId,
            CodeHash = Hash(code, destination, request.Purpose),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            OperationBinding = request.OperationBinding,
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent
        });

        await _db.SaveChangesAsync(ct);

        await _sender.SendAsync(
            destination, channel, request.Purpose, code,
            TimeSpan.FromSeconds(_options.ValidForSeconds), request.Language, ct);

        _logger.LogInformation(
            "Issued a {Purpose} code to {Channel} destination {Masked}, valid for {Seconds}s.",
            request.Purpose, channel, Mask(destination, channel), _options.ValidForSeconds);

        // Echoing the code keeps the flow testable without an SMS or email provider. The decision
        // is made here, once, rather than at each call site, so a new endpoint cannot leak it by
        // forgetting to strip it.
        var exposeCode = _options.ExposeCodeInDevelopment && !_environment.IsProduction();

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

        var expected = Hash(verification.Code.Trim(), destination, verification.Purpose);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(candidate.CodeHash)))
        {
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

        // A code bound to an operation only approves that operation.
        if (candidate.OperationBinding is not null &&
            candidate.OperationBinding != verification.OperationBinding)
        {
            candidate.AttemptCount += 1;
            await _db.SaveChangesAsync(ct);

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

        candidate.ConsumedAt = now;
        await _db.SaveChangesAsync(ct);

        return new OtpVerificationResult(candidate.Id, candidate.UserId, candidate.Purpose);
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

        var phone = new string(value.Where(c => char.IsDigit(c) || c == '+').ToArray());

        if (phone.Length < 7)
        {
            throw new DomainException(ErrorCodes.OtpDestinationInvalid, "That phone number is not valid.");
        }

        return (phone, OtpChannel.Sms);
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
