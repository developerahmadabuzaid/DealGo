using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

/// <summary>
/// The withdrawal queue. The money left the user's wallet when they asked; staff make the bank
/// transfer and then mark it paid, or refuse it and the amount goes back to the wallet.
/// </summary>
[Route("api/v1/admin/withdrawals")]
[Authorize]
public sealed class AdminWithdrawalsController : ApiControllerBase
{
    private readonly IAdminWithdrawalService _withdrawals;

    public AdminWithdrawalsController(IAdminWithdrawalService withdrawals) => _withdrawals = withdrawals;

    [HttpGet]
    [Authorize(Policy = Permissions.WalletRead)]
    public async Task<ActionResult<PagedResult<AdminWithdrawalDto>>> List(
        [FromQuery] AdminWithdrawalQuery query, CancellationToken ct) =>
        Ok(await _withdrawals.ListAsync(query, ct));

    [HttpPost("{id:guid}/paid")]
    [Authorize(Policy = Permissions.WalletAdjust)]
    public async Task<ActionResult<AdminWithdrawalDto>> MarkPaid(
        Guid id, MarkWithdrawalPaidRequest request, CancellationToken ct) =>
        Ok(await _withdrawals.MarkPaidAsync(id, request, ct));

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Permissions.WalletAdjust)]
    public async Task<ActionResult<AdminWithdrawalDto>> Reject(
        Guid id, RejectWithdrawalRequest request, CancellationToken ct) =>
        Ok(await _withdrawals.RejectAsync(id, request, ct));
}
