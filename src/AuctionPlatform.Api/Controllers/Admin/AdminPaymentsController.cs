using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// Watching the money that comes in through Paymob: health figures, the full list of top-ups to
/// compare with Paymob's own report, and a way to run reconciliation now. Nothing here can credit a
/// wallet; reconciliation applies only what Paymob confirms.
/// </summary>
[Route("api/v1/admin/payments")]
[Authorize]
public sealed class AdminPaymentsController : ApiControllerBase
{
    private readonly IPaymentMonitorService _monitor;
    private readonly IWalletTopUpService _topUps;

    public AdminPaymentsController(IPaymentMonitorService monitor, IWalletTopUpService topUps)
    {
        _monitor = monitor;
        _topUps = topUps;
    }

    [HttpGet("health")]
    [Authorize(Policy = Permissions.WalletRead)]
    public async Task<ActionResult<PaymentHealthDto>> Health(CancellationToken ct) =>
        Ok(await _monitor.GetHealthAsync(ct));

    [HttpGet("topups")]
    [Authorize(Policy = Permissions.WalletRead)]
    public async Task<ActionResult<PagedResult<TopUpRowDto>>> TopUps(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] PaymentStatus? status,
        [FromQuery] PagedQuery page, CancellationToken ct) =>
        Ok(await _monitor.ListTopUpsAsync(from, to, status, page, ct));

    [HttpPost("reconcile")]
    [Authorize(Policy = Permissions.WalletAdjust)]
    public async Task<ActionResult<TopUpReconciliation>> Reconcile(CancellationToken ct) =>
        Ok(await _topUps.ReconcileAsync(ct));
}
