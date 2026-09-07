using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuctionPlatform.Infrastructure.Identity;

public sealed class UserAdminService : IUserAdminService
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;

    public UserAdminService(
        ApplicationDbContext db,
        UserManager<ApplicationUser> users,
        IAuditService audit,
        IDateTimeProvider clock)
    {
        _db = db;
        _users = users;
        _audit = audit;
        _clock = clock;
    }

    public async Task<PagedResult<AdminUserDto>> ListAsync(UserListQuery query, CancellationToken ct = default)
    {
        var q = _db.Users.AsNoTracking().AsQueryable();

        if (query.IsActive.HasValue) q = q.Where(u => u.IsActive == query.IsActive.Value);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(u => u.FullName.ToLower().Contains(term) ||
                             (u.Email != null && u.Email.ToLower().Contains(term)) ||
                             (u.PhoneNumber != null && u.PhoneNumber.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            var role = query.Role;
            var userIds = from ur in _db.UserRoles
                          join r in _db.Roles on ur.RoleId equals r.Id
                          where r.Name == role
                          select ur.UserId;

            q = q.Where(u => userIds.Contains(u.Id));
        }

        var total = await q.LongCountAsync(ct);

        var users = await q
            .OrderByDescending(u => u.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .ToListAsync(ct);

        return PagedResult<AdminUserDto>.Create(await MapAsync(users, ct), query.Page, query.PageSize, total);
    }

    public async Task<AdminUserDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");

        return (await MapAsync(new[] { user }, ct))[0];
    }

    private async Task<IReadOnlyList<AdminUserDto>> MapAsync(
        IReadOnlyCollection<ApplicationUser> users, CancellationToken ct)
    {
        var ids = users.Select(u => u.Id).ToList();

        var wallets = await _db.Wallets
            .AsNoTracking()
            .Where(w => ids.Contains(w.UserId))
            .Select(w => new { w.UserId, w.WalletType, w.Balance, w.Currency })
            .ToListAsync(ct);

        var roleRows = await (from ur in _db.UserRoles
                              join r in _db.Roles on ur.RoleId equals r.Id
                              where ids.Contains(ur.UserId)
                              select new { ur.UserId, RoleName = r.Name! })
            .ToListAsync(ct);

        var rolesByUser = roleRows
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.RoleName).ToList());

        return users.Select(u =>
        {
            var main = wallets.FirstOrDefault(w => w.UserId == u.Id && w.WalletType == WalletType.Main);
            var shopping = wallets.FirstOrDefault(w => w.UserId == u.Id && w.WalletType == WalletType.Shopping);

            return new AdminUserDto(
                u.Id, u.FullName, u.Email ?? string.Empty, u.PhoneNumber, u.IsActive,
                rolesByUser.GetValueOrDefault(u.Id, Array.Empty<string>()),
                main?.Balance ?? 0m, shopping?.Balance ?? 0m,
                main?.Currency ?? Money.DefaultCurrency,
                u.CreatedAt, u.LastLoginAt);
        }).ToList();
    }

    public async Task<AdminUserDto> SetRolesAsync(
        Guid userId, SetUserRolesRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A reason is required for role changes.");
        }

        var user = await _users.FindByIdAsync(userId.ToString())
            ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");

        var invalid = request.Roles.Where(r => !Roles.All.Contains(r)).ToList();
        if (invalid.Count > 0)
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed, "Unknown role.", invalid);
        }

        var current = await _users.GetRolesAsync(user);

        await _users.RemoveFromRolesAsync(user, current.Except(request.Roles));
        await _users.AddToRolesAsync(user, request.Roles.Except(current));

        await _audit.LogAsync(AuditActionType.UserRoleChanged, nameof(ApplicationUser), userId,
            new { Roles = current }, new { Roles = request.Roles }, request.Reason, ct);

        return await GetAsync(userId, ct);
    }

    public async Task<AdminUserDto> SetActiveAsync(
        Guid userId, SetUserActiveRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "A reason is required.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");

        var before = user.IsActive;
        user.IsActive = request.IsActive;
        user.UpdatedAt = _clock.UtcNow;

        if (!request.IsActive)
        {
            // Disabling an account also invalidates every outstanding session.
            await _db.RefreshTokens
                .Where(t => t.UserId == userId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RevokedAt, _clock.UtcNow)
                    .SetProperty(t => t.RevokedReason, "Account disabled"), ct);
        }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.UserRoleChanged, nameof(ApplicationUser), userId,
            new { IsActive = before }, new { IsActive = request.IsActive }, request.Reason, ct);

        return await GetAsync(userId, ct);
    }
}
