using System.Net;
using System.Text;
using System.Text.Json;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Tests.Unit;

/// <summary>
/// The Tawked Verify client.
///
/// Two things are worth pinning down here. First, that the request carries what Tawked's own
/// protections need — the idempotency key that stops a retry sending a second paid message, and
/// the caller's IP, which is the only signal that tells many users apart from one abuser. Second,
/// and more important, that a refusal is translated by *whose problem it is*: a bad number is the
/// caller's and says so, while an empty balance or a dead key is ours and must never reach them.
/// </summary>
public class TawkedVerifyTests
{
    private const string Phone = "+966512345678";

    [Fact]
    public void Without_a_key_the_client_is_switched_off()
    {
        var client = Build(new StubHandler(_ => throw new InvalidOperationException("must not be called")),
            apiKey: string.Empty);

        client.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Starting_a_verification_returns_the_id_to_check_against()
    {
        var expiry = new DateTime(2026, 9, 30, 12, 34, 56, DateTimeKind.Utc);

        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, new
        {
            id = "b7e5c2b0-9c1a-4e2f-8f2a-3a6b0e9d1c44",
            status = "pending",
            expires_at = "2026-09-30T12:34:56.000Z"
        }));

        var verification = await Build(handler).StartAsync(
            Phone, "ar", "Login:abc", "37.16.1.9", "idem-1");

