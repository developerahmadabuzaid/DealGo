using System.Security.Claims;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;

namespace AuctionPlatform.Api.Services;

/// <summary>
/// Reads the caller's identity from the validated JWT. Nothing here comes from the request body,
/// so a client cannot claim to be someone else by sending a user id.
/// </summary>
public sealed class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _accessor;

    public CurrentUserService(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email);

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList() ?? new List<string>();

    public IReadOnlyCollection<string> PermissionClaims =>
        Principal?.FindAll(Permissions.ClaimType).Select(c => c.Value).ToList() ?? new List<string>();

    public string? IpAddress => _accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => _accessor.HttpContext?.Request.Headers.UserAgent.ToString();

    /// <summary>SUPER_ADMIN implicitly holds every permission; everyone else needs the explicit claim.</summary>
    public bool HasPermission(string permission) =>
        Roles.Contains(Domain.Common.Roles.SuperAdmin) || PermissionClaims.Contains(permission);
}
