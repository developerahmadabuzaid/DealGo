using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Identity;
using AuctionPlatform.Infrastructure.Persistence;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Tests.Integration;

/// <summary>
/// Test phone numbers that sign in with a fixed code.
///
/// They exist for store reviewers, who cannot receive a text on a Saudi number. What has to hold:
/// a listed number gets in with its code and nothing is sent or charged; everyone else is exactly
/// as before; and a known code opens sign-in only — never a withdrawal.
///
/// The provider here is switched <i>on</i> and records every call, so each test can say whether an
/// SMS would have gone out.
/// </summary>
[Collection("database")]
public class TestAccountTests
{
    private const string FixedCode = "246810";

    private readonly DatabaseFixture _fixture;

    public TestAccountTests(DatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_test_number_signs_in_with_its_code_and_nothing_is_sent()
    {
        var (otp, tawked, sms) = Build(_fixture, new() { ["0599990001"] = FixedCode });

        var challenge = await otp.RequestAsync(Login("0599990001"));

        tawked.Starts.Should().Be(0, "a test number never reaches the SMS provider");
        sms.Sent.Should().Be(0, "and nothing is sent any other way either");
        challenge.DevelopmentCode.Should().BeNull("a test code is a password and is never echoed");

        var result = await otp.VerifyAsync(Verify("0599990001", FixedCode));
        result.Purpose.Should().Be(OtpPurpose.Login);
    }

    [Fact]
    public async Task A_test_number_still_refuses_the_wrong_code()
    {
        var (otp, _, _) = Build(_fixture, new() { ["0599990002"] = FixedCode });

        await otp.RequestAsync(Login("0599990002"));

        var act = () => otp.VerifyAsync(Verify("0599990002", "000000"));

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be(ErrorCodes.InvalidOtp);
    }

    [Fact]
    public async Task Every_other_number_still_gets_a_real_code()
    {
        var (otp, tawked, _) = Build(_fixture, new() { ["0599990003"] = FixedCode });

        await otp.RequestAsync(Login("0599990004"));

        tawked.Starts.Should().Be(1, "a number that is not listed goes to the provider as before");
    }

    /// <summary>
    /// The one that matters most. A test account's code is written in a settings file and handed to
    /// store reviewers; if it also approved withdrawals, whoever held it could move money.
    /// </summary>
    [Fact]
    public async Task A_test_number_needs_a_real_code_to_withdraw()
    {
        var (otp, tawked, _) = Build(_fixture, new() { ["0599990005"] = FixedCode });

        await otp.RequestAsync(new OtpRequest
        {
            Destination = "0599990005",
            Purpose = OtpPurpose.Withdrawal,
            OperationBinding = "withdraw|100.00|SAR|"
        });

        tawked.Starts.Should().Be(1, "a withdrawal from a test number goes to the provider like any other");

        var act = () => otp.VerifyAsync(new OtpVerification
        {
            Destination = "0599990005",
            Purpose = OtpPurpose.Withdrawal,
            Code = FixedCode,
            OperationBinding = "withdraw|100.00|SAR|"
        });

        await act.Should().ThrowAsync<DomainException>("the fixed code must not approve a withdrawal");
    }

    // Each case signs in with its own number: asking twice for one number inside a minute is
    // refused by the resend cooldown, which is a different rule from the one under test.
    [Theory]
    [InlineData("+966 59 999 0061", "0599990061")]
    [InlineData("966599990062", "0599990062")]
    [InlineData("599990063", "0599990063")]
    public async Task The_number_may_be_written_any_way_in_the_settings(string written, string phone)
    {
        var (otp, tawked, _) = Build(_fixture, new() { [written] = FixedCode });

        await otp.RequestAsync(Login(phone));

        tawked.Starts.Should().Be(0);
        (await otp.VerifyAsync(Verify(phone, FixedCode))).Purpose.Should().Be(OtpPurpose.Login);
    }

    /// <summary>A code that is not 4 to 8 digits is a typo, not a password; it is ignored rather than trusted.</summary>
    [Theory]
    [InlineData("12", "0599990071")]
    [InlineData("abcdef", "0599990072")]
    [InlineData("", "0599990073")]
    public async Task A_badly_written_code_is_ignored(string bad, string phone)
    {
        var (otp, tawked, _) = Build(_fixture, new() { [phone] = bad });

        await otp.RequestAsync(Login(phone));

        tawked.Starts.Should().Be(1, "with no usable fixed code, the number is treated like any other");
    }

    // ------------------------------------------------------------------ plumbing

    private static OtpRequest Login(string phone) =>
        new() { Destination = phone, Purpose = OtpPurpose.Login };

    private static OtpVerification Verify(string phone, string code) =>
        new() { Destination = phone, Purpose = OtpPurpose.Login, Code = code };

    /// <summary>
    /// An OTP service with these test numbers, a provider that is switched on and counts its calls,
    /// and an SMS sender that counts its sends.
    /// </summary>
    private static (OtpService Otp, RecordingTawked Tawked, RecordingSender Sms) Build(
        DatabaseFixture fixture, Dictionary<string, string> testAccounts)
    {
        // Each test asks for its own scope and keeps it for the length of the test.
        var scope = fixture.CreateScope();
        var sp = scope.ServiceProvider;

        var tawked = new RecordingTawked();
        var sms = new RecordingSender();

        var otp = new OtpService(
            sp.GetRequiredService<ApplicationDbContext>(),
            sms,
            tawked,
            sp.GetRequiredService<IDateTimeProvider>(),
            sp.GetRequiredService<ICurrentUserService>(),
            Options.Create(new OtpOptions { TestAccounts = testAccounts }),
            sp.GetRequiredService<IHostEnvironment>(),
            NullLogger<OtpService>.Instance);

        return (otp, tawked, sms);
    }

    private sealed class RecordingTawked : ITawkedVerifyClient
    {
        public int Starts { get; private set; }

        public bool IsEnabled => true;

        public Task<TawkedVerification> StartAsync(string phoneE164, string language, string reference,
            string? clientIp, string idempotencyKey, CancellationToken ct = default)
        {
            Starts++;
            return Task.FromResult(new TawkedVerification($"v-{Guid.NewGuid():N}", DateTime.UtcNow.AddMinutes(5)));
        }

        // Whatever is typed, the provider says no: these tests never hold a real provider code.
        public Task<TawkedCheckOutcome> CheckAsync(string verificationId, string code, CancellationToken ct = default) =>
            Task.FromResult(TawkedCheckOutcome.InvalidCode);
    }

    private sealed class RecordingSender : IOtpSender
    {
        public int Sent { get; private set; }

        public Task SendAsync(string destination, OtpChannel channel, OtpPurpose purpose, string code,
            TimeSpan validFor, string language, CancellationToken ct = default)
        {
            Sent++;
            return Task.CompletedTask;
        }
    }
}
