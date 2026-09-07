using AuctionPlatform.Domain.Common;
using AuctionPlatform.Domain.Enums;

namespace AuctionPlatform.Domain.Rules;

/// <summary>Legal store order transitions. Enforced server side on every admin action.</summary>
public static class StoreOrderStateMachine
{
    private static readonly Dictionary<StoreOrderStatus, StoreOrderStatus[]> Allowed = new()
    {
        [StoreOrderStatus.Pending] = new[] { StoreOrderStatus.Paid, StoreOrderStatus.Cancelled },
        [StoreOrderStatus.Paid] = new[] { StoreOrderStatus.Processing, StoreOrderStatus.Cancelled },
        [StoreOrderStatus.Processing] = new[] { StoreOrderStatus.Shipped, StoreOrderStatus.Cancelled },
        [StoreOrderStatus.Shipped] = new[] { StoreOrderStatus.Delivered },
        [StoreOrderStatus.Delivered] = new[] { StoreOrderStatus.Completed, StoreOrderStatus.Refunded },
        [StoreOrderStatus.Completed] = new[] { StoreOrderStatus.Refunded },
        [StoreOrderStatus.Cancelled] = new[] { StoreOrderStatus.Refunded },
        [StoreOrderStatus.Refunded] = Array.Empty<StoreOrderStatus>()
    };

    public static bool CanTransition(StoreOrderStatus from, StoreOrderStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureCanTransition(StoreOrderStatus from, StoreOrderStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainException(
                ErrorCodes.InvalidOrderState,
                $"Cannot move order from {from} to {to}.");
        }
    }

    /// <summary>A customer may cancel only before the order has physically moved.</summary>
    public static bool IsCustomerCancellable(StoreOrderStatus status) =>
        status == StoreOrderStatus.Pending ||
        status == StoreOrderStatus.Paid ||
        status == StoreOrderStatus.Processing;

    /// <summary>Cancelling one of these releases reserved stock back to available.</summary>
    public static bool ReleasesStock(StoreOrderStatus status) =>
        status == StoreOrderStatus.Paid ||
        status == StoreOrderStatus.Processing ||
        status == StoreOrderStatus.Pending;
}
