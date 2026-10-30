using AuctionPlatform.Application.Common;
using AuctionPlatform.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

/// <summary>
/// Inbound payment provider callbacks.
///
/// The endpoint is anonymous because the provider cannot hold a user token; authenticity comes from
/// the HMAC signature instead. A redelivered event returns 200 without moving money a second time,
/// so the provider stops retrying.
/// </summary>
[Route("api/v1/webhooks")]
[AllowAnonymous]
public sealed class WebhooksController : ApiControllerBase
{
    private readonly IPaymentWebhookService _webhooks;
    private readonly IWalletTopUpService _topUps;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        IPaymentWebhookService webhooks, IWalletTopUpService topUps, ILogger<WebhooksController> logger)
    {
        _webhooks = webhooks;
        _topUps = topUps;
        _logger = logger;
    }

    [HttpPost("payments/{provider}")]
    public async Task<IActionResult> Payments(string provider, CancellationToken ct)
    {
        // Off unless deliberately enabled outside Production with a real secret. It credits whatever
        // user and amount the payload names, so a guessable secret is a way to mint money.
        var options = HttpContext.RequestServices.GetRequiredService<IOptions<PaymentOptions>>().Value;
        var environment = HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>();
        if (environment.IsProduction() || !options.GenericWebhookEnabled || options.WebhookSecret.Length < 32)
        {
            _logger.LogWarning("Refused a call to the disabled generic payment webhook for {Provider}.", provider);
            return NotFound();
        }

        // The raw body is needed verbatim: the signature is computed over the exact bytes sent.
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        var signature = Request.Headers["X-Signature"].FirstOrDefault()
            ?? Request.Headers["X-Hub-Signature-256"].FirstOrDefault();

        var result = await _webhooks.HandleAsync(provider, body, signature, ct);

        if (!result.Accepted)
        {
            _logger.LogWarning("Rejected a {Provider} webhook: {Message}", provider, result.Message);
            return BadRequest(new { code = "WEBHOOK_REJECTED", message = result.Message, details = Array.Empty<string>() });
        }

        return Ok(new { received = true, alreadyProcessed = result.AlreadyProcessed });
    }

    /// <summary>
    /// Paymob's "transaction processed" callback. Paymob signs it with an HMAC sent as the
    /// <c>hmac</c> query parameter; anything unsigned or wrongly signed is refused.
    /// </summary>
    [HttpPost("paymob")]
    public async Task<IActionResult> Paymob([FromQuery] string? hmac, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        var result = await _topUps.HandleCallbackAsync(body, hmac, ct);
        if (!result.Accepted)
        {
            _logger.LogWarning("Rejected a Paymob callback: {Message}", result.Message);
            return BadRequest(new { code = "WEBHOOK_REJECTED", message = result.Message, details = Array.Empty<string>() });
        }

        return Ok(new { received = true, alreadyProcessed = result.AlreadyProcessed });
    }
}