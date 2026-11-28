using AuctionPlatform.Application.Site;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// Writing the auction policies people accept before joining. Gated like the landing page and the
/// support number: it is text the platform publishes to everyone.
/// </summary>
[Route("api/v1/admin/auction-policy")]
[Authorize(Policy = Permissions.SiteManage)]
public sealed class AdminAuctionPolicyController : ApiControllerBase
{
    private readonly IAuctionPolicyService _policies;

    public AdminAuctionPolicyController(IAuctionPolicyService policies) => _policies = policies;

    [HttpGet]
    public async Task<ActionResult<AuctionPolicyDto>> Get(CancellationToken ct) =>
        Ok(await _policies.GetAsync(ct));

    [HttpPut]
    public async Task<ActionResult<AuctionPolicyDto>> Update(
        UpdateAuctionPolicyRequest request, CancellationToken ct) =>
        Ok(await _policies.UpdateAsync(request, ct));
}
