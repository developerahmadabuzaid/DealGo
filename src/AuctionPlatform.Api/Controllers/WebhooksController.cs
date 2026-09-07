using AuctionPlatform.Infrastructure.Services;
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
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(IPaymentWebhookService webhooks, ILogger<WebhooksController> logger)
    {
        _webhooks = webhooks;
        _logger = logger;
    }

    [HttpPost("payments/{provider}")]
    public async Task<IActionResult> Payments(string provider, CancellationToken ct)
    {
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
}
