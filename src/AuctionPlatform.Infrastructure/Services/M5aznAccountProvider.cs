using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.M5azn;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Infrastructure.Services;

/// <summary>
/// One per process. Starts from the server's configuration and, once the database has been read,
/// follows the dashboard's saved settings when there are any.
/// </summary>
public sealed class M5aznAccountProvider : IM5aznAccountProvider
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ISecretProtector _protector;
    private readonly M5aznOptions _options;
    private readonly ILogger<M5aznAccountProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile M5aznAccount _current;
    private DateTime _readAt = DateTime.MinValue;

    public M5aznAccountProvider(
        IServiceScopeFactory scopes,
        ISecretProtector protector,
        IOptions<M5aznOptions> options,
        ILogger<M5aznAccountProvider> logger)
    {
        _scopes = scopes;
        _protector = protector;
        _options = options.Value;
        _logger = logger;
        _current = Resolve(null, _options, _protector);
    }

    public M5aznAccount Current => _current;

    public async Task<M5aznAccount> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && DateTime.UtcNow - _readAt < MaxAge) return _current;

        await _gate.WaitAsync(ct);
        try
        {
            if (!force && DateTime.UtcNow - _readAt < MaxAge) return _current;

            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.M5aznSettings.AsNoTracking().OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);

            var next = Resolve(row, _options, _protector);
            if (next.PasswordUnreadable)
            {
                _logger.LogError("M5azn: the seller password saved on the dashboard cannot be read (the server key changed?). Enter it again on the M5azn page.");
            }

            _current = next;
            _readAt = DateTime.UtcNow;
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The dashboard's row wins where it says something; the server's configuration fills the rest.</summary>
    public static M5aznAccount Resolve(M5aznSettings? row, M5aznOptions options, ISecretProtector protector)
    {
        if (row is null)
        {
            return new M5aznAccount(options.Enabled, options.BaseUrl, options.Email.Trim(), options.Password,
                options.AutoSubmit, M5aznSettings.DefaultProfitPercentage, M5aznSettingsSource.Server);
        }

        var email = options.Email.Trim();
        var password = options.Password;
        var unreadable = false;

        if (!string.IsNullOrWhiteSpace(row.Email))
        {
            email = row.Email.Trim();
            password = string.Empty;
            if (!string.IsNullOrEmpty(row.PasswordProtected))
            {
                password = protector.Unprotect(row.PasswordProtected) ?? string.Empty;
                unreadable = password.Length == 0;
            }
        }

        return new M5aznAccount(
            row.Enabled,
            string.IsNullOrWhiteSpace(row.BaseUrl) ? options.BaseUrl : row.BaseUrl.Trim(),
            email,
            password,
            row.AutoSubmit,
            row.ProfitPercentage,
            M5aznSettingsSource.Dashboard,
            unreadable,
            string.IsNullOrWhiteSpace(row.DeliveryTime) ? M5aznSettings.DefaultDeliveryTime : row.DeliveryTime.Trim());
    }
}
