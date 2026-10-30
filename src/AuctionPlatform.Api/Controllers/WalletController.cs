using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

[Route("api/v1/wallet")]
[Authorize]
public sealed class WalletController : ApiControllerBase
{
    private readonly IWalletService _wallets;
    private readonly IWalletTopUpService _topUps;
    private readonly IWebHostEnvironment _environment;
    private readonly PaymentOptions _payments;

    public WalletController(
        IWalletService wallets,
        IWalletTopUpService topUps,
        IWebHostEnvironment environment,
        IOptions<PaymentOptions> payments)
    {
        _wallets = wallets;
        _topUps = topUps;
        _environment = environment;
        _payments = payments.Value;
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

    /// <summary>
    /// Adds balance with no payment behind it. For development only: in Production it is refused
    /// whatever the settings say, because there it let anyone signed in mint money. Real money
    /// comes in through <c>topups</c>.
    /// </summary>
    [HttpPost("deposit")]
    public async Task<ActionResult<WalletOperationResult>> Deposit(
        DepositRequest request, CancellationToken ct)
    {
        if (_environment.IsProduction() || !_payments.AllowSandboxDeposits)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                code = ErrorCodes.Forbidden,
                message = "Top up the wallet through the payment page.",
                details = Array.Empty<string>()
            });
        }

        return Ok(await _wallets.DepositAsync(CurrentUserId, request, ct));
    }

    public sealed record StartTopUpRequest(decimal Amount);

    /// <summary>
    /// Opens a card, mada or Apple Pay payment with Paymob for a top-up of the Main wallet and
    /// returns the checkout page to send the customer to. The wallet is credited when Paymob
    /// confirms the payment, never by this call.
    /// </summary>
    [HttpPost("topups")]
    public async Task<ActionResult<TopUpStartDto>> StartTopUp(StartTopUpRequest request, CancellationToken ct) =>
        Ok(await _topUps.StartAsync(CurrentUserId, request.Amount, $"{Request.Scheme}://{Request.Host}", ct));

    /// <summary>Where a top-up stands: the app asks this when the customer returns from paying.</summary>
    [HttpGet("topups/{paymentId:guid}")]
    public async Task<ActionResult<TopUpStatusDto>> TopUpStatus(Guid paymentId, CancellationToken ct) =>
        Ok(await _topUps.GetAsync(CurrentUserId, paymentId, ct));

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
