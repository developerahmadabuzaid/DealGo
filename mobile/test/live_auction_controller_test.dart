import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/network/api_client.dart';
import 'package:mobile/core/providers.dart';
import 'package:mobile/core/realtime/auction_hub_client.dart';
import 'package:mobile/core/storage/token_store.dart';
import 'package:mobile/features/auctions/data/auction_models.dart';
import 'package:mobile/features/auctions/data/auction_repository.dart';
import 'package:mobile/features/auctions/presentation/live_auction_controller.dart';

/// A hub that is already connected when the screen opens, as it is after sign-in. Its status
/// stream stays silent, because nothing changes.
class _AlreadyConnectedHub extends AuctionHubClient {
  _AlreadyConnectedHub() : super(accessTokenProvider: () async => null);

  @override
  HubStatus get currentStatus => HubStatus.connected;
  @override
  Stream<HubStatus> get status => const Stream.empty();
  @override
  Stream<HubEvent> get events => const Stream.empty();
  @override
  Future<void> connect() async {}
  @override
  Future<Map<String, dynamic>?> joinAuction(String auctionId) async => null;
  @override
  Future<void> leaveAuction(String auctionId) async {}
}

class _Repo extends AuctionRepository {
  _Repo() : super(ApiClient(tokenStore: TokenStore()));

  int snapshots = 0;

  @override
  Future<AuctionSnapshot> snapshot(String id) async {
    snapshots++;
    return AuctionSnapshot.fromJson({
      'id': id,
      'status': 'Live',
      'currentPrice': 1500,
      'targetPrice': 2500,
      'myParticipantId': 'me',
      'serverTime': DateTime.now().toUtc().toIso8601String(),
    });
  }

  @override
  Future<AuctionDetail> detail(String id) async => throw Exception('not needed');

  @override
  Future<BidResult> placeBid(String auctionId, String idempotencyKey) async =>
      BidResult.fromJson({
        'bidId': 'b1',
        'sequenceNumber': 7,
        'previousPrice': 1500,
        'newPrice': 1501,
        'targetPrice': 2500,
        'reachedTarget': false,
        'finalPhase': false,
        'auctionStatus': 'Live',
        'myRank': 1,
        'myHighestPrice': 1501,
        'wasDuplicate': false,
      });
}

/// A hub that counts how often anything tries to connect it.
class _CountingHub extends _AlreadyConnectedHub {
  int connects = 0;

  @override
  HubStatus get currentStatus => HubStatus.disconnected;
  @override
  Future<void> connect() async => connects++;
}

final _signedIn = authControllerProvider.overrideWith((ref) => AuthController.withState(
    ref, const AuthState(checked: true, hasSession: true)));

final _guest = authControllerProvider
    .overrideWith((ref) => AuthController.withState(ref, const AuthState(checked: true)));

Future<ProviderContainer> _openScreen(_Repo repo) async {
  final container = ProviderContainer(overrides: [
    _signedIn,
    hubClientProvider.overrideWithValue(_AlreadyConnectedHub()),
    auctionRepositoryProvider.overrideWithValue(repo),
  ]);
  container.listen(liveAuctionProvider('a1'), (_, _) {});
  for (var i = 0; i < 20 && container.read(liveAuctionProvider('a1')).snapshot == null; i++) {
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
  return container;
}

void main() {
  test('a bid frees the button from its own answer, without a second request', () async {
    final repo = _Repo();
    final container = await _openScreen(repo);
    final before = repo.snapshots;

    await container.read(liveAuctionProvider('a1').notifier).placeBid();

    final state = container.read(liveAuctionProvider('a1'));
    expect(state.isBidding, isFalse);
    expect(state.snapshot!.currentPrice, 1501);
    expect(state.snapshot!.myRank, 1);
    expect(state.snapshot!.myHighestPrice, 1501);
    expect(state.snapshot!.lastBidIsMine, isTrue,
        reason: 'the button stays off until someone else bids, as the server requires');
    expect(repo.snapshots, before, reason: 'every bidder refetching after every bid doubled the load');

    container.dispose();
  });

  test('a screen opened on an already-connected hub can bid', () async {
    final container = ProviderContainer(overrides: [
      _signedIn,
      hubClientProvider.overrideWithValue(_AlreadyConnectedHub()),
      auctionRepositoryProvider.overrideWithValue(_Repo()),
    ]);
    final sub = container.listen(liveAuctionProvider('a1'), (_, _) {});

    // Let the controller finish starting up.
    for (var i = 0; i < 20 && container.read(liveAuctionProvider('a1')).snapshot == null; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 10));
    }

    final state = container.read(liveAuctionProvider('a1'));
    expect(state.isConnected, isTrue,
        reason: 'the hub was connected before the screen opened; the screen must know that');
    expect(state.canBid, isTrue);

    sub.close();
    container.dispose();
  });

  test('a guest watches the price without a live connection the server would refuse', () async {
    final hub = _CountingHub();
    final container = ProviderContainer(overrides: [
      _guest,
      hubClientProvider.overrideWithValue(hub),
      auctionRepositoryProvider.overrideWithValue(_Repo()),
    ]);
    final sub = container.listen(liveAuctionProvider('a1'), (_, _) {});

    for (var i = 0; i < 20 && container.read(liveAuctionProvider('a1')).snapshot == null; i++) {
      await Future<void>.delayed(const Duration(milliseconds: 10));
    }

    expect(container.read(liveAuctionProvider('a1')).snapshot!.currentPrice, 1500);
    expect(hub.connects, 0);

    sub.close();
    container.dispose();
  });
}
