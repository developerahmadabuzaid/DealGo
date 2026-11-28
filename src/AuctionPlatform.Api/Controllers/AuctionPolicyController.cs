using AuctionPlatform.Application.Site;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

/// <summary>
/// The auction policies the app shows before joining. Anonymous, so they can be read before
/// signing in. Not cached: a join must name the version that is current right now.
/// </summary>
[ApiController]
[Route("api/v1/auction-policy")]
[AllowAnonymous]
public sealed class AuctionPolicyController : ControllerBase
{
    private readonly IAuctionPolicyService _policies;

    public AuctionPolicyController(IAuctionPolicyService policies) => _policies = policies;

    [HttpGet]
    public async Task<ActionResult<AuctionPolicyDto>> Get(CancellationToken ct) =>
        Ok(await _policies.GetAsync(ct));
}
