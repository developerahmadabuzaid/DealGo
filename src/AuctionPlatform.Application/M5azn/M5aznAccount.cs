namespace AuctionPlatform.Application.M5azn;

/// <summary>Where the M5azn settings in use came from.</summary>
public enum M5aznSettingsSource
{
    /// <summary>Nothing saved on the dashboard yet: the server's M5azn:* configuration.</summary>
    Server,

    /// <summary>Saved by an administrator on the dashboard's M5azn page.</summary>
    Dashboard
}

/// <summary>
/// The M5azn settings in effect right now: the dashboard's when saved there, else the server's.
/// Holds the seller password in plain text, so it is never logged, serialised or returned.
/// </summary>
public sealed record M5aznAccount(
    bool Enabled,
    string BaseUrl,
    string Email,
    string Password,
    bool AutoSubmit,
    decimal ProfitPercentage,
    M5aznSettingsSource Source,
    bool PasswordUnreadable = false,
    string DeliveryTime = AuctionPlatform.Domain.Entities.M5aznSettings.DefaultDeliveryTime)
{
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);

    public bool IsActive => Enabled && HasCredentials && Uri.TryCreate(BaseUrl, UriKind.Absolute, out _);

    /// <summary>Changes when anything the seller session depends on changes.</summary>
    public string SessionKey => $"{BaseUrl.TrimEnd('/').ToLowerInvariant()}|{Email.Trim().ToLowerInvariant()}|{Password.GetHashCode()}";

    // The password stays out of logs and debugger views.
    public override string ToString() =>
        $"M5aznAccount {{ Enabled = {Enabled}, BaseUrl = {BaseUrl}, Email = {Email}, AutoSubmit = {AutoSubmit}, ProfitPercentage = {ProfitPercentage}, Source = {Source} }}";
}

/// <summary>
/// The current <see cref="M5aznAccount"/>, kept in memory so every M5azn call can read it at once,
/// and re-read from the database at most every few seconds, so a change saved on one server is
/// picked up by the others.
/// </summary>
public interface IM5aznAccountProvider
{
    M5aznAccount Current { get; }

    /// <summary>Re-reads the saved settings. Without <paramref name="force"/>, a recent read is reused.</summary>
    Task<M5aznAccount> RefreshAsync(bool force = false, CancellationToken ct = default);
}

/// <summary>Turns M5azn's price into the DealGo price.</summary>
public static class M5aznPricing
{
    public const decimal MaxProfitPercentage = 1000m;

    /// <summary>M5azn's price plus the profit percentage, rounded to the halala.</summary>
    public static decimal PriceFor(decimal m5aznPrice, decimal profitPercentage) =>
        Math.Round(m5aznPrice * (1m + profitPercentage / 100m), 2, MidpointRounding.AwayFromZero);
}
