using AuctionPlatform.Application.Common;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Domain.Rules;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuctionPlatform.Infrastructure.Persistence;

/// <summary>
/// Applies migrations and seeds reference data.
///
/// Roles and store settings are seeded in every environment because the application cannot work
/// without them. Demo users, products and auctions are seeded only when explicitly requested,
/// so production never receives development credentials.
/// </summary>
public sealed class DatabaseSeeder
{
    private const string DemoPassword = "Passw0rd!";

    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly RoleManager<ApplicationRole> _roles;
    private readonly INumberGenerator _numbers;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        ApplicationDbContext db,
        UserManager<ApplicationUser> users,
        RoleManager<ApplicationRole> roles,
        INumberGenerator numbers,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _users = users;
        _roles = roles;
        _numbers = numbers;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await _db.Database.MigrateAsync(ct);
        _logger.LogInformation("Database migrations applied.");
    }

    /// <summary>Roles and the single store-settings row. Safe and required everywhere.</summary>
    public async Task SeedEssentialsAsync(CancellationToken ct = default)
    {
        foreach (var role in Roles.All)
        {
            if (!await _roles.RoleExistsAsync(role))
            {
                await _roles.CreateAsync(new ApplicationRole(role));
            }
        }

        if (!await _db.StoreSettings.AnyAsync(ct))
        {
            _db.StoreSettings.Add(new StoreSettings());
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Essential seed data verified.");
    }

    /// <summary>
    /// Demo data for development and testing. Never call this in production: the accounts use a
    /// well-known password.
    /// </summary>
    public async Task SeedDevelopmentDataAsync(CancellationToken ct = default)
    {
        if (await _db.Auctions.AnyAsync(ct))
        {
            _logger.LogInformation("Development data already present; skipping.");
            return;
        }

        var superAdmin = await EnsureUserAsync("superadmin@auctions.local", "Super Admin", Roles.SuperAdmin);
        await EnsureUserAsync("admin@auctions.local", "Platform Admin", Roles.Admin);
        await EnsureUserAsync("store@auctions.local", "Store Manager", Roles.StoreManager);
        await EnsureUserAsync("finance@auctions.local", "Finance Officer", Roles.Finance);

        var bidders = new List<ApplicationUser>();
        for (var i = 1; i <= 12; i++)
        {
            var user = await EnsureUserAsync($"user{i}@auctions.local", $"Test User {i}", Roles.User);
            bidders.Add(user);

            await CreditWalletAsync(user.Id, WalletType.Main, 500m, ct);
        }

        await SeedCatalogProductsAsync(ct);
        await SeedStoreAsync(ct);
        await SeedAuctionsAsync(superAdmin.Id, ct);

        _logger.LogInformation(
            "Development data seeded. Demo accounts use the password {Password}.", DemoPassword);
    }

    private async Task<ApplicationUser> EnsureUserAsync(string email, string fullName, string role)
    {
        var user = await _users.FindByEmailAsync(email);

        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = fullName,
                Language = "ar",
                IsActive = true
            };

            var result = await _users.CreateAsync(user, DemoPassword);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not seed {email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
            }
        }

        if (!await _users.IsInRoleAsync(user, role))
        {
            await _users.AddToRoleAsync(user, role);
        }

        // Everyone gets both wallets.
        foreach (var type in new[] { WalletType.Main, WalletType.Shopping })
        {
            if (!await _db.Wallets.AnyAsync(w => w.UserId == user.Id && w.WalletType == type))
            {
                _db.Wallets.Add(new Wallet
                {
                    UserId = user.Id,
                    WalletType = type,
                    Balance = 0m,
                    Currency = Money.DefaultCurrency
                });
            }
        }

        await _db.SaveChangesAsync();
        return user;
    }

    private async Task CreditWalletAsync(Guid userId, WalletType type, decimal amount, CancellationToken ct)
    {
        var wallet = await _db.Wallets.FirstAsync(w => w.UserId == userId && w.WalletType == type, ct);
        if (wallet.Balance >= amount) return;

        var before = wallet.Balance;
        wallet.Balance = amount;
        wallet.UpdatedAt = DateTime.UtcNow;

        _db.WalletTransactions.Add(new WalletTransaction
        {
            WalletId = wallet.Id,
            UserId = userId,
            WalletType = type,
            TransactionType = WalletTransactionType.Deposit,
            Amount = amount - before,
            Currency = wallet.Currency,
            BalanceBefore = before,
            BalanceAfter = amount,
            Description = "Development seed deposit",
            ReferenceType = "Seed"
        });

        await _db.SaveChangesAsync(ct);
    }

    private async Task SeedCatalogProductsAsync(CancellationToken ct)
    {
        if (await _db.Products.AnyAsync(ct)) return;

        var products = new[]
        {
            new Product { Name = "Luxury SUV", Price = 5000m, PrimaryImageUrl = "https://picsum.photos/seed/suv/800/600" },
            new Product { Name = "Premium Laptop", Price = 1800m, PrimaryImageUrl = "https://picsum.photos/seed/laptop/800/600" },
            new Product { Name = "Smartphone Pro", Price = 1200m, PrimaryImageUrl = "https://picsum.photos/seed/phone/800/600" },
            new Product { Name = "Smart Watch", Price = 600m, PrimaryImageUrl = "https://picsum.photos/seed/watch/800/600" },
            new Product { Name = "Home Theatre", Price = 2400m, PrimaryImageUrl = "https://picsum.photos/seed/theatre/800/600" },
            new Product { Name = "Gaming Console", Price = 900m, PrimaryImageUrl = "https://picsum.photos/seed/console/800/600" },
            new Product { Name = "Electric Scooter", Price = 750m, PrimaryImageUrl = "https://picsum.photos/seed/scooter/800/600" },
            new Product { Name = "Espresso Machine", Price = 450m, PrimaryImageUrl = "https://picsum.photos/seed/espresso/800/600" }
        };

        _db.Products.AddRange(products);
        await _db.SaveChangesAsync(ct);
    }

    private async Task SeedStoreAsync(CancellationToken ct)
    {
        if (await _db.StoreCategories.AnyAsync(ct)) return;

        var categories = new[]
        {
            new StoreCategory { Name = "Electronics", Slug = "electronics", DisplayOrder = 1 },
            new StoreCategory { Name = "Accessories", Slug = "accessories", DisplayOrder = 2 },
            new StoreCategory { Name = "Home", Slug = "home", DisplayOrder = 3 }
        };
        _db.StoreCategories.AddRange(categories);
        await _db.SaveChangesAsync(ct);

        var items = new (string Name, string Sku, decimal Price, int Stock, int Category, bool Featured)[]
        {
            ("Headphones Pro", "SKU-HEADPHONES-001", 35m, 40, 0, true),
            ("Wireless Earbuds", "SKU-EARBUDS-001", 25m, 60, 0, true),
            ("Bluetooth Speaker", "SKU-SPEAKER-001", 45m, 25, 0, false),
            ("Phone Case", "SKU-CASE-001", 12m, 200, 1, false),
            ("Fast Charger", "SKU-CHARGER-001", 18m, 120, 1, true),
            ("Laptop Sleeve", "SKU-SLEEVE-001", 22m, 80, 1, false),
            ("Desk Lamp", "SKU-LAMP-001", 30m, 45, 2, false),
            ("Coffee Grinder", "SKU-GRINDER-001", 55m, 18, 2, true),
            ("Kitchen Scale", "SKU-SCALE-001", 20m, 3, 2, false)
        };

        foreach (var item in items)
        {
            var product = new StoreProduct
            {
                Name = item.Name,
                Slug = item.Name.ToLowerInvariant().Replace(' ', '-'),
                Description = $"{item.Name} available for purchase with shopping credit.",
                CategoryId = categories[item.Category].Id,
                Sku = item.Sku,
                Price = item.Price,
                Currency = Money.DefaultCurrency,
                StockQuantity = item.Stock,
                Status = item.Stock > 0 ? StoreProductStatus.Active : StoreProductStatus.OutOfStock,
                PaymentMode = StorePaymentMode.Both,
                IsFeatured = item.Featured,
                RequiresShipping = true,
                ShippingFee = 0m
            };

            _db.StoreProducts.Add(product);

            _db.StoreProductImages.Add(new StoreProductImage
            {
                ProductId = product.Id,
                Url = $"https://picsum.photos/seed/{product.Slug}/600/600",
                IsPrimary = true,
                DisplayOrder = 0
            });

            _db.InventoryTransactions.Add(new InventoryTransaction
            {
                ProductId = product.Id,
                Type = InventoryTransactionType.Initial,
                Quantity = item.Stock,
                BeforeQuantity = 0,
                AfterQuantity = item.Stock,
                Reason = "Initial seed stock"
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task SeedAuctionsAsync(Guid adminId, CancellationToken ct)
    {
        var products = await _db.Products.OrderBy(p => p.Price).ToListAsync(ct);
        if (products.Count < 4) return;

        var main = products.OrderByDescending(p => p.Price).First();
        var prizes = products.Where(p => p.Id != main.Id).OrderByDescending(p => p.Price).Take(3).ToList();

        // A small auction so the whole lifecycle can be exercised without 400 test users.
        var auction = BuildAuction(
            await _numbers.NextAuctionNumberAsync(ct), "Luxury SUV Grand Auction", main, prizes,
            requiredParticipants: 4, startDelaySeconds: 0);

        auction.Status = AuctionStatus.RegistrationOpen;

        _db.Auctions.Add(auction);
        AddPrizes(auction, main, prizes);

        var second = BuildAuction(
            await _numbers.NextAuctionNumberAsync(ct), "Premium Electronics Bundle",
            products.First(p => p.Name == "Home Theatre"),
            products.Where(p => p.Name is "Gaming Console" or "Smart Watch" or "Espresso Machine").ToList(),
            requiredParticipants: 6, startDelaySeconds: 3600);

        second.Status = AuctionStatus.RegistrationOpen;

        _db.Auctions.Add(second);
        AddPrizes(second,
            products.First(p => p.Name == "Home Theatre"),
            products.Where(p => p.Name is "Gaming Console" or "Smart Watch" or "Espresso Machine").ToList());

        _db.AuctionEvents.Add(new AuctionEvent
        {
            AuctionId = auction.Id,
            UserId = adminId,
            EventType = AuctionEventType.AuctionPublished,
            Timestamp = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }

    private static Domain.Entities.Auction BuildAuction(
        string number, string title, Product main, List<Product> prizes,
        int requiredParticipants, int startDelaySeconds)
    {
        var auction = new Domain.Entities.Auction
        {
            AuctionNumber = number,
            Title = title,
            Description = $"Win {main.Name} and three more prizes.",
            MainProductId = main.Id,
            MainProductNameSnapshot = main.Name,
            OriginalPrice = main.Price,
            StartingPrice = Money.Round(main.Price * 0.2m),
            TargetPercentage = AuctionRules.DefaultTargetPercentage,
            FinalPhasePercentage = AuctionRules.DefaultFinalPhasePercentage,
            BidIncrement = AuctionRules.DefaultBidIncrement,
            EntryFee = AuctionRules.DefaultEntryFee,
            Currency = Money.DefaultCurrency,
            RequiredParticipants = requiredParticipants,
            StartDelayAfterParticipantsSeconds = startDelaySeconds,
            DurationSeconds = AuctionRules.DefaultDurationSeconds,
            ExpirationPolicy = ExpirationPolicy.EndAndDistribute
        };

        auction.TargetPrice = AuctionRules.CalculateTargetPrice(auction.OriginalPrice, auction.TargetPercentage);
        auction.FinalPhasePrice = AuctionRules.CalculateFinalPhasePrice(auction.OriginalPrice, auction.FinalPhasePercentage);
        auction.CurrentPrice = auction.StartingPrice;

        return auction;
    }

    private void AddPrizes(Domain.Entities.Auction auction, Product main, List<Product> others)
    {
        _db.AuctionPrizes.Add(new AuctionPrize
        {
            AuctionId = auction.Id,
            ProductId = main.Id,
            Rank = 1,
            OriginalPrice = main.Price,
            ProductNameSnapshot = main.Name,
            ProductImageSnapshot = main.PrimaryImageUrl
        });

        for (var i = 0; i < Math.Min(3, others.Count); i++)
        {
            _db.AuctionPrizes.Add(new AuctionPrize
            {
                AuctionId = auction.Id,
                ProductId = others[i].Id,
                Rank = i + 2,
                OriginalPrice = others[i].Price,
                ProductNameSnapshot = others[i].Name,
                ProductImageSnapshot = others[i].PrimaryImageUrl
            });
        }
    }
}
