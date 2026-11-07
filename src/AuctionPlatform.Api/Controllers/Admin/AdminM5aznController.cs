using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.M5azn;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>M5azn dropshipping for staff: what was sent, what failed, and which products are M5azn's.</summary>
[Route("api/v1/admin/m5azn")]
[Authorize]
public sealed class AdminM5aznController : ApiControllerBase
{
    private readonly IM5aznFulfilmentService _m5azn;

    public AdminM5aznController(IM5aznFulfilmentService m5azn) => _m5azn = m5azn;

    [HttpGet("overview")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<M5aznOverviewDto>> Overview(CancellationToken ct) =>
        Ok(await _m5azn.GetOverviewAsync(ct));

    [HttpGet("shipments")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<PagedResult<M5aznShipmentDto>>> Shipments(
        [FromQuery] M5aznShipmentQuery query, CancellationToken ct) =>
        Ok(await _m5azn.GetShipmentsAsync(query, ct));

    [HttpGet("shipments/by-source/{type}/{sourceId:guid}")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<M5aznShipmentDto>> BySource(M5aznSourceType type, Guid sourceId, CancellationToken ct) =>
        await _m5azn.GetForSourceAsync(type, sourceId, ct) is { } s ? Ok(s) : NoContent();

    /// <summary>Sends a waiting shipment, or a failed one again. This places a paid order at M5azn.</summary>
    [HttpPost("shipments/{id:guid}/submit")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<M5aznShipmentDto>> Submit(Guid id, CancellationToken ct) =>
        Ok(await _m5azn.SubmitNowAsync(id, ct));

    [HttpPost("shipments/{id:guid}/refresh")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<M5aznShipmentDto>> Refresh(Guid id, CancellationToken ct) =>
        Ok(await _m5azn.RefreshAsync(id, ct));

    [HttpGet("links")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<M5aznLinksDto>> Links(CancellationToken ct) =>
        Ok(await _m5azn.GetLinksAsync(ct));

    [HttpPut("links/store-products/{id:guid}")]
    [Authorize(Policy = Permissions.StoreUpdateProduct)]
    public async Task<IActionResult> LinkStoreProduct(Guid id, M5aznLinkRequest request, CancellationToken ct)
    {
        await _m5azn.LinkStoreProductAsync(id, request, ct);
        return NoContent();
    }

    [HttpPut("links/prizes/{id:guid}")]
    [Authorize(Policy = Permissions.StoreUpdateProduct)]
    public async Task<IActionResult> LinkPrize(Guid id, M5aznLinkRequest request, CancellationToken ct)
    {
        await _m5azn.LinkPrizeAsync(id, request, ct);
        return NoContent();
    }

    /// <summary>Reads a product from M5azn's catalogue, to check an id before linking it. Read-only.</summary>
    [HttpGet("products/{m5aznProductId:long}")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<M5aznProductInfo>> Product(long m5aznProductId, CancellationToken ct) =>
        await _m5azn.LookupProductAsync(m5aznProductId, ct) is { } p ? Ok(p) : NotFound();
}
