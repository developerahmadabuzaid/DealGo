import 'dart:async';
import 'dart:math';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/config/app_config.dart';
import '../../../core/errors/api_exception.dart';
import '../../../core/providers.dart';
import '../../../core/realtime/auction_hub_client.dart';
import '../data/auction_models.dart';

/// Everything the live auction screen renders.
class LiveAuctionState {
  const LiveAuctionState({
    this.snapshot,
    this.detail,
    this.hubStatus = HubStatus.disconnected,
    this.isBidding = false,
    this.error,
    this.lastBidFlash,
    this.serverOffset = Duration.zero,
    this.rankMessage,
  });

  final AuctionSnapshot? snapshot;
  final AuctionDetail? detail;
  final HubStatus hubStatus;
  final bool isBidding;
  final String? error;

  /// The most recent accepted bid, used to animate the price.
  final double? lastBidFlash;

  /// serverTime − deviceTime. The countdown is rendered through this so a wrong device clock
  /// cannot make the timer lie.
  final Duration serverOffset;

  /// A transient message such as "you are in the winning four".
  final String? rankMessage;

  bool get isConnected => hubStatus == HubStatus.connected;

  /// Bidding needs a live, authoritative connection; it is never queued for later.
  bool get canBid =>
      isConnected &&
      !isBidding &&
      (snapshot?.isLive ?? false) &&
      !(snapshot?.targetReached ?? false);

  Duration get remaining {
    final endsAt = snapshot?.endsAt;
    if (endsAt == null) return Duration.zero;

    final serverNow = DateTime.now().toUtc().add(serverOffset);
    final left = endsAt.difference(serverNow);
    return left.isNegative ? Duration.zero : left;
  }

  LiveAuctionState copyWith({
    AuctionSnapshot? snapshot,
    AuctionDetail? detail,
    HubStatus? hubStatus,
    bool? isBidding,
    String? error,
    bool clearError = false,
    double? lastBidFlash,
    Duration? serverOffset,
    String? rankMessage,
    bool clearRankMessage = false,
  }) =>
      LiveAuctionState(
        snapshot: snapshot ?? this.snapshot,
        detail: detail ?? this.detail,
        hubStatus: hubStatus ?? this.hubStatus,
        isBidding: isBidding ?? this.isBidding,
        error: clearError ? null : (error ?? this.error),
        lastBidFlash: lastBidFlash ?? this.lastBidFlash,
        serverOffset: serverOffset ?? this.serverOffset,
        rankMessage: clearRankMessage ? null : (rankMessage ?? this.rankMessage),
      );
}

/// Drives one live auction screen.
///
/// The controller holds no authority of its own: it renders the server's snapshot, applies pushed
/// events on top, and refetches the snapshot whenever the connection was interrupted or an event
/// might have been missed.
class LiveAuctionController extends StateNotifier<LiveAuctionState> {
  // The hub is read once, here, and kept: dispose runs after the provider container may already be
  // gone, and reading it again from there throws.
  LiveAuctionController(this._ref, this.auctionId)
      : _hub = _ref.read(hubClientProvider),
        super(const LiveAuctionState()) {
    _start();
  }

  final Ref _ref;
  final String auctionId;
  final AuctionHubClient _hub;

  StreamSubscription<HubEvent>? _eventSub;
  StreamSubscription<HubStatus>? _statusSub;
  Timer? _resyncTimer;
  final _random = Random();

  Future<void> _start() async {
    final hub = _hub;

    _statusSub = hub.status.listen((status) {
      final wasDisconnected = state.hubStatus != HubStatus.connected;
      state = state.copyWith(hubStatus: status);

      // Reconnecting means we may have missed events; the snapshot is the recovery path.
      if (status == HubStatus.connected && wasDisconnected) {
        hub.joinAuction(auctionId);
        unawaited(resync());
      }
    });

    _eventSub = hub.events.listen(_onEvent);

    await hub.connect();
    // The status stream only reports changes. When the connection was already up before this
    // screen opened — the usual case, since the app connects at sign-in — nothing changes, so the
    // current status has to be read directly. Without this the screen believed it was offline and
    // kept the bid button switched off on a perfectly good connection.
    if (mounted) state = state.copyWith(hubStatus: hub.currentStatus);
    await hub.joinAuction(auctionId);

    await Future.wait([resync(), _loadDetail()]);

    // A periodic resync is the safety net for anything the event stream lost.
    _resyncTimer = Timer.periodic(AppConfig.resyncInterval, (_) => resync());

    // Automatic reconnect only covers a connection that was once established. If the first attempt
    // failed, nothing would ever try again, so this does.
    _reconnectTimer = Timer.periodic(const Duration(seconds: 5), (_) => _ensureConnected());
  }

