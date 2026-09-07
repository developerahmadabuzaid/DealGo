using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// Prize products used by auctions. Distinct from the shop catalog: these are awarded, not sold.
/// </summary>
[Route("api/v1/admin/catalog/products")]
[Authorize]
public sealed class AdminCatalogController : ApiControllerBase
{
    private readonly ICatalogService _catalog;

    public AdminCatalogController(ICatalogService catalog) => _catalog = catalog;

    [HttpGet]
    [Authorize(Policy = Permissions.AuctionRead)]
    public async Task<ActionResult<PagedResult<CatalogProductDto>>> List(
        [FromQuery] PagedQuery query, CancellationToken ct) =>
        Ok(await _catalog.ListAsync(query, ct));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.AuctionRead)]
    public async Task<ActionResult<CatalogProductDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await _catalog.GetAsync(id, ct));

    [HttpPost]
    [Authorize(Policy = Permissions.AuctionCreate)]
    public async Task<ActionResult<CatalogProductDto>> Create(
        SaveCatalogProductRequest request, CancellationToken ct)
    {
        var product = await _catalog.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, product);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.AuctionUpdate)]
    public async Task<ActionResult<CatalogProductDto>> Update(
        Guid id, SaveCatalogProductRequest request, CancellationToken ct) =>
        Ok(await _catalog.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.AuctionUpdate)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _catalog.DeleteAsync(id, ct);
        return NoContent();
    }
}
