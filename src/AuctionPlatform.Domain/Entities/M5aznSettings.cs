using AuctionPlatform.Domain.Common;

namespace AuctionPlatform.Domain.Entities;

/// <summary>
/// The M5azn settings an administrator changes from the dashboard (docs/M5AZN.md). One row at
/// most. Until it exists, the server's configuration (M5azn:Enabled, M5azn:Email, ...) is used;
/// once saved, this row wins.
/// </summary>
public class M5aznSettings : AuditableEntity
{
    /// <summary>On: paid store orders with M5azn products are sent, followed and stock-synced.</summary>
    public bool Enabled { get; set; }

    /// <summary>The M5azn seller account's email. Null: the server's configured account is used.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// The seller account's password, encrypted (never stored or shown as typed). Readable only by
    /// the server with the same key it was written with.
    /// </summary>
    public string? PasswordProtected { get; set; }

    /// <summary>Null: the server's M5azn:BaseUrl (https://m5azn.com by default).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Send paid orders without waiting for an administrator's approval.</summary>
    public bool AutoSubmit { get; set; } = true;

    /// <summary>
    /// The profit added to M5azn's price to make the DealGo price, in percent: with 70, a product
    /// M5azn sells for 100 SAR is priced 170 SAR.
    /// </summary>
    public decimal ProfitPercentage { get; set; } = DefaultProfitPercentage;

    public const decimal DefaultProfitPercentage = 70m;
}
