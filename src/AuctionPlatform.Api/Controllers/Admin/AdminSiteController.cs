using AuctionPlatform.Application.Site;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// Editing the public landing page.
///
/// Every route here is gated on a permission of its own rather than on "administrator". Writing
/// the words on the front page is publishing, not operating the platform: it is the one thing in
/// this console that strangers read, and the people who should be trusted with it are not
/// necessarily the people who may cancel an auction or adjust a wallet.
/// </summary>
[Route("api/v1/admin/site")]
[Authorize(Policy = Permissions.SiteManage)]
public sealed class AdminSiteController : ApiControllerBase
{
    private readonly ISiteContentService _site;

    public AdminSiteController(ISiteContentService site) => _site = site;

    /// <summary>The whole page, drafts included.</summary>
    [HttpGet("content")]
    public async Task<ActionResult<SiteContentDto>> Content(CancellationToken ct) =>
        Ok(await _site.GetForAdminAsync(ct));

    [HttpPut("settings")]
    public async Task<ActionResult<SiteSettingsDto>> UpdateSettings(
        SiteSettingsDto request, CancellationToken ct) =>
        Ok(await _site.UpdateSettingsAsync(request, ct));

    // ---------------------------------------------------------------- cards

    [HttpPost("cards")]
    public async Task<ActionResult<SiteCardDto>> CreateCard(SiteCardRequest request, CancellationToken ct) =>
        Ok(await _site.SaveCardAsync(null, request, ct));

    [HttpPut("cards/{id:guid}")]
    public async Task<ActionResult<SiteCardDto>> UpdateCard(
        Guid id, SiteCardRequest request, CancellationToken ct) =>
        Ok(await _site.SaveCardAsync(id, request, ct));

    [HttpDelete("cards/{id:guid}")]
    public async Task<IActionResult> DeleteCard(Guid id, CancellationToken ct)
    {
        await _site.DeleteCardAsync(id, ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- reviews

    [HttpPost("testimonials")]
    public async Task<ActionResult<SiteTestimonialDto>> CreateTestimonial(
        SiteTestimonialRequest request, CancellationToken ct) =>
        Ok(await _site.SaveTestimonialAsync(null, request, ct));

    [HttpPut("testimonials/{id:guid}")]
    public async Task<ActionResult<SiteTestimonialDto>> UpdateTestimonial(
        Guid id, SiteTestimonialRequest request, CancellationToken ct) =>
        Ok(await _site.SaveTestimonialAsync(id, request, ct));

    [HttpDelete("testimonials/{id:guid}")]
    public async Task<IActionResult> DeleteTestimonial(Guid id, CancellationToken ct)
    {
        await _site.DeleteTestimonialAsync(id, ct);
        return NoContent();
    }

    // ---------------------------------------------------------------- questions

    [HttpPost("faqs")]
    public async Task<ActionResult<SiteFaqDto>> CreateFaq(SiteFaqRequest request, CancellationToken ct) =>
        Ok(await _site.SaveFaqAsync(null, request, ct));

    [HttpPut("faqs/{id:guid}")]
    public async Task<ActionResult<SiteFaqDto>> UpdateFaq(
        Guid id, SiteFaqRequest request, CancellationToken ct) =>
        Ok(await _site.SaveFaqAsync(id, request, ct));

    [HttpDelete("faqs/{id:guid}")]
    public async Task<IActionResult> DeleteFaq(Guid id, CancellationToken ct)
    {
        await _site.DeleteFaqAsync(id, ct);
        return NoContent();
    }
}
