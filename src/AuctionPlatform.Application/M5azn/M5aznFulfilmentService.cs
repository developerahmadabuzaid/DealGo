using System.Text.Json;
using AuctionPlatform.Application.Common;
using AuctionPlatform.Application.Store;
using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Entities;
using AuctionPlatform.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuctionPlatform.Application.M5azn;

// ---------------------------------------------------------------- DTOs

public sealed record M5aznShipmentDto(
    Guid Id,
    long Reference,
    M5aznSourceType SourceType,
    Guid SourceId,
    string? SourceNumber,
    Guid UserId,
    string? CustomerName,
    M5aznShipmentStatus Status,
    string? RemoteOrderId,
    string? RemoteStatus,
    string? TrackingNumber,
    string? TrackingUrl,
    string? Carrier,
    string? LastError,
    int Attempts,
    DateTime? NextAttemptAt,
    DateTime CreatedAt,
    DateTime? SubmittedAt,
    DateTime? ShippedAt,
    DateTime? DeliveredAt,
    DateTime? LastSyncedAt);

/// <summary>What a customer sees about delivery by M5azn.</summary>
public sealed record ShipmentTrackingDto(
    string Provider,
    M5aznShipmentStatus Status,
    string? RemoteStatus,
    string? TrackingNumber,
    string? TrackingUrl,
    string? Carrier,
    DateTime CreatedAt,
    DateTime? SubmittedAt,
    DateTime? ShippedAt,
    DateTime? DeliveredAt,
    DateTime? LastSyncedAt);

public sealed record M5aznOverviewDto(
    bool Enabled,
    bool Configured,
    bool AutoSubmit,
    bool StockSyncEnabled,
    string BaseUrl,
    string PaymentMethod,
    IReadOnlyDictionary<string, int> CountsByStatus,
    int LinkedStoreProducts);

public sealed class M5aznShipmentQuery : PagedQuery
{
    public M5aznShipmentStatus? Status { get; set; }
}

/// <summary>Both null removes the link. A link needs both: M5azn's documented order line carries the two.</summary>
public sealed record M5aznLinkRequest(long? M5aznProductId, string? M5aznSku);

public sealed record M5aznLinkedItemDto(
    Guid Id,
    string Name,
    string? Sku,
    long? M5aznProductId,
    string? M5aznSku,
    int? Stock);

public sealed record M5aznLinksDto(IReadOnlyList<M5aznLinkedItemDto> StoreProducts);

public sealed record M5aznRunSummary(int Found, int Sent, int Refreshed);

// ---------------------------------------------------------------- service

public interface IM5aznFulfilmentService
{
    bool IsEnabled { get; }

    /// <summary>One pass of the worker: pick up new paid work, send what is due, refresh what is on its way.</summary>
    Task<M5aznRunSummary> RunAsync(CancellationToken ct = default);

    /// <summary>Copies M5azn's recent stock changes onto linked store products. Returns how many changed.</summary>
    Task<int> SyncStockAsync(CancellationToken ct = default);

    Task<M5aznOverviewDto> GetOverviewAsync(CancellationToken ct = default);
    Task<PagedResult<M5aznShipmentDto>> GetShipmentsAsync(M5aznShipmentQuery query, CancellationToken ct = default);

    /// <summary>Approves a waiting shipment or sends a failed one again, now.</summary>
    Task<M5aznShipmentDto> SubmitNowAsync(Guid shipmentId, CancellationToken ct = default);

    Task<M5aznShipmentDto> RefreshAsync(Guid shipmentId, CancellationToken ct = default);

    Task<M5aznLinksDto> GetLinksAsync(CancellationToken ct = default);
    Task LinkStoreProductAsync(Guid productId, M5aznLinkRequest request, CancellationToken ct = default);
    Task<M5aznProductInfo?> LookupProductAsync(long m5aznProductId, CancellationToken ct = default);

    Task<ShipmentTrackingDto?> GetStoreOrderTrackingAsync(Guid userId, Guid orderId, CancellationToken ct = default);

    /// <summary>For staff screens: the shipment of any store order, whoever owns it.</summary>
    Task<M5aznShipmentDto?> GetForStoreOrderAsync(Guid orderId, CancellationToken ct = default);
}

