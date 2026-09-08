using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// Delivers one-time codes.
///
/// This implementation writes the message to the log instead of sending it, which keeps local
/// development and tests free of an SMS or email provider. It is the single integration point:
/// swap it for a Twilio / SNS / SendGrid client and nothing else changes.
///
/// The code is logged only outside Production — see <see cref="LogCode"/>.
/// </summary>
public sealed class LoggingOtpSender : IOtpSender
{
    private readonly ILocalizationService _localization;
    private readonly ILogger<LoggingOtpSender> _logger;
    private readonly bool _logCode;

    public LoggingOtpSender(
        ILocalizationService localization,
        ILogger<LoggingOtpSender> logger,
        bool logCode)
    {
        _localization = localization;
        _logger = logger;
        _logCode = logCode;
    }

    public Task SendAsync(
        string destination,
        OtpChannel channel,
        OtpPurpose purpose,
        string code,
        TimeSpan validFor,
        string language,
        CancellationToken ct = default)
    {
        var body = _localization.Translate(
            OtpMessageKeys.For(purpose), language, code, (int)validFor.TotalMinutes);

        if (_logCode)
        {
            // Development convenience: the code is visible so the flow can be exercised without a
            // real provider. Never enabled in Production.
            _logger.LogWarning(
                "[OTP · no provider configured] {Channel} to {Destination}: {Body}",
                channel, destination, body);
        }
        else
        {
            _logger.LogError(
                "No OTP provider is configured, so a {Purpose} code could not be delivered to a " +
                "{Channel} destination. Configure a sender before running in Production.",
                purpose, channel);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Localization keys for the one-time code messages.</summary>
public static class OtpMessageKeys
{
    public const string Login = "otp.login";
    public const string Withdrawal = "otp.withdrawal";
    public const string VerifyContact = "otp.verify_contact";
    public const string PrizePayment = "otp.prize_payment";

    public static string For(OtpPurpose purpose) => purpose switch
    {
        OtpPurpose.Login => Login,
        OtpPurpose.Withdrawal => Withdrawal,
        OtpPurpose.VerifyContact => VerifyContact,
        OtpPurpose.PrizePayment => PrizePayment,
        _ => Login
    };
}
