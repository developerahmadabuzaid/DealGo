using System.Security.Claims;
using AuctionPlatform.Application.Auctions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AuctionPlatform.Api.Hubs;

/// <summary>
/// Real-time auction channel.
///
/// The hub carries state that PostgreSQL has already committed; it decides nothing. A client that
/// reconnects joins its groups again and then pulls <see cref="GetSnapshot"/>, rather than trying
/// to replay whatever it missed.
/// </summary>
[Authorize]
public sealed class AuctionHub : Hub
{
    public const string Route = "/hubs/auction";

    private readonly IAuctionService _auctions;
    private readonly ILogger<AuctionHub> _logger;

    public AuctionHub(IAuctionService auctions, ILogger<AuctionHub> logger)
    {
        _auctions = auctions;
        _logger = logger;
    }

    public static string AuctionGroup(Guid auctionId) => $"auction:{auctionId}";
    public static string UserGroup(Guid userId) => $"user:{userId}";

    private Guid? CurrentUserId =>
        Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public override async Task OnConnectedAsync()
    {
        if (CurrentUserId is { } userId)
        {
            // A personal group so user-scoped events (prize claims, credit) reach every device.
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
            _logger.LogDebug("SignalR connection {ConnectionId} opened for user {UserId}.",
                Context.ConnectionId, userId);
        }

        await base.OnConnectedAsync();
    }

    /// <summary>Subscribes to one auction and returns the authoritative snapshot in the same call.</summary>
    public async Task<AuctionSnapshotDto> JoinAuction(Guid auctionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, AuctionGroup(auctionId));
        return await _auctions.GetSnapshotAsync(auctionId, CurrentUserId, Context.ConnectionAborted);
    }

    public Task LeaveAuction(Guid auctionId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, AuctionGroup(auctionId));

    /// <summary>Explicit resync after a reconnect or a suspected missed event.</summary>
    public Task<AuctionSnapshotDto> GetSnapshot(Guid auctionId) =>
        _auctions.GetSnapshotAsync(auctionId, CurrentUserId, Context.ConnectionAborted);

    /// <summary>
    /// Server clock reading so the client can render a countdown without trusting the device clock.
    /// </summary>
    public Task<DateTime> GetServerTime() => Task.FromResult(DateTime.UtcNow);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
        {
            _logger.LogDebug(exception, "SignalR connection {ConnectionId} dropped.", Context.ConnectionId);
        }
        return base.OnDisconnectedAsync(exception);
    }
}