/// <summary>
/// Sends paid store orders whose products come from M5azn to M5azn, follows them until
/// delivered, and keeps linked products' stock in step with M5azn's. Store only: auctions and
/// their prizes are never sent to M5azn.
///
/// Nothing here runs inside a checkout or a payment: the worker picks paid work up afterwards, so
/// a slow or unreachable M5azn never holds up a customer, and a crash in between loses nothing.
///
/// Money is at stake on every send (M5azn charges the seller's balance for each order), so a send
/// is only repeated when the first one certainly did not arrive, or when M5azn does not know the
/// order when asked by its reference. An unclear answer that cannot be confirmed is retried a few
/// times and then left as failed for a person, rather than repeated indefinitely.
/// </summary>
public sealed class M5aznFulfilmentService : IM5aznFulfilmentService
{
    private const int BatchSize = 20;

    private readonly IApplicationDbContext _db;
    private readonly IM5aznClient _client;
    private readonly IStoreAdminService _storeAdmin;
    private readonly IAuditService _audit;
    private readonly IDateTimeProvider _clock;
    private readonly M5aznOptions _options;
    private readonly ILogger<M5aznFulfilmentService> _logger;

    public M5aznFulfilmentService(
        IApplicationDbContext db,
        IM5aznClient client,
        IStoreAdminService storeAdmin,
        IAuditService audit,
        IDateTimeProvider clock,
        IOptions<M5aznOptions> options,
        ILogger<M5aznFulfilmentService> logger)
    {
        _db = db;
        _client = client;
        _storeAdmin = storeAdmin;
        _audit = audit;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.IsActive && _client.IsEnabled;

    // ================================================================ worker

    public async Task<M5aznRunSummary> RunAsync(CancellationToken ct = default)
    {
        if (!IsEnabled) return new M5aznRunSummary(0, 0, 0);

        var found = await DiscoverAsync(ct);
        var sent = await SubmitDueAsync(ct);
        var refreshed = await SyncStatusesAsync(ct);

        return new M5aznRunSummary(found, sent, refreshed);
    }

    /// <summary>Opens a shipment for each recently paid store order that has an M5azn product and an address.</summary>
    private async Task<int> DiscoverAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var since = now.AddDays(-Math.Max(1, _options.LookbackDays));
        var initial = _options.AutoSubmit ? M5aznShipmentStatus.Queued : M5aznShipmentStatus.AwaitingApproval;

        var orders = await _db.StoreOrders.AsNoTracking()
            .Where(o => (o.Status == StoreOrderStatus.Paid || o.Status == StoreOrderStatus.Processing)
                        && o.PaidAt != null && o.PaidAt >= since
                        && o.ShippingAddressId != null
                        && o.Items.Any(i => i.Product!.M5aznProductId != null)
                        && !_db.M5aznShipments.Any(s => s.SourceType == M5aznSourceType.StoreOrder && s.SourceId == o.Id))
            .OrderBy(o => o.PaidAt)
            .Select(o => new { o.Id, o.UserId })
            .Take(100)
            .ToListAsync(ct);

        foreach (var o in orders)
        {
            _db.M5aznShipments.Add(NewShipment(M5aznSourceType.StoreOrder, o.Id, o.UserId, initial, now));
        }

        var count = orders.Count;
        if (count == 0) return 0;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Another instance opened the same ones first; UNIQUE(SourceType, SourceId) kept it single.
            _logger.LogInformation(ex, "M5azn: shipments were opened elsewhere at the same time; skipping this pass.");
            DetachAdded();
            return 0;
        }

