using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers.Admin;

[Route("api/v1/admin")]
[Authorize]
public sealed class AdminWalletsController : ApiControllerBase
{
    private readonly IAdminWalletService _wallets;
    private readonly IWalletService _walletService;

    public AdminWalletsController(IAdminWalletService wallets, IWalletService walletService)
    {
        _wallets = wallets;
        _walletService = walletService;
    }

    [HttpGet("wallets")]
    [Authorize(Policy = Permissions.WalletRead)]
    public async Task<ActionResult<PagedResult<AdminWalletRowDto>>> Wallets(
        [FromQuery] AdminWalletQuery query, CancellationToken ct) =>
        Ok(await _wallets.ListAsync(query, ct));

    [HttpGet("wallets/{userId:guid}")]
    [Authorize(Policy = Permissions.WalletRead)]
    public async Task<ActionResult<AdminWalletRowDto>> Wallet(Guid userId, CancellationToken ct) =>
        Ok(await _wallets.GetAsync(userId, ct));

    [HttpGet("wallets/{userId:guid}/transactions")]
    [Authorize(Policy = Permissions.WalletRead)]
    public async Task<ActionResult<PagedResult<WalletTransactionDto>>> Transactions(
        Guid userId, [FromQuery] WalletTransactionQuery query, CancellationToken ct) =>
        Ok(await _walletService.GetTransactionsAsync(userId, query, ct));

    [HttpGet("shopping-credits")]
    [Authorize(Policy = Permissions.WalletRead)]
    public async Task<ActionResult<PagedResult<ShoppingCreditByUserDto>>> ShoppingCredits(
        [FromQuery] PagedQuery query, CancellationToken ct) =>
        Ok(await _wallets.GetShoppingCreditsAsync(query, ct));

    [HttpGet("shopping-credits/summary")]
    [Authorize(Policy = Permissions.WalletRead)]
    public async Task<ActionResult<ShoppingCreditSummaryDto>> ShoppingCreditSummary(
        [FromQuery] string currency = Money.DefaultCurrency, CancellationToken ct = default) =>
        Ok(await _walletService.GetShoppingCreditSummaryAsync(currency, ct));

    /// <summary>
    /// Issues shopping credit to one user. Requires the Wallet.Adjust permission, a written reason
    /// and an idempotency key; the result is always audited.
    /// </summary>
    [HttpPost("users/{userId:guid}/shopping-credit")]
    [Authorize(Policy = Permissions.WalletAdjust)]
    public async Task<ActionResult<WalletOperationResult>> IssueShoppingCredit(
        Guid userId, AdminCreditRequest request, CancellationToken ct) =>
        Ok(await _wallets.AdjustAsync(userId, request with { WalletType = WalletType.Shopping }, ct));

    /// <summary>Adjusts either wallet. Positive credits, negative debits.</summary>
    [HttpPost("users/{userId:guid}/wallet-adjustment")]
    [Authorize(Policy = Permissions.WalletAdjust)]
    public async Task<ActionResult<WalletOperationResult>> AdjustWallet(
        Guid userId, AdminCreditRequest request, CancellationToken ct) =>
        Ok(await _wallets.AdjustAsync(userId, request, ct));
}

[Route("api/v1/admin/users")]
[Authorize]
public sealed class AdminUsersController : ApiControllerBase
{
    private readonly IUserAdminService _users;

    public AdminUsersController(IUserAdminService users) => _users = users;

    [HttpGet]
    [Authorize(Policy = Permissions.UserRead)]
    public async Task<ActionResult<PagedResult<AdminUserDto>>> List(
        [FromQuery] UserListQuery query, CancellationToken ct) =>
        Ok(await _users.ListAsync(query, ct));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Permissions.UserRead)]
    public async Task<ActionResult<AdminUserDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await _users.GetAsync(id, ct));

    /// <summary>Role assignment is restricted to SUPER_ADMIN.</summary>
    [HttpPut("{id:guid}/roles")]
    [Authorize(Roles = Roles.SuperAdmin)]
    public async Task<ActionResult<AdminUserDto>> SetRoles(
        Guid id, SetUserRolesRequest request, CancellationToken ct) =>
        Ok(await _users.SetRolesAsync(id, request, ct));

    [HttpPut("{id:guid}/active")]
    [Authorize(Policy = Permissions.UserManage)]
    public async Task<ActionResult<AdminUserDto>> SetActive(
        Guid id, SetUserActiveRequest request, CancellationToken ct) =>
        Ok(await _users.SetActiveAsync(id, request, ct));
}
