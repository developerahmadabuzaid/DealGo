using AuctionPlatform.Application.Admin;
using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// Administrative auction management. Authorisation is enforced here on the server; hiding a button
/// in the dashboard is never treated as access control.
/// </summary>
[Route("api/v1/admin/auctions")]
[Authorize]
public sealed class AdminAuctionsController : ApiControllerBase
{
    private readonly IAuctionAdminService _admin;
    private readonly IAuctionService _reader;
    private readonly IDashboardService _dashboard;

    public AdminAuctionsController(
        IAuctionAdminService admin, IAuctionService reader, IDashboardService dashboard)
    {
        _admin = admin;
        _reader = reader;
        _dashboard = dashboard;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.AuctionRead)]
    public async Task<ActionResult<PagedResult<AuctionListItemDto>>> List(
        [FromQuery] AuctionListQuery query, CancellationToken ct) =>
        Ok(await _reader.ListAsync(query, null, ct));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.AuctionRead)]
    public async Task<ActionResult<AuctionDetailDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await _reader.GetAsync(id, null, ct));

    [HttpPost]
    [Authorize(Policy = Permissions.AuctionCreate)]
    public async Task<ActionResult<AuctionDetailDto>> Create(CreateAuctionRequest request, CancellationToken ct)
    {
        var auction = await _admin.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = auction.Id }, auction);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.AuctionUpdate)]
    public async Task<ActionResult<AuctionDetailDto>> Update(
        Guid id, UpdateAuctionRequest request, CancellationToken ct) =>
        Ok(await _admin.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.AuctionCancel)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _admin.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Validates the full publish checklist, including exactly four ranked prizes.</summary>
    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = Permissions.AuctionUpdate)]
    public async Task<ActionResult<AuctionDetailDto>> Publish(Guid id, CancellationToken ct) =>
        Ok(await _admin.PublishAsync(id, ct));

    [HttpPost("{id:guid}/start")]
    [Authorize(Policy = Permissions.AuctionStart)]
    public async Task<ActionResult<AuctionDetailDto>> StartNow(Guid id, CancellationToken ct) =>
        Ok(await _admin.StartNowAsync(id, ct));

    [HttpPatch("{id:guid}/schedule")]
    [Authorize(Policy = Permissions.AuctionStart)]
    public async Task<ActionResult<AuctionDetailDto>> Schedule(
        Guid id, UpdateScheduleRequest request, CancellationToken ct) =>
        Ok(await _admin.UpdateScheduleAsync(id, request, ct));

    [HttpPost("{id:guid}/pause")]
    [Authorize(Policy = Permissions.AuctionStart)]
    public async Task<ActionResult<AuctionDetailDto>> Pause(Guid id, CancellationToken ct) =>
        Ok(await _admin.PauseAsync(id, ct));

    [HttpPost("{id:guid}/resume")]
    [Authorize(Policy = Permissions.AuctionStart)]
    public async Task<ActionResult<AuctionDetailDto>> Resume(Guid id, CancellationToken ct) =>
        Ok(await _admin.ResumeAsync(id, ct));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Permissions.AuctionCancel)]
    public async Task<ActionResult<AuctionDetailDto>> Cancel(
        Guid id, [FromBody] CancelAuctionRequest? request, CancellationToken ct) =>
        Ok(await _admin.CancelAsync(id, request?.Reason, ct));

    [HttpGet("{id:guid}/participants")]
    [Authorize(Policy = Permissions.AuctionRead)]
    public async Task<ActionResult<PagedResult<ParticipantAdminDto>>> Participants(
        Guid id, [FromQuery] PagedQuery query, CancellationToken ct) =>
        Ok(await _reader.GetParticipantsAsync(id, query, ct));

    [HttpGet("{id:guid}/winners")]
    [Authorize(Policy = Permissions.AuctionRead)]
    public async Task<ActionResult<IReadOnlyList<WinnerDto>>> Winners(Guid id, CancellationToken ct) =>
        Ok(await _reader.GetWinnersAsync(id, ct));

    /// <summary>The append-only event trail for one auction.</summary>
    [HttpGet("{id:guid}/events")]
    [Authorize(Policy = Permissions.AuctionRead)]
    public async Task<ActionResult<PagedResult<AuctionEventDto>>> Events(
        Guid id, [FromQuery] PagedQuery query, CancellationToken ct) =>
        Ok(await _dashboard.GetAuctionEventsAsync(id, query, ct));
}

public sealed record CancelAuctionRequest(string? Reason);

[Route("api/v1/admin")]
[Authorize]
public sealed class AdminDashboardController : ApiControllerBase
{
    private readonly IDashboardService _dashboard;

    public AdminDashboardController(IDashboardService dashboard) => _dashboard = dashboard;

    [HttpGet("dashboard")]
    [Authorize(Policy = Permissions.ReportsRead)]
    public async Task<ActionResult<DashboardDto>> Dashboard(CancellationToken ct) =>
        Ok(await _dashboard.GetDashboardAsync(ct));

    [HttpGet("audit-logs")]
    [Authorize(Policy = Permissions.AuditRead)]
    public async Task<ActionResult<PagedResult<AuditLogDto>>> AuditLogs(
        [FromQuery] AuditLogQuery query, CancellationToken ct) =>
        Ok(await _dashboard.GetAuditLogsAsync(query, ct));
}
