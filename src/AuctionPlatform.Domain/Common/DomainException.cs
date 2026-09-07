namespace AuctionPlatform.Domain.Common;

/// <summary>
/// Raised when a business rule is violated. Carries the stable machine-readable error code
/// that the API surfaces to clients (see ErrorCodes in the Application layer).
/// </summary>
public class DomainException : Exception
{
    public string Code { get; }
    public IReadOnlyList<string> Details { get; }

    public DomainException(string code, string message, IReadOnlyList<string>? details = null)
        : base(message)
    {
        Code = code;
        Details = details ?? Array.Empty<string>();
    }
}
