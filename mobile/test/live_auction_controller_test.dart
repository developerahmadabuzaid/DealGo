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

  @override
  Future<AuctionSnapshot> snapshot(String id) async => AuctionSnapshot.fromJson({
        'id': id,
        'status': 'Live',
        'currentPrice': 1500,
        'targetPrice': 2500,
        'serverTime': DateTime.now().toUtc().toIso8601String(),
      });

  @override
  Future<AuctionDetail> detail(String id) async => throw Exception('not needed');
}

void main() {
  test('a screen opened on an already-connected hub can bid', () async {
    final container = ProviderContainer(overrides: [
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
}
