using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

[Route("api/v1/wallet")]
[Authorize]
public sealed class WalletController : ApiControllerBase
{
    private readonly IWalletService _wallets;
    private readonly IWebHostEnvironment _environment;

    public WalletController(IWalletService wallets, IWebHostEnvironment environment)
    {
        _wallets = wallets;
        _environment = environment;
    }

    /// <summary>
    /// Both balances, reported separately. There is deliberately no combined total: presenting one
    /// would imply the shopping credit is withdrawable.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<WalletsDto>> Get(CancellationToken ct) =>
        Ok(await _wallets.GetWalletsAsync(CurrentUserId, ct));

    [HttpGet("transactions")]
    public async Task<ActionResult<PagedResult<WalletTransactionDto>>> Transactions(
        [FromQuery] WalletTransactionQuery query, CancellationToken ct) =>
        Ok(await _wallets.GetTransactionsAsync(CurrentUserId, query, ct));

    [HttpGet("shopping")]
    public async Task<ActionResult<WalletDto>> Shopping(CancellationToken ct)
    {
        var wallets = await _wallets.GetWalletsAsync(CurrentUserId, ct);
        return Ok(wallets.Shopping);
    }

    [HttpGet("shopping/transactions")]
    public async Task<ActionResult<PagedResult<WalletTransactionDto>>> ShoppingTransactions(
        [FromQuery] WalletTransactionQuery query, CancellationToken ct)
    {
        query.WalletType = WalletType.Shopping;
        return Ok(await _wallets.GetTransactionsAsync(CurrentUserId, query, ct));
    }

    [HttpGet("main/transactions")]
    public async Task<ActionResult<PagedResult<WalletTransactionDto>>> MainTransactions(
        [FromQuery] WalletTransactionQuery query, CancellationToken ct)
    {
        query.WalletType = WalletType.Main;
        return Ok(await _wallets.GetTransactionsAsync(CurrentUserId, query, ct));
    }

    [HttpPost("deposit")]
    public async Task<ActionResult<WalletOperationResult>> Deposit(
        DepositRequest request, CancellationToken ct) =>
        Ok(await _wallets.DepositAsync(CurrentUserId, request, ct));

    /// <summary>
    /// Sends the code that confirms a withdrawal. The code is bound to this exact amount,
    /// currency and destination.
    /// </summary>
    [HttpPost("withdraw/request-otp")]
    public async Task<ActionResult<OtpChallengeDto>> RequestWithdrawalOtp(
        RequestWithdrawalOtpRequest request, CancellationToken ct)
    {
        var challenge = await _wallets.RequestWithdrawalOtpAsync(CurrentUserId, request, ct);

        return Ok(_environment.IsProduction()
            ? challenge with { DevelopmentCode = null }
            : challenge);
    }

    /// <summary>
    /// Withdraws from the Main wallet. Requires the confirmation code issued by
    /// <c>withdraw/request-otp</c> whenever withdrawal OTP is enabled.
    /// </summary>
    [HttpPost("withdraw")]
    public async Task<ActionResult<WalletOperationResult>> Withdraw(
        WithdrawRequest request, CancellationToken ct) =>
        Ok(await _wallets.WithdrawAsync(CurrentUserId, request, ct));

    // The Shopping wallet is a closed loop. These endpoints exist so the refusal is explicit and
    // documented rather than a 404 that looks like a missing feature.

    [HttpPost("shopping/withdraw")]
    public IActionResult ShoppingWithdraw() => ShoppingNotAllowed();

    [HttpPost("shopping/transfer")]
    public IActionResult ShoppingTransfer() => ShoppingNotAllowed();

    [HttpPost("shopping/convert")]
    public IActionResult ShoppingConvert() => ShoppingNotAllowed();

    private ObjectResult ShoppingNotAllowed() =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            code = ErrorCodes.ShoppingWalletOperationNotAllowed,
            message = "Shopping Wallet balance can only be used for store purchases.",
            details = Array.Empty<string>()
        });
}
