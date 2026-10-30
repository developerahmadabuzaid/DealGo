using AuctionPlatform.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuctionPlatform.Api.Controllers;

[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController : ApiControllerBase
{
    private readonly IAuthService _auth;
    private readonly IWebHostEnvironment _environment;

    public AuthController(IAuthService auth, IWebHostEnvironment environment)
    {
        _auth = auth;
        _environment = environment;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResultDto>> Register(RegisterRequest request, CancellationToken ct) =>
        Ok(await _auth.RegisterAsync(request, ct));

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResultDto>> Login(LoginRequest request, CancellationToken ct) =>
        Ok(await _auth.LoginAsync(request, ct));

    /// <summary>Exchanges a refresh token for a new pair. The presented token is consumed.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResultDto>> Refresh(RefreshRequest request, CancellationToken ct) =>
        Ok(await _auth.RefreshAsync(request, ct));

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct)
    {
        await _auth.LogoutAsync(CurrentUserId, request, ct);
        return NoContent();
    }

    /// <summary>
    /// Requests a sign-in code by email or phone.
    ///
    /// Always succeeds, whether or not the destination belongs to an account, so the endpoint
    /// cannot be used to discover who is registered.
    /// </summary>
    [HttpPost("otp/request")]
    [AllowAnonymous]
    public async Task<ActionResult<OtpChallengeDto>> RequestLoginOtp(
        RequestLoginOtpRequest request, CancellationToken ct)
    {
        var challenge = await _auth.RequestLoginOtpAsync(request, ct);

        // The code is echoed outside Production only, so the flow is testable without a real
        // SMS or email provider.
        return Ok(_environment.IsProduction()
            ? challenge with { DevelopmentCode = null }
            : challenge);
    }

    /// <summary>Exchanges a sign-in code for a session.</summary>
    [HttpPost("otp/verify")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResultDto>> VerifyLoginOtp(
        VerifyLoginOtpRequest request, CancellationToken ct) =>
        Ok(await _auth.VerifyLoginOtpAsync(request, ct));

    [HttpPost("verify-otp")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyOtp(VerifyOtpRequest request, CancellationToken ct)
    {
        await _auth.VerifyOtpAsync(request, ct);
        return NoContent();
    }

    /// <summary>
    /// Always returns 200 so the endpoint cannot be used to discover which emails are registered.
    /// The reset token is echoed outside Production only, to keep the flow testable.
    /// </summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        var token = await _auth.ForgotPasswordAsync(request, ct);

        return _environment.IsProduction()
            ? Ok(new { message = "If the account exists, a reset link has been sent." })
            : Ok(new { message = "Reset token generated.", resetToken = token });
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        await _auth.ResetPasswordAsync(request, ct);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        await _auth.ChangePasswordAsync(CurrentUserId, request, ct);
        return NoContent();
    }
}

[Route("api/v1/me")]
[Authorize]
public sealed class ProfileController : ApiControllerBase
{
    private readonly IAuthService _auth;
    private readonly IAccountDeletionService _deletion;
    private readonly IWebHostEnvironment _environment;

    public ProfileController(
        IAuthService auth, IAccountDeletionService deletion, IWebHostEnvironment environment)
    {
        _auth = auth;
        _deletion = deletion;
        _environment = environment;
    }

    [HttpGet]
    public async Task<ActionResult<UserProfileDto>> Get(CancellationToken ct) =>
        Ok(await _auth.GetProfileAsync(CurrentUserId, ct));

    [HttpPut]
    public async Task<ActionResult<UserProfileDto>> Update(UpdateProfileRequest request, CancellationToken ct) =>
        Ok(await _auth.UpdateProfileAsync(CurrentUserId, request, ct));

    /// <summary>
    /// Sends the code that confirms deleting the account. Refused while the account still has a
    /// withdrawable balance or an auction, device, order or payment in progress.
    /// </summary>
    [HttpPost("delete/request-otp")]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult<OtpChallengeDto>> RequestDeletionOtp(CancellationToken ct)
    {
        var challenge = await _deletion.RequestAsync(CurrentUserId, ct);

        return Ok(_environment.IsProduction()
            ? challenge with { DevelopmentCode = null }
            : challenge);
    }

    /// <summary>Deletes the account once the code is confirmed. Every session ends.</summary>
    [HttpPost("delete")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Delete(DeleteAccountRequest request, CancellationToken ct)
    {
        await _deletion.DeleteAsync(CurrentUserId, request, ct);
        return NoContent();
    }
}
