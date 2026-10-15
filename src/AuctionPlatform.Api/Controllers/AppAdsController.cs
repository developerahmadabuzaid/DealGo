using AuctionPlatform.Application.Site;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

/// <summary>
/// What the app's front page advertises.
///
/// Anonymous and cached: the same pictures are shown to everybody, they change rarely, and the
/// front page asks for them on every visit. Only advertisements that are published and in season
/// are returned, so a campaign being prepared in the console stays out of the app.
/// </summary>
[ApiController]
[Route("api/v1/ads")]
[AllowAnonymous]
public sealed class AppAdsController : ControllerBase
{
    private readonly IAppAdService _ads;

    public AppAdsController(IAppAdService ads) => _ads = ads;

    [HttpGet]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<IReadOnlyList<AppAdDto>>> List(CancellationToken ct) =>
        Ok(await _ads.GetLiveAsync(ct));
}
