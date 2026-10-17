using AuctionPlatform.Application.Common;

namespace AuctionPlatform.Application.Identity;

public sealed record RegisterRequest(
    string FullName,
    string Email,
    string Password,
    string? PhoneNumber,
    string? Language);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string? RefreshToken, bool AllDevices);

/// <summary>
/// Ask for a sign-in code. The destination is an email address or a phone number.
/// <para>
/// <c>StaffOnly</c> is set by the admin console. A destination that is not an active staff account
/// then gets the same decoy reply an unknown one does: no message is sent and no account is
/// created, so the console can neither enrol customers nor be used to send codes to any number.
/// </para>
/// </summary>
public sealed record RequestLoginOtpRequest(string Destination, string? Language, bool StaffOnly = false);

/// <summary>Exchange a sign-in code for a session. <c>StaffOnly</c> refuses non-staff accounts.</summary>
public sealed record VerifyLoginOtpRequest(string Destination, string Code, bool StaffOnly = false);

/// <summary>Legacy contact-verification code. Kept so existing clients keep working.</summary>
public sealed record VerifyOtpRequest(string Email, string Code);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record AuthResultDto(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresAt,
    DateTime RefreshTokenExpiresAt,
    UserProfileDto User);

public sealed record UserProfileDto(
    Guid Id,
    string FullName,
    string Email,
    string? PhoneNumber,
    string Language,
    bool EmailConfirmed,
    bool IsActive,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    DateTime CreatedAt);

public sealed record UpdateProfileRequest(
    string? FullName, string? PhoneNumber, string? Language, bool? NotificationsEnabled);

public sealed class UserListQuery : PagedQuery
{
    public string? Role { get; set; }
    public bool? IsActive { get; set; }
}

public sealed record AdminUserDto(
    Guid Id,
    string FullName,
    string Email,
    string? PhoneNumber,
    bool IsActive,
    IReadOnlyList<string> Roles,
    decimal MainWalletBalance,
    decimal ShoppingWalletBalance,
    string Currency,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

public sealed record SetUserRolesRequest(IReadOnlyList<string> Roles, string Reason);

public sealed record SetUserActiveRequest(bool IsActive, string Reason);

public interface IAuthService
{
    Task<AuthResultDto> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<AuthResultDto> LoginAsync(LoginRequest request, CancellationToken ct = default);

    /// <summary>Rotates the refresh token: the presented token is consumed and a new one issued.</summary>
    Task<AuthResultDto> RefreshAsync(RefreshRequest request, CancellationToken ct = default);

    Task LogoutAsync(Guid userId, LogoutRequest request, CancellationToken ct = default);

    /// <summary>
    /// Issues a sign-in code. Always succeeds from the caller's point of view, whether or not the
    /// destination belongs to an account, so the endpoint cannot be used to enumerate users.
    /// </summary>
    Task<OtpChallengeDto> RequestLoginOtpAsync(RequestLoginOtpRequest request, CancellationToken ct = default);

    /// <summary>Consumes a sign-in code and returns a session.</summary>
    Task<AuthResultDto> VerifyLoginOtpAsync(VerifyLoginOtpRequest request, CancellationToken ct = default);

    Task<UserProfileDto> GetProfileAsync(Guid userId, CancellationToken ct = default);
    Task<UserProfileDto> UpdateProfileAsync(Guid userId, UpdateProfileRequest request, CancellationToken ct = default);
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct = default);

    Task<string> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default);
    Task VerifyOtpAsync(VerifyOtpRequest request, CancellationToken ct = default);
}

public interface IUserAdminService
{
    Task<PagedResult<AdminUserDto>> ListAsync(UserListQuery query, CancellationToken ct = default);
    Task<AdminUserDto> GetAsync(Guid userId, CancellationToken ct = default);
    Task<AdminUserDto> SetRolesAsync(Guid userId, SetUserRolesRequest request, CancellationToken ct = default);
    Task<AdminUserDto> SetActiveAsync(Guid userId, SetUserActiveRequest request, CancellationToken ct = default);
}
