using AuctionPlatform.Application.Site;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// The support WhatsApp number shown in the app. Gated like the landing page: it is something the
/// platform publishes to everyone, not an operation on anyone's account.
/// </summary>
[Route("api/v1/admin/support")]
[Authorize(Policy = Permissions.SiteManage)]
public sealed class AdminSupportController : ApiControllerBase
{
    private readonly ISupportService _support;

    public AdminSupportController(ISupportService support) => _support = support;

    [HttpGet]
    public async Task<ActionResult<SupportSettingsDto>> Get(CancellationToken ct) =>
        Ok(await _support.GetAsync(ct));

    [HttpPut]
    public async Task<ActionResult<SupportSettingsDto>> Update(
        UpdateSupportSettingsRequest request, CancellationToken ct) =>
        Ok(await _support.UpdateAsync(request, ct));
}
