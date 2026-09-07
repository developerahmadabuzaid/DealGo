using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

[Route("api/v1/store")]
public sealed class StoreController : ApiControllerBase
{
    private readonly IStoreCatalogService _catalog;
    private readonly IStoreOrderService _orders;

    public StoreController(IStoreCatalogService catalog, IStoreOrderService orders)
    {
        _catalog = catalog;
        _orders = orders;
    }

    [HttpGet("home")]
    [AllowAnonymous]
    public async Task<ActionResult<StoreHomeDto>> Home(CancellationToken ct) =>
        Ok(await _catalog.GetHomeAsync(CurrentUserIdOrNull, ct));

    [HttpGet("products")]
    [AllowAnonymous]
    public async Task<ActionResult<PagedResult<StoreProductListItemDto>>> Products(
        [FromQuery] StoreProductQuery query, CancellationToken ct)
    {
        // Status is an administrative filter; public callers always see the public visibility set.
        query.Status = null;
        return Ok(await _catalog.GetProductsAsync(query, ct));
    }

    [HttpGet("products/{id:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<StoreProductDetailDto>> Product(Guid id, CancellationToken ct) =>
        Ok(await _catalog.GetProductAsync(id, ct));

    [HttpGet("categories")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<StoreCategoryDto>>> Categories(CancellationToken ct) =>
        Ok(await _catalog.GetCategoriesAsync(ct));

    [HttpGet("categories/{id:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<StoreCategoryDto>> Category(Guid id, CancellationToken ct) =>
        Ok(await _catalog.GetCategoryAsync(id, ct));

    /// <summary>Server-calculated totals for a cart, before committing to the purchase.</summary>
    [HttpPost("checkout/quote")]
    [Authorize]
    public async Task<ActionResult<CheckoutQuoteDto>> Quote(CheckoutRequest request, CancellationToken ct) =>
        Ok(await _orders.QuoteAsync(CurrentUserId, request, ct));

    [HttpPost("orders")]
    [Authorize]
    public async Task<ActionResult<StoreOrderDto>> Checkout(CheckoutRequest request, CancellationToken ct)
    {
        var order = await _orders.CheckoutAsync(CurrentUserId, request, ct);
        return CreatedAtAction(nameof(Order), new { id = order.Id }, order);
    }

    [HttpGet("orders")]
    [Authorize]
    public async Task<ActionResult<PagedResult<StoreOrderDto>>> Orders(
        [FromQuery] StoreOrderQuery query, CancellationToken ct) =>
        Ok(await _orders.GetMyOrdersAsync(CurrentUserId, query, ct));

    [HttpGet("orders/{id:guid}")]
    [Authorize]
    public async Task<ActionResult<StoreOrderDto>> Order(Guid id, CancellationToken ct) =>
        Ok(await _orders.GetOrderAsync(CurrentUserId, id, ct));

    [HttpPost("orders/{id:guid}/cancel")]
    [Authorize]
    public async Task<ActionResult<StoreOrderDto>> CancelOrder(
        Guid id, [FromBody] CancelOrderRequest? request, CancellationToken ct) =>
        Ok(await _orders.CancelOrderAsync(CurrentUserId, id, request?.Reason, ct));

    [HttpGet("addresses")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<ShippingAddressDto>>> Addresses(CancellationToken ct) =>
        Ok(await _orders.GetAddressesAsync(CurrentUserId, ct));

    [HttpPost("addresses")]
    [Authorize]
    public async Task<ActionResult<ShippingAddressDto>> SaveAddress(
        SaveShippingAddressRequest request, CancellationToken ct) =>
        Ok(await _orders.SaveAddressAsync(CurrentUserId, request, ct));

    [HttpDelete("addresses/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> DeleteAddress(Guid id, CancellationToken ct)
    {
        await _orders.DeleteAddressAsync(CurrentUserId, id, ct);
        return NoContent();
    }
}

public sealed record CancelOrderRequest(string? Reason);
