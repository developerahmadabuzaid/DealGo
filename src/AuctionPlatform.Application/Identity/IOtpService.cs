using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Application.Identity;

/// <summary>
/// Issues and verifies one-time codes.
///
/// Codes are single-use, short-lived, scoped to one purpose, rate limited on both request and
/// verification, and stored only as hashes.
/// </summary>
public interface IOtpService
{
    /// <summary>
    /// Issues a code for a destination and purpose, retiring any code still outstanding for that
    /// pair. Returns metadata only — the code itself goes to the user through <see cref="IOtpSender"/>.
    /// </summary>
    Task<OtpChallengeDto> RequestAsync(OtpRequest request, CancellationToken ct = default);

    /// <summary>
    /// Consumes a code. Throws on an invalid, expired, already-used or over-attempted code, and
    /// returns the account it belongs to on success.
    /// </summary>
    Task<OtpVerificationResult> VerifyAsync(OtpVerification verification, CancellationToken ct = default);
}

public sealed record OtpRequest
{
    /// <summary>An email address or a phone number. The channel is inferred from its shape.</summary>
    public required string Destination { get; init; }

    public required OtpPurpose Purpose { get; init; }

    /// <summary>Set when the caller is already authenticated, e.g. requesting a withdrawal code.</summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// Binds the code to a specific operation, so a withdrawal code approves that withdrawal
    /// alone. Verification requires the same binding.
    /// </summary>
    public string? OperationBinding { get; init; }

    /// <summary>Preferred language for the message body.</summary>
    public string Language { get; init; } = "ar";
}

public sealed record OtpVerification
{
    public required string Destination { get; init; }
    public required OtpPurpose Purpose { get; init; }
    public required string Code { get; init; }
    public Guid? UserId { get; init; }
    public string? OperationBinding { get; init; }
}

/// <summary>
/// What the client is told about an issued code. Deliberately never includes the code itself,
/// except in non-Production environments where the API echoes it to keep the flow testable.
/// </summary>
public sealed record OtpChallengeDto(
    string Destination,
    OtpChannel Channel,
    DateTime ExpiresAt,
    int CodeLength,
    int ResendAvailableInSeconds,
    string? DevelopmentCode);

public sealed record OtpVerificationResult(Guid OtpId, Guid? UserId, OtpPurpose Purpose);

/// <summary>
/// Delivers a one-time code. SMS or email is a deployment concern; failures are surfaced to the
/// caller because a code the user never receives is a dead end, not a background nicety.
/// </summary>
public interface IOtpSender
{
    Task SendAsync(
        string destination,
        OtpChannel channel,
        OtpPurpose purpose,
        string code,
        TimeSpan validFor,
        string language,
        CancellationToken ct = default);
}