  Timer? _reconnectTimer;
  bool _reconnecting = false;

  Future<void> _ensureConnected() async {
    final hub = _hub;
    if (_reconnecting || hub.currentStatus != HubStatus.disconnected) return;

    _reconnecting = true;
    try {
      await hub.connect();
      if (!mounted) return;
      state = state.copyWith(hubStatus: hub.currentStatus);
      if (hub.currentStatus == HubStatus.connected) {
        await hub.joinAuction(auctionId);
        unawaited(resync());
      }
    } finally {
      _reconnecting = false;
    }
  }

  Future<void> _loadDetail() async {
    try {
      final detail = await _ref.read(auctionRepositoryProvider).detail(auctionId);
      state = state.copyWith(detail: detail);
    } catch (_) {
      // The snapshot carries everything the live screen strictly needs.
    }
  }

  /// Pulls the authoritative state and re-derives the clock offset from it.
  Future<void> resync() async {
    try {
      final snapshot = await _ref.read(auctionRepositoryProvider).snapshot(auctionId);
      final offset = snapshot.serverTime.difference(DateTime.now().toUtc());

      state = state.copyWith(snapshot: snapshot, serverOffset: offset, clearError: true);
    } catch (e) {
      if (e is ApiException) state = state.copyWith(error: e.displayMessage);
    }
  }

  void _onEvent(HubEvent event) {
    final snapshot = state.snapshot;
    if (snapshot == null) return;

    // Events for other auctions share the connection; ignore them.
    final eventAuctionId = event.payload['auctionId'] as String?;
    if (eventAuctionId != null && eventAuctionId != auctionId) return;

    switch (event.name) {
      case 'BidPlaced':
      case 'AuctionPriceChanged':
        final price = (event.payload['newPrice'] ?? event.payload['currentPrice']) as num?;
        if (price != null) {
          state = state.copyWith(
            snapshot: _withPrice(snapshot, price.toDouble()),
            lastBidFlash: price.toDouble(),
          );
        }
        break;

      case 'AuctionParticipantCountChanged':
        final count = event.payload['participantCount'] as int?;
        if (count != null) state = state.copyWith(snapshot: _withCount(snapshot, count));
        // The registration bar on the page is drawn from the detail.
        _ref.invalidate(auctionDetailProvider(auctionId));
        break;

      case 'LeaderboardUpdated':
        final raw = event.payload['leaderboard'] as List?;
        if (raw != null) {
          final entries = raw
              .map((e) => LeaderboardEntry.fromJson(Map<String, dynamic>.from(e as Map)))
              .toList();
          state = state.copyWith(snapshot: _withLeaderboard(snapshot, entries));
        }
        break;

      case 'AuctionReady':
      case 'AuctionStartScheduled':
      case 'AuctionStartTimeChanged':
      case 'FinalPhaseStarted':
      case 'AuctionStarted':
      case 'AuctionFinished':
      case 'WinnersDeclared':
      case 'AuctionCancelled':
      case 'AuctionPaused':
      case 'AuctionResumed':
        // These change status or timing, both of which the server owns: refetch rather than guess.
        // The page is drawn from the detail, so that is refetched too; before, it stayed on the
        // old panel until the user pulled down.
        unawaited(resync());
        unawaited(_loadDetail());
        _ref.invalidate(auctionDetailProvider(auctionId));
        if (event.name == 'WinnersDeclared') {
          _ref.invalidate(auctionWinnersProvider(auctionId));
        }
        break;
    }
  }

  AuctionSnapshot _withPrice(AuctionSnapshot s, double price) => AuctionSnapshot(
        id: s.id, status: s.status, currentPrice: price, startingPrice: s.startingPrice,
        targetPrice: s.targetPrice, finalPhasePrice: s.finalPhasePrice,
        bidIncrement: s.bidIncrement, currency: s.currency,
        participantCount: s.participantCount, requiredParticipants: s.requiredParticipants,
        remainingSeconds: s.remainingSeconds, endsAt: s.endsAt,
        targetReached: price >= s.targetPrice, progress: _progress(s, price),
        myRank: s.myRank, myHighestPrice: s.myHighestPrice, leaderboard: s.leaderboard,
        lastSequenceNumber: s.lastSequenceNumber, serverTime: s.serverTime,
      );

