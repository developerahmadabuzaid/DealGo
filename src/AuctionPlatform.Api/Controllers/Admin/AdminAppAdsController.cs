using AuctionPlatform.Application.Site;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// The advertisements on the app's front page.
///
/// Gated on the same permission as the public page: both are publishing rather than operating the
/// platform, and the people who write what customers read are the same people either way.
/// </summary>
[Route("api/v1/admin/ads")]
[Authorize(Policy = Permissions.SiteManage)]
public sealed class AdminAppAdsController : ApiControllerBase
{
    private readonly IAppAdService _ads;

    public AdminAppAdsController(IAppAdService ads) => _ads = ads;

    /// <summary>Every advertisement, drafts and finished campaigns included.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AppAdDto>>> List(CancellationToken ct) =>
        Ok(await _ads.GetAllAsync(ct));

    [HttpPost]
    public async Task<ActionResult<AppAdDto>> Create(AppAdRequest request, CancellationToken ct) =>
        Ok(await _ads.SaveAsync(null, request, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AppAdDto>> Update(
        Guid id, AppAdRequest request, CancellationToken ct) =>
        Ok(await _ads.SaveAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _ads.DeleteAsync(id, ct);
        return NoContent();
    }
}
