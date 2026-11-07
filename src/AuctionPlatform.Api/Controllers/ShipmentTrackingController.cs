using AuctionPlatform.Application.M5azn;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

/// <summary>
/// Delivery tracking for the customer's own order or prize, when M5azn delivers it. 204 when it is
/// not (yet) with M5azn, which is also the answer while M5azn is switched off.
/// </summary>
[Route("api/v1")]
[Authorize]
public sealed class ShipmentTrackingController : ApiControllerBase
{
    private readonly IM5aznFulfilmentService _m5azn;

    public ShipmentTrackingController(IM5aznFulfilmentService m5azn) => _m5azn = m5azn;

    [HttpGet("store/orders/{id:guid}/shipment")]
    public async Task<ActionResult<ShipmentTrackingDto>> StoreOrder(Guid id, CancellationToken ct) =>
        await _m5azn.GetStoreOrderTrackingAsync(CurrentUserId, id, ct) is { } t ? Ok(t) : NoContent();

    [HttpGet("me/prize-claims/{id:guid}/shipment")]
    public async Task<ActionResult<ShipmentTrackingDto>> PrizeClaim(Guid id, CancellationToken ct) =>
        await _m5azn.GetPrizeClaimTrackingAsync(CurrentUserId, id, ct) is { } t ? Ok(t) : NoContent();
}
