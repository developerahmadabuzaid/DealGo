using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.M5azn;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>M5azn dropshipping for staff: the imported products, what was sent, what failed, and the settings.</summary>
[Route("api/v1/admin/m5azn")]
[Authorize]
public sealed class AdminM5aznController : ApiControllerBase
{
    private readonly IM5aznFulfilmentService _m5azn;
    private readonly IM5aznCatalogService _catalog;

    public AdminM5aznController(IM5aznFulfilmentService m5azn, IM5aznCatalogService catalog)
    {
        _m5azn = m5azn;
        _catalog = catalog;
    }

    [HttpGet("overview")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<M5aznOverviewDto>> Overview(CancellationToken ct) =>
        Ok(await _m5azn.GetOverviewAsync(ct));

    [HttpGet("shipments")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<PagedResult<M5aznShipmentDto>>> Shipments(
        [FromQuery] M5aznShipmentQuery query, CancellationToken ct) =>
        Ok(await _m5azn.GetShipmentsAsync(query, ct));

    [HttpGet("shipments/by-order/{orderId:guid}")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<M5aznShipmentDto>> ByOrder(Guid orderId, CancellationToken ct) =>
        await _m5azn.GetForStoreOrderAsync(orderId, ct) is { } s ? Ok(s) : NoContent();

    /// <summary>Sends a waiting shipment, or a failed one again. This places a paid order at M5azn.</summary>
    [HttpPost("shipments/{id:guid}/submit")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<M5aznShipmentDto>> Submit(Guid id, CancellationToken ct) =>
        Ok(await _m5azn.SubmitNowAsync(id, ct));

    [HttpPost("shipments/{id:guid}/refresh")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<M5aznShipmentDto>> Refresh(Guid id, CancellationToken ct) =>
        Ok(await _m5azn.RefreshAsync(id, ct));

    /// <summary>The products imported from M5azn. Products added in the dashboard are never listed here.</summary>
    [HttpGet("products")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<PagedResult<M5aznImportedProductDto>>> Products(
        [FromQuery] M5aznProductQuery query, CancellationToken ct) =>
        Ok(await _catalog.GetImportedProductsAsync(query, ct));

    /// <summary>
    /// Gives one imported product its own profit percentage, or (null) returns it to the general
    /// one. The product is re-priced at once.
    /// </summary>
    [HttpPut("products/{id:guid}/profit")]
    [Authorize(Policy = Permissions.StoreUpdateProduct)]
    public async Task<ActionResult<M5aznImportedProductDto>> SetProfit(Guid id, M5aznProductProfitRequest request, CancellationToken ct) =>
        Ok(await _catalog.SetProductProfitAsync(id, request.ProfitPercentage, ct));

    /// <summary>
    /// Starts reading the seller's M5azn product list (read-only; nothing is ordered) to create,
    /// update or hide the imported products. Answers at once; the import goes on in the background.
    /// It also runs on its own every hour while M5azn is on.
    /// </summary>
    [HttpPost("import")]
    [Authorize(Policy = Permissions.StoreUpdateProduct)]
    public async Task<ActionResult<M5aznImportStatus>> Import(CancellationToken ct) =>
        Ok(await _catalog.RequestImportAsync(ct));

    /// <summary>How the import is going, whether the button or the hourly run started it.</summary>
    [HttpGet("import/status")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<M5aznImportStatus>> ImportStatus(CancellationToken ct) =>
        Ok(await _catalog.GetImportStatusAsync(ct));

    /// <summary>The on/off switch, the seller account (never its password), the address and the profit percentage.</summary>
    [HttpGet("settings")]
    [Authorize(Policy = Permissions.StoreManageSettings)]
    public async Task<ActionResult<M5aznSettingsDto>> Settings(CancellationToken ct) =>
        Ok(await _m5azn.GetSettingsAsync(ct));

    [HttpPut("settings")]
    [Authorize(Policy = Permissions.StoreManageSettings)]
    public async Task<ActionResult<M5aznSettingsSaved>> UpdateSettings(M5aznSettingsRequest request, CancellationToken ct) =>
        Ok(await _m5azn.UpdateSettingsAsync(request, ct));

    /// <summary>Signs in to M5azn with the saved account, to check it. Places nothing.</summary>
    [HttpPost("settings/check")]
    [Authorize(Policy = Permissions.StoreManageSettings)]
    public async Task<ActionResult<M5aznSignInCheck>> CheckSignIn(CancellationToken ct) =>
        Ok(await _m5azn.CheckSignInAsync(ct));

}
