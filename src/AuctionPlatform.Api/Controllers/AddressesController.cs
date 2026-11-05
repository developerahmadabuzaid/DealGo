using AuctionPlatform.Application.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

/// <summary>
/// The signed-in customer's delivery addresses, used by store checkout and prize claims. Every
/// call is scoped to the caller: another account's address answers as not found.
/// </summary>
[Route("api/v1/me/addresses")]
[Authorize]
public sealed class AddressesController : ApiControllerBase
{
    private readonly IShippingAddressService _addresses;

    public AddressesController(IShippingAddressService addresses) => _addresses = addresses;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ShippingAddressDto>>> List(CancellationToken ct) =>
        Ok(await _addresses.GetAsync(CurrentUserId, ct));

    [HttpPost]
    public async Task<ActionResult<ShippingAddressDto>> Create(
        SaveShippingAddressRequest request, CancellationToken ct) =>
        Ok(await _addresses.CreateAsync(CurrentUserId, request, ct));

    /// <summary>The response's id can differ from the one asked for: see <see cref="IShippingAddressService.UpdateAsync"/>.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ShippingAddressDto>> Update(
        Guid id, SaveShippingAddressRequest request, CancellationToken ct) =>
        Ok(await _addresses.UpdateAsync(CurrentUserId, id, request, ct));

    [HttpPost("{id:guid}/default")]
    public async Task<ActionResult<ShippingAddressDto>> SetDefault(Guid id, CancellationToken ct) =>
        Ok(await _addresses.SetDefaultAsync(CurrentUserId, id, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _addresses.DeleteAsync(CurrentUserId, id, ct);
        return NoContent();
    }
}
