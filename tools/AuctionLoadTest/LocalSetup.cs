using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AuctionLoadTest;

sealed record TestUser(Guid Id, string Email, string Name, string Token);

/// <summary>
/// Local runs only. Creates the test bidders straight in the database and signs their tokens with
/// the local server's own key, so 400 people can take part without 400 text messages and without
/// the sign-up limit of 20 a minute. Everything after that — joining, bidding, the live connection —
/// goes through the real API like the app does.
/// </summary>
sealed class LocalSetup
{
    private const string EmailDomain = "loadtest.local";

    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly string _secret;
    private readonly string _issuer;
    private readonly string _audience;

    public LocalSetup(string connectionString, string secret, string issuer, string audience)
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, o => o.CommandTimeout(120))
            .Options;
        _secret = secret;
        _issuer = issuer;
        _audience = audience;
    }

    public ApplicationDbContext Db() => new(_options);

    public async Task<TestUser> EnsureAdminAsync()
    {
        var admin = (await EnsureAsync(new[] { $"loadtest-admin@{EmailDomain}" }, _ => "Load Test Admin"))[0];
        return admin with { Token = Mint(admin.Id, admin.Email, admin.Name, Roles.SuperAdmin) };
    }

    public async Task<List<TestUser>> EnsureBiddersAsync(int count)
    {
        var emails = Enumerable.Range(1, count).Select(i => $"loadtest-{i:0000}@{EmailDomain}").ToList();
        var users = await EnsureAsync(emails, email => $"Bidder {email[9..13]}");
        return users.Select(u => u with { Token = Mint(u.Id, u.Email, u.Name, Roles.User) }).ToList();
    }

    /// <summary>Bidders with ordinary Arabic names, for `play`, where a person watches them on screen.</summary>
    public async Task<List<TestUser>> EnsurePlayBotsAsync(int count)
    {
        string[] first = ["محمد", "عبدالله", "فهد", "سارة", "نورة", "خالد", "ريم", "سلطان", "هند", "تركي",
            "منيرة", "ناصر", "لمى", "بندر", "غادة", "ماجد", "جود", "عبدالعزيز", "شهد", "يوسف"];
        string[] last = ["العتيبي", "القحطاني", "الشمري", "الدوسري", "الحربي", "المطيري", "الغامدي", "الزهراني", "السبيعي", "العنزي"];

        var emails = Enumerable.Range(1, count).Select(i => $"play-bot-{i:000}@{EmailDomain}").ToList();
        var users = await EnsureAsync(emails, email =>
        {
            var i = int.Parse(email[9..12]) - 1;
            return $"{first[i % first.Length]} {last[(i / first.Length + i) % last.Length]}";
        });
        return users.Select(u => u with { Token = Mint(u.Id, u.Email, u.Name, Roles.User) }).ToList();
    }

    private async Task<List<TestUser>> EnsureAsync(IReadOnlyList<string> emails, Func<string, string> name)
    {
        await using var db = Db();
        var normalized = emails.Select(e => e.ToUpperInvariant()).ToList();
        var existing = await db.Users.Where(u => normalized.Contains(u.NormalizedEmail!))
            .ToDictionaryAsync(u => u.NormalizedEmail!, u => u);

        foreach (var email in emails)
        {
            if (existing.ContainsKey(email.ToUpperInvariant())) continue;

            var user = new ApplicationUser
            {
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = true,
                FullName = name(email),
                Language = "ar",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                IsActive = true
            };
            db.Users.Add(user);
            db.Wallets.Add(new Wallet { UserId = user.Id, WalletType = WalletType.Main, Currency = Money.DefaultCurrency });
            db.Wallets.Add(new Wallet { UserId = user.Id, WalletType = WalletType.Shopping, Currency = Money.DefaultCurrency });
            existing[user.NormalizedEmail] = user;
        }

        await db.SaveChangesAsync();
        return emails.Select(e => existing[e.ToUpperInvariant()])
            .Select(u => new TestUser(u.Id, u.Email!, u.FullName, string.Empty))
            .ToList();
    }

    /// <summary>Main wallet set to exactly one entry fee and the shopping wallet emptied, so every
    /// refund and every charge of the run can be checked against a known starting point.</summary>
    public async Task ResetWalletsAsync(IReadOnlyCollection<Guid> userIds, decimal main)
    {
        await using var db = Db();
        await db.Wallets.Where(w => userIds.Contains(w.UserId) && w.WalletType == WalletType.Main)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, main));
        await db.Wallets.Where(w => userIds.Contains(w.UserId) && w.WalletType == WalletType.Shopping)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, 0m));
    }

    /// <summary>The same claims the API's own sign-in puts in a token (AuthService.BuildAccessToken).</summary>
    private string Mint(Guid id, string email, string name, string role)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(ClaimTypes.NameIdentifier, id.ToString()),
            new("name", name),
            new("lang", "ar"),
            new(ClaimTypes.Role, role)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret)), SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(_issuer, _audience, claims, now.AddMinutes(-1), now.AddHours(8), credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
