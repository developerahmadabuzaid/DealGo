using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Application.Prizes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

[Route("api/v1")]
[Authorize]
public sealed class PrizeClaimsController : ApiControllerBase
{
    private readonly IPrizeClaimService _claims;

    public PrizeClaimsController(IPrizeClaimService claims) => _claims = claims;

    [HttpGet("me/wins")]
    public async Task<ActionResult<PagedResult<MyWinDto>>> MyWins(
        [FromQuery] PagedQuery query, CancellationToken ct) =>
        Ok(await _claims.GetMyWinsAsync(CurrentUserId, query, ct));

    [HttpGet("me/prize-claims")]
    public async Task<ActionResult<PagedResult<PrizeClaimDto>>> MyClaims(
        [FromQuery] PagedQuery query, CancellationToken ct) =>
        Ok(await _claims.GetMyClaimsAsync(CurrentUserId, query, ct));

    [HttpGet("me/prize-claims/{id:guid}")]
    public async Task<ActionResult<PrizeClaimDto>> Claim(Guid id, CancellationToken ct) =>
        Ok(await _claims.GetClaimAsync(CurrentUserId, id, ct));

    /// <summary>Pays the prize value from the Main wallet. Shopping credit is not accepted here.</summary>
    [HttpPost("prize-claims/{id:guid}/pay")]
    public async Task<ActionResult<PrizeClaimDto>> Pay(
        Guid id, PayPrizeRequest request, CancellationToken ct) =>
        Ok(await _claims.PayAsync(CurrentUserId, id, request, ct));

    [HttpPost("prize-claims/{id:guid}/claim")]
    public async Task<ActionResult<PrizeClaimDto>> Claim(
        Guid id, [FromBody] ClaimPrizeRequest? request, CancellationToken ct) =>
        Ok(await _claims.ClaimAsync(CurrentUserId, id, request?.ShippingAddressId, ct));
}

public sealed record ClaimPrizeRequest(Guid? ShippingAddressId);

[Route("api/v1/notifications")]
[Authorize]
public sealed class NotificationsController : ApiControllerBase
{
    private readonly INotificationService _notifications;

    public NotificationsController(INotificationService notifications) => _notifications = notifications;

    [HttpGet]
    public async Task<ActionResult<PagedResult<NotificationDto>>> List(
        [FromQuery] PagedQuery query, CancellationToken ct) =>
        Ok(await _notifications.GetForUserAsync(CurrentUserId, query, ct));

    [HttpGet("unread-count")]
    public async Task<ActionResult<int>> UnreadCount(CancellationToken ct) =>
        Ok(await _notifications.GetUnreadCountAsync(CurrentUserId, ct));

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        await _notifications.MarkReadAsync(CurrentUserId, id, ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        await _notifications.MarkAllReadAsync(CurrentUserId, ct);
        return NoContent();
    }

    [HttpPost("devices")]
    public async Task<IActionResult> RegisterDevice(RegisterDeviceRequest request, CancellationToken ct)
    {
        await _notifications.RegisterDeviceAsync(CurrentUserId, request.Token, request.Platform, ct);
        return NoContent();
    }

    [HttpDelete("devices")]
    public async Task<IActionResult> UnregisterDevice([FromQuery] string token, CancellationToken ct)
    {
        await _notifications.UnregisterDeviceAsync(CurrentUserId, token, ct);
        return NoContent();
    }
}

public sealed record RegisterDeviceRequest(string Token, string? Platform);
