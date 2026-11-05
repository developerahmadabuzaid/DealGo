using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>A message from staff to every user, as a push and in the app's notification list.</summary>
[Route("api/v1/admin/notifications")]
[Authorize(Policy = Permissions.NotificationsSend)]
public sealed class AdminNotificationsController : ApiControllerBase
{
    private readonly IAdminNotificationService _notifications;

    public AdminNotificationsController(IAdminNotificationService notifications) =>
        _notifications = notifications;

    [HttpGet("audience")]
    public async Task<ActionResult<NotificationAudienceDto>> Audience(CancellationToken ct) =>
        Ok(await _notifications.GetAudienceAsync(ct));

    [HttpPost("broadcast")]
    public async Task<ActionResult<AdminMessageSentDto>> Broadcast(
        SendAdminMessageRequest request, CancellationToken ct) =>
        Ok(await _notifications.SendAsync(request, ct));
}