        _logger.LogInformation("M5azn: {Orders} store order(s) picked up.", count);
        return count;
    }

    private static M5aznShipment NewShipment(
        M5aznSourceType type, Guid sourceId, Guid userId, M5aznShipmentStatus status, DateTime now) => new()
    {
        SourceType = type,
        SourceId = sourceId,
        UserId = userId,
        Status = status,
        CreatedAt = now,
        UpdatedAt = now
    };

    private async Task<int> SubmitDueAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var due = await _db.M5aznShipments.AsNoTracking()
            .Where(s => s.Status == M5aznShipmentStatus.Queued && (s.NextAttemptAt == null || s.NextAttemptAt <= now))
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var id in due)
        {
            if (await SubmitOneAsync(id, ct)) sent++;
        }

        return sent;
    }

    /// <summary>Sends one queued shipment. True when M5azn now has it.</summary>
    private async Task<bool> SubmitOneAsync(Guid shipmentId, CancellationToken ct)
    {
        var shipment = await _db.M5aznShipments.FirstOrDefaultAsync(s => s.Id == shipmentId, ct);
        if (shipment is null || shipment.Status != M5aznShipmentStatus.Queued) return false;

        var now = _clock.UtcNow;
        var built = await BuildOrderAsync(shipment, ct);

        if (built.CancelReason is not null)
        {
            shipment.Status = M5aznShipmentStatus.Cancelled;
            shipment.CancelledAt = now;
            shipment.LastError = built.CancelReason;
            shipment.UpdatedAt = now;
            await SaveIgnoringConflictAsync(ct);
            return false;
        }

        if (built.Problem is not null)
        {
            shipment.Status = M5aznShipmentStatus.Failed;
            shipment.LastError = built.Problem;
            shipment.UpdatedAt = now;
            await SaveIgnoringConflictAsync(ct);
            return false;
        }

        var request = built.Request!;

        // Recorded before the call, so a customer cancelling while it is in flight is refused
        // (StoreOrderService checks Attempts) and a crash mid-call is never forgotten.
        shipment.Attempts++;
        shipment.NextAttemptAt = now + Backoff(shipment.Attempts);
        shipment.RequestJson = JsonSerializer.Serialize(request);
        shipment.UpdatedAt = now;
        if (!await SaveIgnoringConflictAsync(ct)) return false;

        M5aznSubmitResult result;
        try
        {
            result = await _client.CreateOrderAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new M5aznSubmitResult(M5aznSubmitOutcome.Unknown, Message: ex.Message);
        }

        if (result.Outcome == M5aznSubmitOutcome.Unknown)
        {
            // Ask by our own reference before ever sending again.
            var confirmed = await TryGetRemoteAsync(shipment.Reference.ToString(), ct);
            if (confirmed is not null)
            {
                result = new M5aznSubmitResult(M5aznSubmitOutcome.Accepted, confirmed.RemoteOrderId, confirmed.Status);
            }
        }

        now = _clock.UtcNow;
        shipment.UpdatedAt = now;

        switch (result.Outcome)
        {
            case M5aznSubmitOutcome.Accepted:
                shipment.Status = M5aznShipmentStatus.Submitted;
                shipment.SubmittedAt = now;
                shipment.RemoteOrderId = result.RemoteOrderId ?? shipment.RemoteOrderId;
                shipment.RemoteStatus = result.RemoteStatus ?? shipment.RemoteStatus;
                shipment.LastError = null;
                shipment.NextAttemptAt = null;
                ApplyMappedStatus(shipment, M5aznStatusMap.Map(result.RemoteStatus), now);
                break;

            case M5aznSubmitOutcome.Rejected:
                shipment.Status = M5aznShipmentStatus.Failed;
                shipment.NextAttemptAt = null;
                shipment.LastError = $"M5azn refused the order: {result.Message ?? "no reason given"}";
                break;

            default:
                var maybeThere = result.Outcome == M5aznSubmitOutcome.Unknown;
                shipment.LastError = maybeThere
                    ? $"No clear answer from M5azn ({result.Message}). It may have the order; it is asked by reference before any resend."
                    : $"Not sent: {result.Message}";

                if (shipment.Attempts >= Math.Max(1, _options.MaxAttempts))
                {
                    shipment.Status = M5aznShipmentStatus.Failed;
                    shipment.NextAttemptAt = null;
                    if (maybeThere)
                    {
                        shipment.LastError += $" Check M5azn's orders for reference {shipment.Reference} before sending again.";
                    }
                }
                break;
        }

        await _db.SaveChangesAsync(ct);

        if (shipment.Status is M5aznShipmentStatus.Submitted or M5aznShipmentStatus.Processing
            or M5aznShipmentStatus.Shipped or M5aznShipmentStatus.Delivered)
        {
            await AdvanceSourceAsync(shipment, ct);
            return true;
        }

        if (shipment.Status == M5aznShipmentStatus.Failed)
        {
            _logger.LogWarning("M5azn: shipment {Reference} failed: {Error}", shipment.Reference, shipment.LastError);
        }

        return false;
    }

    private static TimeSpan Backoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(5),
        2 => TimeSpan.FromMinutes(15),
        3 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(4)
    };

    private async Task<int> SyncStatusesAsync(CancellationToken ct)
    {
        var cutoff = _clock.UtcNow.AddMinutes(-Math.Max(1, _options.StatusSyncMinutes));

        var due = await _db.M5aznShipments
            .Where(s => (s.Status == M5aznShipmentStatus.Submitted
                         || s.Status == M5aznShipmentStatus.Processing
                         || s.Status == M5aznShipmentStatus.Shipped)
                        && (s.LastSyncedAt == null || s.LastSyncedAt <= cutoff))
            .OrderBy(s => s.LastSyncedAt)
            .Take(50)
            .ToListAsync(ct);

        var refreshed = 0;
        foreach (var shipment in due)
        {
            if (await RefreshOneAsync(shipment, ct)) refreshed++;
        }

        return refreshed;
    }

    /// <summary>Asks M5azn about one shipment and carries a change on to the order. True when it changed.</summary>
    private async Task<bool> RefreshOneAsync(M5aznShipment shipment, CancellationToken ct)
    {
        var remote = await TryGetRemoteAsync(shipment.RemoteOrderId ?? shipment.Reference.ToString(), ct);
        var now = _clock.UtcNow;
        shipment.LastSyncedAt = now;

        if (remote is null)
        {
            await SaveIgnoringConflictAsync(ct);
            return false;
        }

        var before = (shipment.Status, shipment.TrackingNumber, shipment.RemoteStatus);

        shipment.RemoteOrderId ??= remote.RemoteOrderId;
        shipment.RemoteStatus = remote.Status ?? shipment.RemoteStatus;
        shipment.TrackingNumber = remote.TrackingNumber ?? shipment.TrackingNumber;
        shipment.TrackingUrl = remote.TrackingUrl ?? shipment.TrackingUrl;
        shipment.Carrier = remote.Carrier ?? shipment.Carrier;

        var mapped = M5aznStatusMap.Map(remote.Status);

        // A tracking number with no clearer word means it has left the warehouse.
        if (mapped is null && !string.IsNullOrWhiteSpace(remote.TrackingNumber))
        {
            mapped = M5aznShipmentStatus.Shipped;
        }

        ApplyMappedStatus(shipment, mapped, now);

        var changed = before != (shipment.Status, shipment.TrackingNumber, shipment.RemoteStatus);
        if (changed) shipment.UpdatedAt = now;

        if (!await SaveIgnoringConflictAsync(ct)) return false;

        if (before.Status != shipment.Status || before.TrackingNumber != shipment.TrackingNumber)
        {
            await AdvanceSourceAsync(shipment, ct);
        }

        return changed;
    }

    private void ApplyMappedStatus(M5aznShipment shipment, M5aznShipmentStatus? mapped, DateTime now)
    {
        if (mapped is null) return;

        if (mapped == M5aznShipmentStatus.Cancelled)
        {
            if (shipment.Status == M5aznShipmentStatus.Delivered) return;
            shipment.Status = M5aznShipmentStatus.Cancelled;
            shipment.CancelledAt ??= now;
            shipment.LastError = "M5azn cancelled or returned this order. Review the DealGo order; nothing was refunded automatically.";
            _logger.LogWarning("M5azn: shipment {Reference} was cancelled at M5azn ({Remote}).",
                shipment.Reference, shipment.RemoteStatus);
            return;
        }

        if (M5aznStatusMap.Rank(mapped.Value) <= M5aznStatusMap.Rank(shipment.Status)) return;

        shipment.Status = mapped.Value;
        if (mapped == M5aznShipmentStatus.Shipped) shipment.ShippedAt ??= now;
        if (mapped == M5aznShipmentStatus.Delivered)
        {
            shipment.ShippedAt ??= now;
            shipment.DeliveredAt ??= now;
        }
    }

    /// <summary>
    /// Carries a shipment's progress onto what it fulfils. A store order moves to Processing once
    /// M5azn has it, and on to Shipped and Delivered (with the usual customer notifications) only
    /// when M5azn delivers everything on it; a mixed order's other lines are still DealGo's to ship.
    /// A cancellation at M5azn is never turned into a refund here: that is a decision for a person.
    /// </summary>
    private async Task AdvanceSourceAsync(M5aznShipment shipment, CancellationToken ct)
    {
        try
        {
            await AdvanceStoreOrderAsync(shipment, ct);
        }
        catch (DomainException ex)
        {
            _logger.LogWarning("M5azn: could not move {Type} {Id} along with shipment {Reference}: {Message}",
                shipment.SourceType, shipment.SourceId, shipment.Reference, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogInformation("M5azn: {Type} {Id} changed meanwhile; it is moved on the next pass.",
                shipment.SourceType, shipment.SourceId);
        }
    }

    private async Task AdvanceStoreOrderAsync(M5aznShipment shipment, CancellationToken ct)
    {
        var order = await _db.StoreOrders.AsNoTracking()
            .Where(o => o.Id == shipment.SourceId)
            .Select(o => new
            {
                o.Status,
                AllShippedByM5azn = o.Items.All(i => !i.Product!.RequiresShipping || i.Product.M5aznProductId != null)
            })
            .FirstOrDefaultAsync(ct);
        if (order is null) return;

        var target = shipment.Status switch
        {
            M5aznShipmentStatus.Submitted or M5aznShipmentStatus.Processing => StoreOrderStatus.Processing,
            M5aznShipmentStatus.Shipped => order.AllShippedByM5azn ? StoreOrderStatus.Shipped : StoreOrderStatus.Processing,
            M5aznShipmentStatus.Delivered => order.AllShippedByM5azn ? StoreOrderStatus.Delivered : StoreOrderStatus.Processing,
            _ => (StoreOrderStatus?)null
        };
        if (target is null) return;

        var steps = new[] { StoreOrderStatus.Processing, StoreOrderStatus.Shipped, StoreOrderStatus.Delivered };
        var current = order.Status;

        foreach (var step in steps)
        {
            if (StepIndex(step) > StepIndex(target.Value)) break;
            if (StepIndex(current) >= StepIndex(step)) continue;

            var note = step == StoreOrderStatus.Shipped ? shipment.TrackingNumber : null;
            var updated = await _storeAdmin.TransitionOrderAsync(shipment.SourceId, step, note, ct);
            current = updated.Status;
        }

        static int StepIndex(StoreOrderStatus s) => s switch
        {
            StoreOrderStatus.Paid => 0,
            StoreOrderStatus.Processing => 1,
            StoreOrderStatus.Shipped => 2,
            StoreOrderStatus.Delivered => 3,
            // Completed, cancelled and refunded orders are past anything a shipment can say.
            _ => 99
        };
    }

    private async Task<M5aznRemoteOrder?> TryGetRemoteAsync(string id, CancellationToken ct)
    {
        try
        {
            return await _client.GetOrderAsync(id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "M5azn: asking about order {Id} failed.", id);
            return null;
        }
    }

    // ================================================================ order body

    private sealed record BuiltOrder(M5aznOrderRequest? Request, string? CancelReason = null, string? Problem = null);

    private async Task<BuiltOrder> BuildOrderAsync(M5aznShipment shipment, CancellationToken ct)
    {
        var order = await _db.StoreOrders.AsNoTracking()
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .Include(o => o.ShippingAddress)
            .Include(o => o.User)
            .FirstOrDefaultAsync(o => o.Id == shipment.SourceId, ct);

        if (order is null) return new BuiltOrder(null, CancelReason: "The order no longer exists.");
        if (order.Status is not (StoreOrderStatus.Paid or StoreOrderStatus.Processing))
        {
            return new BuiltOrder(null, CancelReason: $"Not sent: the order is {order.Status}.");
        }

        var lines = order.Items.Where(i => i.Product?.M5aznProductId != null).ToList();
        if (lines.Count == 0) return new BuiltOrder(null, CancelReason: "No M5azn products on the order any more.");

        var unlinkedSku = lines.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Product!.M5aznSku));
        if (unlinkedSku is not null)
        {
            return new BuiltOrder(null, Problem: $"\"{unlinkedSku.ProductNameSnapshot}\" has no M5azn SKU. Add it, then send again.");
        }

        if (order.ShippingAddress is null) return new BuiltOrder(null, Problem: "The order has no delivery address.");

        var items = lines.Select((i, index) => new M5aznOrderItem(
            index + 1,
            i.Product!.M5aznProductId!.Value,
            i.Product.M5aznSku!.Trim(),
            i.ProductNameSnapshot,
            i.Quantity)).ToList();

        var total = Money.Round(lines.Sum(i => i.TotalPrice));
        return new BuiltOrder(BuildRequest(shipment, total, order.PaidAt ?? order.CreatedAt,
            order.ShippingAddress, order.User?.Email, items));
    }

    private M5aznOrderRequest BuildRequest(
        M5aznShipment shipment,
        decimal total,
        DateTime createdAt,
        ShippingAddress address,
        string? email,
        IReadOnlyList<M5aznOrderItem> items)
    {
        var cod = string.Equals(_options.PaymentMethod, "cod", StringComparison.OrdinalIgnoreCase);
        var (first, last) = SplitName(address.FullName);

        return new M5aznOrderRequest(
            Id: shipment.Reference,
            Total: total,
            PaymentMethod: cod ? "cod" : "card",
            CodAmount: cod ? total : 0m,
            OrderCreatedAt: createdAt,
            PlatformName: _options.PlatformName,
            ShippingInfo: new M5aznShippingInfo(
                FormatAddress(address),
                CountryName(CountryCode(address.Country), address.Country),
                CountryCode(address.Country),
                address.City.Trim()),
            CustomerInfo: new M5aznCustomerInfo(first, last, email ?? string.Empty, address.Phone.Trim()),
            Items: items);
    }

    /// <summary>One line for M5azn's single address field, most specific first.</summary>
    internal static string FormatAddress(ShippingAddress a)
    {
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(a.BuildingNumber) ? null : $"{a.BuildingNumber.Trim()} {a.Street.Trim()}",
            string.IsNullOrWhiteSpace(a.BuildingNumber) ? a.Street.Trim() : null,
            a.District?.Trim(),
            a.ShortAddress is { Length: > 0 } s ? $"National address {s.Trim()}" : null,
            a.Notes?.Trim()
        };

        return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    internal static string CountryCode(string? country)
    {
        var c = (country ?? string.Empty).Trim().ToLowerInvariant();
        if (c is "ae" || c.Contains("emirat") || c.Contains("uae") || c.Contains("الإمارات") || c.Contains("الامارات")) return "AE";
        if (c is "om" || c.Contains("oman") || c.Contains("عمان") || c.Contains("عُمان")) return "OM";
        return "SA";
    }

    /// <summary>Addresses store the two-letter code; M5azn's example has a name in its country field.</summary>
    internal static string CountryName(string code, string? stored) =>
        !string.IsNullOrWhiteSpace(stored) && stored.Trim().Length > 3
            ? stored.Trim()
            : code switch { "AE" => "United Arab Emirates", "OM" => "Oman", _ => "Saudi Arabia" };

    internal static (string First, string Last) SplitName(string fullName)
    {
        var parts = (fullName ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => ("Customer", "-"),
            1 => (parts[0], "-"),
            _ => (parts[0], parts[1].Trim())
        };
    }

    // ================================================================ stock

    public async Task<int> SyncStockAsync(CancellationToken ct = default)
    {
        if (!IsEnabled || !_options.StockSyncEnabled) return 0;

        // Ask for a little more than the gap between runs, so a late run misses nothing.
        var hours = Math.Clamp((int)Math.Ceiling(Math.Max(1, _options.StockSyncMinutes) / 60.0) + 1, 1, 24);
        var changes = await _client.GetQuantityChangesAsync(hours, ct);
        if (changes.Count == 0) return 0;

        var ids = changes.Select(c => c.ProductId).Distinct().ToList();
        var linked = await _db.StoreProducts.AsNoTracking()
            .Where(p => p.M5aznProductId != null && ids.Contains(p.M5aznProductId!.Value))
            .Select(p => new { p.Id, M5aznProductId = p.M5aznProductId!.Value, p.M5aznSku })
            .ToListAsync(ct);

        var updated = 0;
        foreach (var product in linked)
        {
            var forProduct = changes.Where(c => c.ProductId == product.M5aznProductId).ToList();
            var match = forProduct.LastOrDefault(c => !string.IsNullOrWhiteSpace(product.M5aznSku)
                                                      && string.Equals(c.Sku?.Trim(), product.M5aznSku.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? (forProduct.Count == 1 || forProduct.All(c => string.IsNullOrWhiteSpace(c.Sku)) ? forProduct.LastOrDefault() : null);
            if (match is null) continue;

            if (await SetStockAsync(product.Id, Math.Max(0, match.Quantity), ct)) updated++;
        }

        if (updated > 0) _logger.LogInformation("M5azn: stock updated on {Count} product(s).", updated);
        return updated;
    }

    private async Task<bool> SetStockAsync(Guid productId, int quantity, CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);

        var product = await _db.StoreProducts
            .FromSqlRaw("""SELECT *, xmin FROM "StoreProducts" WHERE "Id" = {0} FOR UPDATE""", productId)
            .FirstOrDefaultAsync(ct);
        if (product is null) return false;

        var before = product.StockQuantity;
        if (before == quantity) return false;

        var now = _clock.UtcNow;
        product.StockQuantity = quantity;
        product.UpdatedAt = now;

        if (product.Status == StoreProductStatus.OutOfStock && quantity > 0) product.Status = StoreProductStatus.Active;
        else if (product.Status == StoreProductStatus.Active && quantity == 0) product.Status = StoreProductStatus.OutOfStock;

        _db.InventoryTransactions.Add(new InventoryTransaction
        {
            ProductId = productId,
            Type = quantity > before ? InventoryTransactionType.Restock : InventoryTransactionType.Adjustment,
            Quantity = quantity - before,
            BeforeQuantity = before,
            AfterQuantity = quantity,
            ReferenceType = "M5azn",
            Reason = "Stock synced from M5azn",
            CreatedAt = now
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    // ================================================================ staff

    public async Task<M5aznOverviewDto> GetOverviewAsync(CancellationToken ct = default)
    {
        var counts = await _db.M5aznShipments.AsNoTracking()
            .GroupBy(s => s.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var linkedProducts = await _db.StoreProducts.CountAsync(p => p.M5aznProductId != null, ct);

        return new M5aznOverviewDto(
            _options.Enabled,
            _options.HasCredentials,
            _options.AutoSubmit,
            _options.StockSyncEnabled,
            _options.BaseUrl,
            _options.PaymentMethod,
            Enum.GetValues<M5aznShipmentStatus>().ToDictionary(
                s => s.ToString(), s => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0),
            linkedProducts);
    }

    public async Task<PagedResult<M5aznShipmentDto>> GetShipmentsAsync(M5aznShipmentQuery query, CancellationToken ct = default)
    {
        var q = _db.M5aznShipments.AsNoTracking().AsQueryable();
        if (query.Status is { } status) q = q.Where(s => s.Status == status);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            if (long.TryParse(term, out var reference))
            {
                q = q.Where(s => s.Reference == reference || s.RemoteOrderId == term || s.TrackingNumber == term);
            }
            else
            {
                q = q.Where(s => s.RemoteOrderId == term || s.TrackingNumber == term
                                 || _db.StoreOrders.Any(o => o.Id == s.SourceId && o.OrderNumber == term));
            }
        }

        var total = await q.LongCountAsync(ct);
        var page = await q.OrderByDescending(s => s.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .ToListAsync(ct);

        return PagedResult<M5aznShipmentDto>.Create(await MapAsync(page, ct), query.Page, query.PageSize, total);
    }

    public async Task<M5aznShipmentDto> SubmitNowAsync(Guid shipmentId, CancellationToken ct = default)
    {
        EnsureEnabled();

        var shipment = await _db.M5aznShipments.FirstOrDefaultAsync(s => s.Id == shipmentId, ct)
            ?? throw new DomainException(ErrorCodes.NotFound, "Shipment not found.");

        if (shipment.Status is not (M5aznShipmentStatus.AwaitingApproval or M5aznShipmentStatus.Failed or M5aznShipmentStatus.Queued))
        {
            throw new DomainException(ErrorCodes.InvalidOrderState,
                $"A shipment that is {shipment.Status} cannot be sent again.");
        }

        var before = shipment.Status;
        shipment.Status = M5aznShipmentStatus.Queued;
        shipment.NextAttemptAt = null;
        if (before == M5aznShipmentStatus.Failed) shipment.Attempts = 0;
        shipment.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.M5aznShipmentSubmitted, nameof(M5aznShipment), shipment.Id,
            new { Status = before }, new { Status = M5aznShipmentStatus.Queued, shipment.Reference }, ct: ct);

        await SubmitOneAsync(shipment.Id, ct);
        return await GetDtoAsync(shipment.Id, ct);
    }

    public async Task<M5aznShipmentDto> RefreshAsync(Guid shipmentId, CancellationToken ct = default)
    {
        EnsureEnabled();

        var shipment = await _db.M5aznShipments.FirstOrDefaultAsync(s => s.Id == shipmentId, ct)
            ?? throw new DomainException(ErrorCodes.NotFound, "Shipment not found.");

        if (shipment.Status is not (M5aznShipmentStatus.AwaitingApproval or M5aznShipmentStatus.Queued))
        {
            await RefreshOneAsync(shipment, ct);
        }

        return await GetDtoAsync(shipment.Id, ct);
    }

    public async Task<M5aznLinksDto> GetLinksAsync(CancellationToken ct = default)
    {
        var products = await _db.StoreProducts.AsNoTracking()
            .Where(p => p.Status != StoreProductStatus.Archived || p.M5aznProductId != null)
            .OrderByDescending(p => p.M5aznProductId != null).ThenBy(p => p.Name)
            .Take(500)
            .Select(p => new M5aznLinkedItemDto(p.Id, p.Name, p.Sku, p.M5aznProductId, p.M5aznSku, p.StockQuantity))
            .ToListAsync(ct);

        return new M5aznLinksDto(products);
    }

    public async Task LinkStoreProductAsync(Guid productId, M5aznLinkRequest request, CancellationToken ct = default)
    {
        var (id, sku) = ValidateLink(request);

        var product = await _db.StoreProducts.FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw new DomainException(ErrorCodes.ProductNotFound, "Product not found.");

        var before = new { product.M5aznProductId, product.M5aznSku };
        product.M5aznProductId = id;
        product.M5aznSku = sku;
        product.UpdatedAt = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActionType.M5aznLinkChanged, nameof(StoreProduct), productId,
            before, new { M5aznProductId = id, M5aznSku = sku }, ct: ct);
    }

    private static (long? Id, string? Sku) ValidateLink(M5aznLinkRequest request)
    {
        var sku = string.IsNullOrWhiteSpace(request.M5aznSku) ? null : request.M5aznSku.Trim();

        if (request.M5aznProductId is null && sku is null) return (null, null);

        if (request.M5aznProductId is not > 0)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The M5azn product id must be a positive number.");
        }

        if (sku is null)
        {
            throw new DomainException(ErrorCodes.ValidationFailed, "The M5azn SKU is required with the product id.");
        }

        if (sku.Length > 128) throw new DomainException(ErrorCodes.ValidationFailed, "The M5azn SKU is too long.");

        return (request.M5aznProductId, sku);
    }

    public async Task<M5aznProductInfo?> LookupProductAsync(long m5aznProductId, CancellationToken ct = default)
    {
        EnsureEnabled();
        if (m5aznProductId <= 0) throw new DomainException(ErrorCodes.ValidationFailed, "Invalid M5azn product id.");

        try
        {
            return await _client.GetProductAsync(m5aznProductId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not DomainException)
        {
            _logger.LogWarning(ex, "M5azn: product {Id} lookup failed.", m5aznProductId);
            throw new DomainException(ErrorCodes.ServiceUnavailable, $"M5azn could not be asked right now: {ex.Message}");
        }
    }

    // ================================================================ customer

    public async Task<ShipmentTrackingDto?> GetStoreOrderTrackingAsync(Guid userId, Guid orderId, CancellationToken ct = default)
    {
        // Only once it has actually gone to M5azn: a queued or waiting one is not news for the customer.
        var s = await _db.M5aznShipments.AsNoTracking()
            .Where(x => x.SourceType == M5aznSourceType.StoreOrder && x.SourceId == orderId && x.UserId == userId
                        && x.Status != M5aznShipmentStatus.AwaitingApproval
                        && x.Status != M5aznShipmentStatus.Queued
                        && x.Status != M5aznShipmentStatus.Failed)
            .FirstOrDefaultAsync(ct);

        return s is null
            ? null
            : new ShipmentTrackingDto("M5azn", s.Status, s.RemoteStatus, s.TrackingNumber, s.TrackingUrl, s.Carrier,
                s.CreatedAt, s.SubmittedAt, s.ShippedAt, s.DeliveredAt, s.LastSyncedAt);
    }

    public async Task<M5aznShipmentDto?> GetForStoreOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var s = await _db.M5aznShipments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SourceType == M5aznSourceType.StoreOrder && x.SourceId == orderId, ct);
        return s is null ? null : (await MapAsync(new[] { s }, ct))[0];
    }

    // ================================================================ helpers

    private void EnsureEnabled()
    {
        if (!IsEnabled)
        {
            throw new DomainException(ErrorCodes.ServiceUnavailable,
                "M5azn is switched off. Set M5azn:Enabled and the seller account to use it.");
        }
    }

    private async Task<M5aznShipmentDto> GetDtoAsync(Guid id, CancellationToken ct)
    {
        var s = await _db.M5aznShipments.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        return (await MapAsync(new[] { s }, ct))[0];
    }

    private async Task<IReadOnlyList<M5aznShipmentDto>> MapAsync(IReadOnlyList<M5aznShipment> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<M5aznShipmentDto>();

        var orderIds = rows.Select(r => r.SourceId).ToList();
        var userIds = rows.Select(r => r.UserId).Distinct().ToList();

        var orderNumbers = await _db.StoreOrders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.OrderNumber })
            .ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);

        var names = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        return rows.Select(s => new M5aznShipmentDto(
            s.Id, s.Reference, s.SourceType, s.SourceId,
            orderNumbers.GetValueOrDefault(s.SourceId),
            s.UserId, names.GetValueOrDefault(s.UserId),
            s.Status, s.RemoteOrderId, s.RemoteStatus, s.TrackingNumber, s.TrackingUrl, s.Carrier,
            s.LastError, s.Attempts, s.NextAttemptAt, s.CreatedAt, s.SubmittedAt, s.ShippedAt,
            s.DeliveredAt, s.LastSyncedAt)).ToList();
    }

    /// <summary>Saves, treating "someone else changed it meanwhile" as "try on the next pass".</summary>
    private async Task<bool> SaveIgnoringConflictAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogInformation(ex, "M5azn: a shipment changed meanwhile; it is picked up on the next pass.");
            foreach (var entry in ex.Entries) await entry.ReloadAsync(ct);
            return false;
        }
    }

    private void DetachAdded()
    {
        if (_db is not DbContext context) return;
        foreach (var entry in context.ChangeTracker.Entries<M5aznShipment>().Where(e => e.State == EntityState.Added).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
