import 'dart:async';

import 'package:signalr_netcore/signalr_client.dart';

import '../config/app_config.dart';

/// Connection state as the UI cares about it.
enum HubStatus { disconnected, connecting, connected }

/// A live auction update pushed by the server.
class HubEvent {
  const HubEvent(this.name, this.payload);

  final String name;
  final Map<String, dynamic> payload;
}

/// SignalR client for /hubs/auction.
///
/// The hub is a delivery mechanism, never a source of truth: a client that reconnects rejoins its
/// groups and then pulls a fresh snapshot rather than attempting to replay whatever it missed.
class AuctionHubClient {
  AuctionHubClient({required this.accessTokenProvider});

  /// Supplies a fresh access token for every (re)connection attempt.
  final Future<String?> Function() accessTokenProvider;

  HubConnection? _connection;
  final _events = StreamController<HubEvent>.broadcast();
  final _status = StreamController<HubStatus>.broadcast();
  final _joinedAuctions = <String>{};

  Stream<HubEvent> get events => _events.stream;
  Stream<HubStatus> get status => _status.stream;

  HubStatus _current = HubStatus.disconnected;
  HubStatus get currentStatus => _current;

  void _setStatus(HubStatus value) {
    _current = value;
    if (!_status.isClosed) _status.add(value);
  }

  static const _serverEvents = [
    'AuctionParticipantCountChanged',
    'AuctionReady',
    'AuctionStartScheduled',
    'AuctionStartTimeChanged',
    'AuctionStarted',
    'BidPlaced',
    'AuctionPriceChanged',
    'ParticipantRankChanged',
    'LeaderboardUpdated',
    'FinalPhaseStarted',
    'AuctionFinished',
    'WinnersDeclared',
    'PrizeClaimAvailable',
    'ShoppingCreditAdded',
    'AuctionCancelled',
    'AuctionPaused',
    'AuctionResumed',
  ];

  Future<void> connect() async {
    if (_connection != null && _current == HubStatus.connected) return;

    _setStatus(HubStatus.connecting);

    final connection = HubConnectionBuilder()
        .withUrl(
          AppConfig.hubUrl,
          options: HttpConnectionOptions(
            // The token goes in the query string because a WebSocket handshake cannot carry
            // custom headers; the server only accepts it on the hub path.
            accessTokenFactory: () async => await accessTokenProvider() ?? '',
          ),
        )
        .withAutomaticReconnect(retryDelays: [0, 2000, 5000, 10000, 15000])
        .build();

    for (final name in _serverEvents) {
      connection.on(name, (args) {
        final payload = (args != null && args.isNotEmpty && args.first is Map)
            ? Map<String, dynamic>.from(args.first as Map)
            : <String, dynamic>{};
        if (!_events.isClosed) _events.add(HubEvent(name, payload));
      });
    }

    connection.onclose(({Exception? error}) => _setStatus(HubStatus.disconnected));
    connection.onreconnecting(({Exception? error}) => _setStatus(HubStatus.connecting));

    connection.onreconnected(({String? connectionId}) async {
      _setStatus(HubStatus.connected);
      // Group membership does not survive a reconnect, so rejoin everything we were watching.
      for (final auctionId in _joinedAuctions.toList()) {
        try {
          await connection.invoke('JoinAuction', args: [auctionId]);
        } catch (_) {
          // The caller will resync from the snapshot endpoint regardless.
        }
      }
    });

    _connection = connection;

    try {
      await connection.start();
      _setStatus(HubStatus.connected);
    } catch (_) {
      _setStatus(HubStatus.disconnected);
    }
  }

  /// Subscribes to an auction and returns the snapshot the server sends back with the join.
  Future<Map<String, dynamic>?> joinAuction(String auctionId) async {
    _joinedAuctions.add(auctionId);

    if (_current != HubStatus.connected) return null;

    try {
      final result = await _connection?.invoke('JoinAuction', args: [auctionId]);
      return result is Map ? Map<String, dynamic>.from(result) : null;
    } catch (_) {
      return null;
    }
  }

  Future<void> leaveAuction(String auctionId) async {
    _joinedAuctions.remove(auctionId);
    try {
      await _connection?.invoke('LeaveAuction', args: [auctionId]);
    } catch (_) {
      // Leaving is best-effort; the server drops the group when the connection ends anyway.
    }
  }

  Future<void> disconnect() async {
    _joinedAuctions.clear();
    try {
      await _connection?.stop();
    } catch (_) {
      // Already gone.
    }
    _connection = null;
    _setStatus(HubStatus.disconnected);
  }

  void dispose() {
    _events.close();
    _status.close();
    _connection?.stop();
  }
}
