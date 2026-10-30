using AuctionPlatform.Application.Site;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

/// <summary>
/// How to reach support. Anonymous, because someone who cannot sign in is exactly who needs it.
/// </summary>
[ApiController]
[Route("api/v1/support")]
[AllowAnonymous]
public sealed class SupportController : ControllerBase
{
    private readonly ISupportService _support;

    public SupportController(ISupportService support) => _support = support;

    [HttpGet]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    public async Task<ActionResult<SupportSettingsDto>> Get(CancellationToken ct) =>
        Ok(await _support.GetAsync(ct));
}
