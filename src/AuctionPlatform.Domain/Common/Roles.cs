namespace AuctionPlatform.Domain.Common;

public static class Roles
{
    public const string User = "USER";
    public const string Admin = "ADMIN";
    public const string SuperAdmin = "SUPER_ADMIN";
    public const string AuctionManager = "AUCTION_MANAGER";
    public const string Finance = "FINANCE";
    public const string Support = "SUPPORT";
    public const string StoreManager = "STORE_MANAGER";

    public static readonly string[] All =
    {
        User, Admin, SuperAdmin, AuctionManager, Finance, Support, StoreManager
    };

    public static readonly string[] AdminRoles =
    {
        Admin, SuperAdmin, AuctionManager, Finance, Support, StoreManager
    };
}

/// <summary>
/// Fine-grained permissions issued as claims. Authorisation policies are checked on the server;
/// hiding UI elements is never treated as access control.
/// </summary>
public static class Permissions
{
    public const string ClaimType = "permission";

    public const string AuctionRead = "Auction.Read";
    public const string AuctionCreate = "Auction.Create";
    public const string AuctionUpdate = "Auction.Update";
    public const string AuctionStart = "Auction.Start";
    public const string AuctionCancel = "Auction.Cancel";

    public const string StoreRead = "Store.Read";
    public const string StoreCreateProduct = "Store.CreateProduct";
    public const string StoreUpdateProduct = "Store.UpdateProduct";
    public const string StoreDeleteProduct = "Store.DeleteProduct";
    public const string StoreManageInventory = "Store.ManageInventory";
    public const string StoreManageOrders = "Store.ManageOrders";
    public const string StoreManageSettings = "Store.ManageSettings";

    public const string WalletRead = "Wallet.Read";
    public const string WalletAdjust = "Wallet.Adjust";
    public const string WalletRefund = "Wallet.Refund";

    public const string UserRead = "User.Read";
    public const string UserManage = "User.Manage";

    public const string ReportsRead = "Reports.Read";
    public const string AuditRead = "Audit.Read";

    /// <summary>Editing the public landing page. Separate because it is publishing, not operations.</summary>
    public const string SiteManage = "Site.Manage";

    public static readonly string[] All =
    {
        AuctionRead, AuctionCreate, AuctionUpdate, AuctionStart, AuctionCancel,
        StoreRead, StoreCreateProduct, StoreUpdateProduct, StoreDeleteProduct,
        StoreManageInventory, StoreManageOrders, StoreManageSettings,
        WalletRead, WalletAdjust, WalletRefund,
        UserRead, UserManage, ReportsRead, AuditRead, SiteManage
    };

    /// <summary>Default permission grant per role. SUPER_ADMIN implicitly holds everything.</summary>
    public static IReadOnlyDictionary<string, string[]> ByRole { get; } =
        new Dictionary<string, string[]>
        {
            [Roles.SuperAdmin] = All,
            [Roles.Admin] = All,
            [Roles.AuctionManager] = new[]
            {
                AuctionRead, AuctionCreate, AuctionUpdate, AuctionStart, AuctionCancel,
                UserRead, ReportsRead
            },
            [Roles.StoreManager] = new[]
            {
                StoreRead, StoreCreateProduct, StoreUpdateProduct, StoreDeleteProduct,
                StoreManageInventory, StoreManageOrders, ReportsRead,
                // The store manager already writes the product copy customers read.
                SiteManage
            },
            [Roles.Finance] = new[]
            {
                WalletRead, WalletAdjust, WalletRefund, ReportsRead, AuditRead,
                StoreRead, StoreManageOrders, AuctionRead
            },
            [Roles.Support] = new[]
            {
                UserRead, AuctionRead, StoreRead, WalletRead
            },
            [Roles.User] = Array.Empty<string>()
        };
}
