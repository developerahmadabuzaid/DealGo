using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Notifications;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// Messages from staff, sent now or at a chosen time, and the wording of the messages the system
/// sends by itself.
/// </summary>
[Route("api/v1/admin/notifications")]
[Authorize(Policy = Permissions.NotificationsSend)]
public sealed class AdminNotificationsController : ApiControllerBase
{
    private readonly IAdminNotificationService _notifications;
    private readonly IAutomaticMessageService _automatic;

    public AdminNotificationsController(IAdminNotificationService notifications, IAutomaticMessageService automatic)
    {
        _notifications = notifications;
        _automatic = automatic;
    }

    [HttpGet("audience")]
    public async Task<ActionResult<NotificationAudienceDto>> Audience(CancellationToken ct) =>
        Ok(await _notifications.GetAudienceAsync(ct));

    [HttpPost("broadcast")]
    public async Task<ActionResult<AdminMessageSentDto>> Broadcast(
        SendAdminMessageRequest request, CancellationToken ct) =>
        Ok(await _notifications.SendAsync(request, ct));

    // ---------------------------------------------------------------- scheduled

    [HttpGet("scheduled")]
    public async Task<ActionResult<PagedResult<ScheduledNotificationDto>>> Scheduled(
        [FromQuery] ScheduledNotificationQuery query, CancellationToken ct) =>
        Ok(await _notifications.GetScheduledAsync(query, ct));

    [HttpPost("scheduled")]
    public async Task<ActionResult<ScheduledNotificationDto>> Schedule(
        ScheduleAdminMessageRequest request, CancellationToken ct) =>
        Ok(await _notifications.ScheduleAsync(request, ct));

    [HttpPut("scheduled/{id:guid}")]
    public async Task<ActionResult<ScheduledNotificationDto>> UpdateScheduled(
        Guid id, ScheduleAdminMessageRequest request, CancellationToken ct) =>
        Ok(await _notifications.UpdateScheduledAsync(id, request, ct));

    [HttpPost("scheduled/{id:guid}/cancel")]
    public async Task<ActionResult<ScheduledNotificationDto>> CancelScheduled(Guid id, CancellationToken ct) =>
        Ok(await _notifications.CancelScheduledAsync(id, ct));

    // ---------------------------------------------------------------- automatic

    [HttpGet("automatic")]
    public async Task<ActionResult<IReadOnlyList<AutomaticMessageDto>>> Automatic(CancellationToken ct) =>
        Ok(await _automatic.ListAsync(ct));

    [HttpPut("automatic/{code}")]
    public async Task<ActionResult<AutomaticMessageDto>> UpdateAutomatic(
        string code, UpdateAutomaticMessageRequest request, CancellationToken ct) =>
        Ok(await _automatic.UpdateAsync(code, request, ct));

    [HttpPost("automatic/{code}/reset")]
    public async Task<ActionResult<AutomaticMessageDto>> ResetAutomatic(string code, CancellationToken ct) =>
        Ok(await _automatic.ResetAsync(code, ct));
}
