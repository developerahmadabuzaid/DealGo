using AuctionPlatform.Application.Auctions;
using AuctionPlatform.Application.Bidding;
using AuctionPlatform.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuctionPlatform.Api.Controllers;

[Route("api/v1/auctions")]
public sealed class AuctionsController : ApiControllerBase
{
    private readonly IAuctionService _auctions;
    private readonly IParticipationService _participation;
    private readonly IBidService _bids;

    public AuctionsController(
        IAuctionService auctions,
        IParticipationService participation,
        IBidService bids)
    {
        _auctions = auctions;
        _participation = participation;
        _bids = bids;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<PagedResult<AuctionListItemDto>>> List(
        [FromQuery] AuctionListQuery query, CancellationToken ct) =>
        Ok(await _auctions.ListAsync(query, CurrentUserIdOrNull, ct: ct));

    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<AuctionDetailDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await _auctions.GetAsync(id, CurrentUserIdOrNull, ct));

    /// <summary>
    /// Authoritative state for a reconnecting client. This is what the mobile app pulls after a
    /// dropped SignalR connection instead of trying to reconstruct missed events.
    /// </summary>
    [HttpGet("{id:guid}/snapshot")]
    [AllowAnonymous]
    public async Task<ActionResult<AuctionSnapshotDto>> Snapshot(Guid id, CancellationToken ct) =>
        Ok(await _auctions.GetSnapshotAsync(id, CurrentUserIdOrNull, ct));

    [HttpGet("{id:guid}/leaderboard")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<LeaderboardEntryDto>>> Leaderboard(
        Guid id, [FromQuery] int take = 10, CancellationToken ct = default) =>
        Ok(await _auctions.GetLeaderboardAsync(id, CurrentUserIdOrNull, take, ct));

    [HttpGet("{id:guid}/winners")]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<WinnerDto>>> Winners(Guid id, CancellationToken ct) =>
        Ok(await _auctions.GetWinnersAsync(id, ct));

    [HttpGet("{id:guid}/participants")]
    [Authorize]
    public async Task<ActionResult<PagedResult<ParticipantAdminDto>>> Participants(
        Guid id, [FromQuery] PagedQuery query, CancellationToken ct) =>
        Ok(await _auctions.GetParticipantsAsync(id, query, ct));

    /// <summary>Pays the entry fee from the Main wallet and registers the caller.</summary>
    [HttpPost("{id:guid}/join")]
    [Authorize]
    public async Task<ActionResult<MyParticipationDto>> Join(
        Guid id, JoinAuctionRequest? request, CancellationToken ct) =>
        Ok(await _participation.JoinAsync(id, CurrentUserId, request?.IdempotencyKey, ct));

    [HttpGet("{id:guid}/my-participation")]
    [Authorize]
    public async Task<ActionResult<MyParticipationDto>> MyParticipation(Guid id, CancellationToken ct)
    {
        var participation = await _participation.GetMyParticipationAsync(id, CurrentUserId, ct);
        return participation is null ? NotFound() : Ok(participation);
    }

    /// <summary>
    /// Places one bid. The body carries only an idempotency key: the price, sequence number and
    /// timestamp are all decided by the server.
    /// </summary>
    [HttpPost("{id:guid}/bids")]
    [Authorize]
    [EnableRateLimiting("bidding")]
    public async Task<ActionResult<BidResultDto>> PlaceBid(
        Guid id, PlaceBidRequest request, CancellationToken ct) =>
        Ok(await _bids.PlaceBidAsync(id, CurrentUserId, request.IdempotencyKey, ct));
}
