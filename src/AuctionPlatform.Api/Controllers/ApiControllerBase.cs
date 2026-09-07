using System.Security.Claims;
using AuctionPlatform.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace AuctionPlatform.Api.Controllers;

/// <summary>
/// Shared base for API controllers. Controllers stay thin: validate, authorise, delegate, project.
/// No business rule lives here.
/// </summary>
[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>The authenticated caller, taken from the token rather than from the request.</summary>
    protected Guid CurrentUserId
    {
        get
        {
            var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id)
                ? id
                : throw new DomainException(ErrorCodes.Unauthorized, "Authentication is required.");
        }
    }

    protected Guid? CurrentUserIdOrNull =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