        verification.Id.Should().Be("b7e5c2b0-9c1a-4e2f-8f2a-3a6b0e9d1c44");
        verification.ExpiresAt.ToUniversalTime().Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task The_request_carries_the_number_the_channel_and_the_protections_Tawked_needs()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, new
        {
            id = "v1",
            expires_at = "2026-09-30T12:00:00.000Z"
        }));

        await Build(handler).StartAsync(Phone, "en", "Withdrawal:xyz", "37.16.1.9", "idem-42");

        var body = JsonDocument.Parse(handler.LastBody!).RootElement;

        body.GetProperty("to").GetString().Should().Be(Phone);
        body.GetProperty("channel").GetString().Should().Be("sms");
        body.GetProperty("lang").GetString().Should().Be("en");
        body.GetProperty("reference").GetString().Should().Be("Withdrawal:xyz");

        // Tawked never infers this, so a per-IP limit only exists if we send it.
        body.GetProperty("client_ip").GetString().Should().Be("37.16.1.9");

        // Without this a retry after a timeout sends — and charges for — a second message.
        handler.LastRequest!.Headers.GetValues("Idempotency-Key").Should().Equal("idem-42");
    }

    [Theory]
    [InlineData("ar", "ar")]
    [InlineData("", "ar")]
    [InlineData("en-GB", "en")]
    public async Task Only_English_selects_English(string requested, string expected)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, new
        {
            id = "v1",
            expires_at = "2026-09-30T12:00:00.000Z"
        }));

        await Build(handler).StartAsync(Phone, requested, "Login:a", null, "idem");

        JsonDocument.Parse(handler.LastBody!).RootElement
            .GetProperty("lang").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task A_number_Tawked_will_not_accept_is_reported_as_the_callers_mistake()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.UnprocessableEntity, new { error = "invalid_destination" }));

        var act = () => Build(handler).StartAsync(Phone, "ar", "Login:a", null, "idem");

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(ErrorCodes.OtpDestinationInvalid);
    }

    [Fact]
    public async Task One_phone_asking_for_too_many_codes_is_reported_as_rate_limited()
    {
        var handler = new StubHandler(_ => Json(
            (HttpStatusCode)429, new { error = "rate_limited" }));

        var act = () => Build(handler).StartAsync(Phone, "ar", "Login:a", null, "idem");

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(ErrorCodes.RateLimited);
    }

    /// <summary>
    /// The one that matters most. Our balance, our key and our approval status are our problem;
    /// a person trying to sign in must be told only that the service is briefly away.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "insufficient_credits")]
    [InlineData(HttpStatusCode.Forbidden, "account_not_active")]
    [InlineData(HttpStatusCode.Forbidden, "application_paused")]
    [InlineData((HttpStatusCode)429, "spend_cap_reached")]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.Unauthorized, "key_expired")]
    [InlineData(HttpStatusCode.InternalServerError, "internal_error")]
    // The sandbox's limits. The first is what the live site answered a real customer number with,
    // and it used to be shown to them as "your number is not valid".
    [InlineData(HttpStatusCode.UnprocessableEntity, "sandbox_unverified_destination")]
    [InlineData((HttpStatusCode)429, "sandbox_quota_exceeded")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "sandbox_unsupported_channel")]
    // A refusal we have never seen must not be blamed on the caller's number either.
    [InlineData(HttpStatusCode.UnprocessableEntity, "something_new")]
    public async Task Our_own_account_trouble_never_reaches_the_caller(HttpStatusCode status, string error)
    {
        var handler = new StubHandler(_ => Json(status, new { error }));

        var act = () => Build(handler).StartAsync(Phone, "ar", "Login:a", null, "idem");

        var thrown = (await act.Should().ThrowAsync<DomainException>()).Which;

        thrown.Code.Should().Be(ErrorCodes.ServiceUnavailable);
        thrown.Message.Should().NotContainAny("credit", "balance", "key", "account", "paused");
    }

    [Fact]
    public async Task A_network_failure_is_reported_as_temporarily_unavailable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("the socket went away"));

        var act = () => Build(handler).StartAsync(Phone, "ar", "Login:a", null, "idem");

        (await act.Should().ThrowAsync<DomainException>())
            .Which.Code.Should().Be(ErrorCodes.ServiceUnavailable);
    }

    // ------------------------------------------------------------------ checking

    [Fact]
    public async Task A_correct_code_verifies()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.OK, new { verified = true, status = "verified" }));

        var outcome = await Build(handler).CheckAsync("v1", "482913");

        outcome.Should().Be(TawkedCheckOutcome.Verified);
        JsonDocument.Parse(handler.LastBody!).RootElement
            .GetProperty("code").GetString().Should().Be("482913");
    }

    [Theory]
    [InlineData("invalid_code", TawkedCheckOutcome.InvalidCode)]
    [InlineData("too_many_attempts", TawkedCheckOutcome.TooManyAttempts)]
    [InlineData("expired", TawkedCheckOutcome.Expired)]
    [InlineData("canceled", TawkedCheckOutcome.Canceled)]
    [InlineData("failed", TawkedCheckOutcome.Failed)]
    public async Task Every_refusal_status_is_carried_through(string status, TawkedCheckOutcome expected)
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.OK, new { verified = false, status, attempts_remaining = 2 }));

        (await Build(handler).CheckAsync("v1", "000000")).Should().Be(expected);
    }

    [Fact]
    public async Task An_unknown_verification_is_not_a_pass()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, new { error = "not_found" }));

        (await Build(handler).CheckAsync("gone", "482913")).Should().Be(TawkedCheckOutcome.NotFound);
    }

    /// <summary>
    /// A status name Tawked adds later must never read as success. `verified` is the decision;
    /// the status only explains it.
    /// </summary>
    [Fact]
    public async Task A_status_we_have_never_seen_is_a_refusal()
    {
        var handler = new StubHandler(_ => Json(
            HttpStatusCode.OK, new { verified = false, status = "something_new" }));

        (await Build(handler).CheckAsync("v1", "482913")).Should().Be(TawkedCheckOutcome.Failed);
    }

    // ------------------------------------------------------------------ plumbing

    private static TawkedVerifyClient Build(StubHandler handler, string apiKey = "tk_test_abc123")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://tawked.test/") };

        return new TawkedVerifyClient(
            http,
            Options.Create(new TawkedOptions { ApiKey = apiKey, BaseUrl = "https://tawked.test" }),
            NullLogger<TawkedVerifyClient>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    /// <summary>Answers with whatever the test decided, and keeps what was asked.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return _respond(request);
        }
    }
}
