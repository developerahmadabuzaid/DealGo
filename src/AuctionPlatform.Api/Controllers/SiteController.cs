using AuctionPlatform.Application.Site;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

/// <summary>
/// What the public landing page reads.
///
/// Anonymous by design: this is the page a visitor sees before they have an account, and it is
/// the only endpoint on the platform whose whole purpose is to be read by strangers. It returns
/// published rows only, so a review or a question being drafted in the console stays private
/// until someone decides otherwise.
/// </summary>
[ApiController]
[Route("api/v1/site")]
[AllowAnonymous]
public sealed class SiteController : ControllerBase
{
    private readonly ISiteContentService _site;

    public SiteController(ISiteContentService site) => _site = site;

    [HttpGet("content")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<SiteContentDto>> Content(CancellationToken ct) =>
        Ok(await _site.GetPublicAsync(ct));
}
