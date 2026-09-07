using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Store;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

[Route("api/v1/admin/store")]
[Authorize]
public sealed class AdminStoreController : ApiControllerBase
{
    private readonly IStoreAdminService _store;

    public AdminStoreController(IStoreAdminService store) => _store = store;

    // ---------------------------------------------------------------- overview

    [HttpGet("overview")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<StoreOverviewDto>> Overview(CancellationToken ct) =>
        Ok(await _store.GetOverviewAsync(ct));

    [HttpGet("statistics")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<StoreOverviewDto>> Statistics(CancellationToken ct) =>
        Ok(await _store.GetOverviewAsync(ct));

    // ---------------------------------------------------------------- products

    [HttpGet("products")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<PagedResult<StoreProductListItemDto>>> Products(
        [FromQuery] StoreProductQuery query, CancellationToken ct) =>
        Ok(await _store.GetProductsAsync(query, ct));

    [HttpGet("products/{id:guid}")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<StoreProductDetailDto>> Product(Guid id, CancellationToken ct) =>
        Ok(await _store.GetProductAsync(id, ct));

    [HttpPost("products")]
    [Authorize(Policy = Permissions.StoreCreateProduct)]
    public async Task<ActionResult<StoreProductDetailDto>> CreateProduct(
        CreateStoreProductRequest request, CancellationToken ct)
    {
        var product = await _store.CreateProductAsync(request, ct);
        return CreatedAtAction(nameof(Product), new { id = product.Id }, product);
    }

    [HttpPut("products/{id:guid}")]
    [Authorize(Policy = Permissions.StoreUpdateProduct)]
    public async Task<ActionResult<StoreProductDetailDto>> UpdateProduct(
        Guid id, UpdateStoreProductRequest request, CancellationToken ct) =>
        Ok(await _store.UpdateProductAsync(id, request, ct));

    [HttpDelete("products/{id:guid}")]
    [Authorize(Policy = Permissions.StoreDeleteProduct)]
    public async Task<IActionResult> DeleteProduct(Guid id, CancellationToken ct)
    {
        await _store.DeleteProductAsync(id, ct);
        return NoContent();
    }

    [HttpPost("products/{id:guid}/activate")]
    [Authorize(Policy = Permissions.StoreUpdateProduct)]
    public async Task<ActionResult<StoreProductDetailDto>> Activate(Guid id, CancellationToken ct) =>
        Ok(await _store.SetProductStatusAsync(id, StoreProductStatus.Active, ct));

    [HttpPost("products/{id:guid}/hide")]
    [Authorize(Policy = Permissions.StoreUpdateProduct)]
    public async Task<ActionResult<StoreProductDetailDto>> Hide(Guid id, CancellationToken ct) =>
        Ok(await _store.SetProductStatusAsync(id, StoreProductStatus.Hidden, ct));

    [HttpPost("products/{id:guid}/archive")]
    [Authorize(Policy = Permissions.StoreUpdateProduct)]
    public async Task<ActionResult<StoreProductDetailDto>> Archive(Guid id, CancellationToken ct) =>
        Ok(await _store.SetProductStatusAsync(id, StoreProductStatus.Archived, ct));

    // ---------------------------------------------------------------- categories

    [HttpGet("categories")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<IReadOnlyList<StoreCategoryDto>>> Categories(CancellationToken ct) =>
        Ok(await _store.GetCategoriesAsync(ct));

    [HttpPost("categories")]
    [Authorize(Policy = Permissions.StoreCreateProduct)]
    public async Task<ActionResult<StoreCategoryDto>> CreateCategory(
        CreateStoreCategoryRequest request, CancellationToken ct) =>
        Ok(await _store.CreateCategoryAsync(request, ct));

    [HttpPut("categories/{id:guid}")]
    [Authorize(Policy = Permissions.StoreUpdateProduct)]
    public async Task<ActionResult<StoreCategoryDto>> UpdateCategory(
        Guid id, UpdateStoreCategoryRequest request, CancellationToken ct) =>
        Ok(await _store.UpdateCategoryAsync(id, request, ct));

    [HttpDelete("categories/{id:guid}")]
    [Authorize(Policy = Permissions.StoreDeleteProduct)]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct)
    {
        await _store.DeleteCategoryAsync(id, ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- inventory

    [HttpGet("inventory")]
    [Authorize(Policy = Permissions.StoreManageInventory)]
    public async Task<ActionResult<PagedResult<InventoryRowDto>>> Inventory(
        [FromQuery] StoreProductQuery query, CancellationToken ct) =>
        Ok(await _store.GetInventoryAsync(query, ct));

    /// <summary>Adjusts stock. A reason is mandatory and the change is always audited.</summary>
    [HttpPost("inventory/{productId:guid}/adjust")]
    [Authorize(Policy = Permissions.StoreManageInventory)]
    public async Task<ActionResult<InventoryTransactionDto>> AdjustInventory(
        Guid productId, AdjustInventoryRequest request, CancellationToken ct) =>
        Ok(await _store.AdjustInventoryAsync(productId, request, ct));

    [HttpGet("inventory/history")]
    [Authorize(Policy = Permissions.StoreManageInventory)]
    public async Task<ActionResult<PagedResult<InventoryTransactionDto>>> InventoryHistory(
        [FromQuery] Guid? productId, [FromQuery] PagedQuery query, CancellationToken ct) =>
        Ok(await _store.GetInventoryHistoryAsync(productId, query, ct));

    // ---------------------------------------------------------------- orders

    [HttpGet("orders")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<PagedResult<StoreOrderDto>>> Orders(
        [FromQuery] StoreOrderQuery query, CancellationToken ct) =>
        Ok(await _store.GetOrdersAsync(query, ct));

    [HttpGet("orders/{id:guid}")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<StoreOrderDto>> Order(Guid id, CancellationToken ct) =>
        Ok(await _store.GetOrderAsync(id, ct));

    [HttpPost("orders/{id:guid}/process")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<StoreOrderDto>> Process(
        Guid id, [FromBody] OrderActionRequest? request, CancellationToken ct) =>
        Ok(await _store.TransitionOrderAsync(id, StoreOrderStatus.Processing, request?.Note, ct));

    [HttpPost("orders/{id:guid}/ship")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<StoreOrderDto>> Ship(
        Guid id, [FromBody] OrderActionRequest? request, CancellationToken ct) =>
        Ok(await _store.TransitionOrderAsync(id, StoreOrderStatus.Shipped, request?.Note, ct));

    [HttpPost("orders/{id:guid}/deliver")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<StoreOrderDto>> Deliver(
        Guid id, [FromBody] OrderActionRequest? request, CancellationToken ct) =>
        Ok(await _store.TransitionOrderAsync(id, StoreOrderStatus.Delivered, request?.Note, ct));

    [HttpPost("orders/{id:guid}/complete")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<StoreOrderDto>> Complete(
        Guid id, [FromBody] OrderActionRequest? request, CancellationToken ct) =>
        Ok(await _store.TransitionOrderAsync(id, StoreOrderStatus.Completed, request?.Note, ct));

    [HttpPost("orders/{id:guid}/cancel")]
    [Authorize(Policy = Permissions.StoreManageOrders)]
    public async Task<ActionResult<StoreOrderDto>> Cancel(
        Guid id, [FromBody] OrderActionRequest? request, CancellationToken ct) =>
        Ok(await _store.TransitionOrderAsync(id, StoreOrderStatus.Cancelled, request?.Note, ct));

    /// <summary>Refunds to the wallet the order was paid from; store credit never becomes cash.</summary>
    [HttpPost("orders/{id:guid}/refund")]
    [Authorize(Policy = Permissions.WalletRefund)]
    public async Task<ActionResult<StoreOrderDto>> Refund(
        Guid id, OrderRefundRequest request, CancellationToken ct) =>
        Ok(await _store.RefundOrderAsync(id, request.Reason, ct));

    // ---------------------------------------------------------------- settings

    [HttpGet("settings")]
    [Authorize(Policy = Permissions.StoreRead)]
    public async Task<ActionResult<StoreSettingsDto>> Settings(CancellationToken ct) =>
        Ok(await _store.GetSettingsAsync(ct));

    [HttpPut("settings")]
    [Authorize(Policy = Permissions.StoreManageSettings)]
    public async Task<ActionResult<StoreSettingsDto>> UpdateSettings(
        StoreSettingsDto request, CancellationToken ct) =>
        Ok(await _store.UpdateSettingsAsync(request, ct));
}

public sealed record OrderActionRequest(string? Note);
public sealed record OrderRefundRequest(string Reason);