  AuctionSnapshot _withCount(AuctionSnapshot s, int count) => AuctionSnapshot(
        id: s.id, status: s.status, currentPrice: s.currentPrice, startingPrice: s.startingPrice,
        targetPrice: s.targetPrice, finalPhasePrice: s.finalPhasePrice,
        bidIncrement: s.bidIncrement, currency: s.currency,
        participantCount: count, requiredParticipants: s.requiredParticipants,
        remainingSeconds: s.remainingSeconds, endsAt: s.endsAt,
        targetReached: s.targetReached, progress: s.progress,
        myRank: s.myRank, myHighestPrice: s.myHighestPrice, leaderboard: s.leaderboard,
        lastSequenceNumber: s.lastSequenceNumber, serverTime: s.serverTime,
      );

  AuctionSnapshot _withLeaderboard(AuctionSnapshot s, List<LeaderboardEntry> board) =>
      AuctionSnapshot(
        id: s.id, status: s.status, currentPrice: s.currentPrice, startingPrice: s.startingPrice,
        targetPrice: s.targetPrice, finalPhasePrice: s.finalPhasePrice,
        bidIncrement: s.bidIncrement, currency: s.currency,
        participantCount: s.participantCount, requiredParticipants: s.requiredParticipants,
        remainingSeconds: s.remainingSeconds, endsAt: s.endsAt,
        targetReached: s.targetReached, progress: s.progress,
        myRank: s.myRank, myHighestPrice: s.myHighestPrice, leaderboard: board,
        lastSequenceNumber: s.lastSequenceNumber, serverTime: s.serverTime,
      );

  double _progress(AuctionSnapshot s, double price) {
    final span = s.targetPrice - s.startingPrice;
    if (span <= 0) return 1;
    return ((price - s.startingPrice) / span).clamp(0, 1);
  }

  /// Places one bid. The key makes a network-level retry safe.
  Future<BidResult?> placeBid() async {
    if (!state.canBid) return null;

    state = state.copyWith(isBidding: true, clearError: true);
    final key = '${DateTime.now().microsecondsSinceEpoch}-${_random.nextInt(1 << 32)}';

    try {
      final result = await _ref.read(auctionRepositoryProvider).placeBid(auctionId, key);
      final previousRank = state.snapshot?.myRank;

      await resync();

      final rank = result.myRank;
      String? message;
      if (rank <= 4 && (previousRank == null || previousRank > 4)) {
        message = 'inTopFour';
      } else if (rank > 4 && previousRank != null && previousRank <= 4) {
        message = 'lostFourthPlace';
      }

      state = state.copyWith(
        isBidding: false,
        lastBidFlash: result.newPrice,
        rankMessage: message,
        clearRankMessage: message == null,
      );

      return result;
    } on ApiException catch (e) {
      state = state.copyWith(isBidding: false, error: e.displayMessage);

      // These mean the world moved on; pull the truth rather than showing a stale screen.
      if (e.code == ErrorCodes.auctionFinished ||
          e.code == ErrorCodes.targetAlreadyReached ||
          e.code == ErrorCodes.auctionNotActive) {
        await resync();
      }
      return null;
    } catch (_) {
      state = state.copyWith(isBidding: false, error: 'Could not place the bid.');
      return null;
    }
  }

  void clearRankMessage() => state = state.copyWith(clearRankMessage: true);

  /// Called once the message has been shown, so the same refusal is not reported twice.
  void clearError() => state = state.copyWith(clearError: true);

  @override
  void dispose() {
    _resyncTimer?.cancel();
    _reconnectTimer?.cancel();
    _eventSub?.cancel();
    _statusSub?.cancel();
    _hub.leaveAuction(auctionId);
    super.dispose();
  }
}

final liveAuctionProvider = StateNotifierProvider.autoDispose
    .family<LiveAuctionController, LiveAuctionState, String>(
        (ref, auctionId) => LiveAuctionController(ref, auctionId));
