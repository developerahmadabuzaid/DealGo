using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Identity;
using AuctionPlatform.Application.Wallets;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuctionPlatform.Infrastructure.Identity;

/// <summary>
/// Authentication with short-lived access tokens and rotating refresh tokens.
///
/// Refresh tokens are stored only as SHA-256 hashes and are single-use: presenting one consumes it
/// and issues a replacement. Presenting an already-consumed token is treated as theft and revokes
/// the whole token family for that user.
/// </summary>
public sealed class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<ApplicationRole> _roles;
    private readonly ApplicationDbContext _db;
    private readonly IWalletService _wallets;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _clock;
    private readonly JwtOptions _jwt;
    private readonly AuctionOptions _auctionOptions;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> users,
        RoleManager<ApplicationRole> roles,
        ApplicationDbContext db,
        IWalletService wallets,
        ICurrentUserService currentUser,
        IDateTimeProvider clock,
        IOptions<JwtOptions> jwt,
        IOptions<AuctionOptions> auctionOptions,
        ILogger<AuthService> logger)
    {
        _users = users;
        _roles = roles;
        _db = db;
        _wallets = wallets;
        _currentUser = currentUser;
        _clock = clock;
        _jwt = jwt.Value;
        _auctionOptions = auctionOptions.Value;
        _logger = logger;
    }

    public async Task<AuthResultDto> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var existing = await _users.FindByEmailAsync(request.Email);
        if (existing is not null)
        {
            throw new DomainException(ErrorCodes.EmailAlreadyRegistered, "That email address is already registered.");
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            PhoneNumber = request.PhoneNumber,
            Language = string.IsNullOrWhiteSpace(request.Language) ? "ar" : request.Language,
            IsActive = true,
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow
        };

        var result = await _users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed,
                "Registration failed.",
                result.Errors.Select(e => e.Description).ToList());
        }

        await _users.AddToRoleAsync(user, Roles.User);

        // Every user gets both wallets up front so no code path has to create one mid-transaction.
        await _wallets.EnsureWalletsExistAsync(user.Id, _auctionOptions.DefaultCurrency, ct);

        _logger.LogInformation("Registered user {UserId} ({Email}).", user.Id, user.Email);

        return await IssueTokensAsync(user, ct);
    }

    public async Task<AuthResultDto> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await _users.FindByEmailAsync(request.Email);

        // The same error for unknown email and wrong password, so the endpoint does not confirm
        // which accounts exist.
        if (user is null || !await _users.CheckPasswordAsync(user, request.Password))
        {
            throw new DomainException(ErrorCodes.InvalidCredentials, "Incorrect email or password.");
        }

        if (!user.IsActive)
        {
            throw new DomainException(ErrorCodes.AccountDisabled, "This account has been disabled.");
        }

        user.LastLoginAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _wallets.EnsureWalletsExistAsync(user.Id, _auctionOptions.DefaultCurrency, ct);

        return await IssueTokensAsync(user, ct);
    }

    public async Task<AuthResultDto> RefreshAsync(RefreshRequest request, CancellationToken ct = default)
    {
        var hash = HashToken(request.RefreshToken);

        var stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null)
        {
            throw new DomainException(ErrorCodes.InvalidRefreshToken, "Invalid refresh token.");
        }

        if (stored.UsedAt is not null)
        {
            // A consumed token was replayed. Treat the whole family as compromised.
            await RevokeAllAsync(stored.UserId, "Refresh token reuse detected", ct);
            _logger.LogWarning("Refresh token reuse detected for user {UserId}.", stored.UserId);
            throw new DomainException(ErrorCodes.InvalidRefreshToken, "This refresh token has already been used.");
        }

        if (!stored.IsActive)
        {
            throw new DomainException(ErrorCodes.InvalidRefreshToken, "The refresh token has expired or been revoked.");
        }

        var user = stored.User ?? await _users.FindByIdAsync(stored.UserId.ToString())
            ?? throw new DomainException(ErrorCodes.InvalidRefreshToken, "Invalid refresh token.");

        if (!user.IsActive)
        {
            throw new DomainException(ErrorCodes.AccountDisabled, "This account has been disabled.");
        }

        var issued = await IssueTokensAsync(user, ct, rotatedFrom: stored);
        return issued;
    }

    public async Task LogoutAsync(Guid userId, LogoutRequest request, CancellationToken ct = default)
    {
        if (request.AllDevices)
        {
            await RevokeAllAsync(userId, "Logged out of all devices", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.RefreshToken)) return;

        var hash = HashToken(request.RefreshToken);

        await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, _clock.UtcNow)
                .SetProperty(t => t.RevokedReason, "Logged out"), ct);
    }

    private async Task RevokeAllAsync(Guid userId, string reason, CancellationToken ct)
    {
        await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, _clock.UtcNow)
                .SetProperty(t => t.RevokedReason, reason), ct);
    }

    private async Task<AuthResultDto> IssueTokensAsync(
        ApplicationUser user, CancellationToken ct, RefreshToken? rotatedFrom = null)
    {
        var roles = await _users.GetRolesAsync(user);
        var permissions = ResolvePermissions(roles);

        var now = _clock.UtcNow;
        var accessExpires = now.AddMinutes(_jwt.AccessTokenMinutes);
        var refreshExpires = now.AddDays(_jwt.RefreshTokenDays);

        var accessToken = BuildAccessToken(user, roles, permissions, now, accessExpires);
        var refreshTokenValue = GenerateRefreshToken();

        var refreshToken = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashToken(refreshTokenValue),
            CreatedAt = now,
            ExpiresAt = refreshExpires,
            CreatedByIp = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent
        };

        _db.RefreshTokens.Add(refreshToken);

        if (rotatedFrom is not null)
        {
            rotatedFrom.UsedAt = now;
            rotatedFrom.ReplacedByTokenId = refreshToken.Id;
        }

        await _db.SaveChangesAsync(ct);

        return new AuthResultDto(
            accessToken, refreshTokenValue, accessExpires, refreshExpires,
            MapProfile(user, roles, permissions));
    }

    private string BuildAccessToken(
        ApplicationUser user,
        IEnumerable<string> roles,
        IEnumerable<string> permissions,
        DateTime issuedAt,
        DateTime expiresAt)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("name", user.FullName),
            new("lang", user.Language)
        };

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        // Permissions travel in the token, but the server still re-checks them on every request.
        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            notBefore: issuedAt,
            expires: expiresAt,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal static IReadOnlyList<string> ResolvePermissions(IEnumerable<string> roles)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in roles)
        {
            if (Permissions.ByRole.TryGetValue(role, out var granted))
            {
                foreach (var permission in granted) result.Add(permission);
            }
        }

        return result.ToList();
    }

    private static string GenerateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Only the hash is persisted, so a database leak does not yield usable tokens.</summary>
    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }

    public async Task<UserProfileDto> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId.ToString())
            ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");

        var roles = await _users.GetRolesAsync(user);
        return MapProfile(user, roles, ResolvePermissions(roles));
    }

    public async Task<UserProfileDto> UpdateProfileAsync(
        Guid userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId.ToString())
            ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");

        if (request.FullName is not null) user.FullName = request.FullName;
        if (request.PhoneNumber is not null) user.PhoneNumber = request.PhoneNumber;
        if (request.Language is not null) user.Language = request.Language;
        if (request.NotificationsEnabled.HasValue) user.NotificationsEnabled = request.NotificationsEnabled.Value;

        user.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        var roles = await _users.GetRolesAsync(user);
        return MapProfile(user, roles, ResolvePermissions(roles));
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default)
    {
        var user = await _users.FindByIdAsync(userId.ToString())
            ?? throw new DomainException(ErrorCodes.NotFound, "User not found.");

        var result = await _users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed, "Password change failed.",
                result.Errors.Select(e => e.Description).ToList());
        }

        // Changing a password invalidates every outstanding session.
        await RevokeAllAsync(userId, "Password changed", ct);
    }

    /// <summary>
    /// Returns the reset token. In production this is emailed rather than returned; the API only
    /// echoes it outside Production so the flow is testable.
    /// </summary>
    public async Task<string> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var user = await _users.FindByEmailAsync(request.Email);
        if (user is null) return string.Empty;

        return await _users.GeneratePasswordResetTokenAsync(user);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var user = await _users.FindByEmailAsync(request.Email)
            ?? throw new DomainException(ErrorCodes.InvalidCredentials, "Invalid reset request.");

        var result = await _users.ResetPasswordAsync(user, request.Token, request.NewPassword);
        if (!result.Succeeded)
        {
            throw new DomainException(
                ErrorCodes.ValidationFailed, "Password reset failed.",
                result.Errors.Select(e => e.Description).ToList());
        }

        await RevokeAllAsync(user.Id, "Password reset", ct);
    }

    public async Task VerifyOtpAsync(VerifyOtpRequest request, CancellationToken ct = default)
    {
        var user = await _users.FindByEmailAsync(request.Email)
            ?? throw new DomainException(ErrorCodes.InvalidOtp, "Invalid verification code.");

        var valid = await _users.VerifyChangePhoneNumberTokenAsync(
            user, request.Code, user.PhoneNumber ?? string.Empty);

        if (!valid)
        {
            throw new DomainException(ErrorCodes.InvalidOtp, "Invalid verification code.");
        }

        user.EmailConfirmed = true;
        user.PhoneNumberConfirmed = true;
        await _db.SaveChangesAsync(ct);
    }

    private static UserProfileDto MapProfile(
        ApplicationUser user, IEnumerable<string> roles, IReadOnlyList<string> permissions) =>
        new(user.Id, user.FullName, user.Email ?? string.Empty, user.PhoneNumber,
            user.Language, user.EmailConfirmed, user.IsActive,
            roles.ToList(), permissions, user.CreatedAt);
}
