import '../../../core/network/api_client.dart';
import 'auction_models.dart';

/// REST access to the auction endpoints.
///
/// Note what the client sends when bidding: only an idempotency key. Price, sequence number and
/// timestamp are all decided by the server.
class AuctionRepository {
  AuctionRepository(this._api);

  final ApiClient _api;

  Future<Paged<AuctionSummary>> list({
    String? group,
    String? search,
    bool? featured,
    int page = 1,
    int pageSize = 20,
  }) async {
    final data = await _api.get('/auctions', query: {
      'group': group,
      'search': search,
      // Only ever sent as true; the server treats a missing value as "all auctions".
      'featured': featured == true ? true : null,
      'page': page,
      'pageSize': pageSize,
    });

    return Paged.fromJson(data as Map<String, dynamic>, AuctionSummary.fromJson);
  }

  Future<AuctionDetail> detail(String id) async {
    final data = await _api.get('/auctions/$id');
    return AuctionDetail.fromJson(data as Map<String, dynamic>);
  }

  /// The resync payload. Called on reconnect instead of trying to replay missed events.
  Future<AuctionSnapshot> snapshot(String id) async {
    final data = await _api.get('/auctions/$id/snapshot');
    return AuctionSnapshot.fromJson(data as Map<String, dynamic>);
  }

  Future<List<LeaderboardEntry>> leaderboard(String id, {int take = 10}) async {
    final data = await _api.get('/auctions/$id/leaderboard', query: {'take': take});
    return (data as List).map((e) => LeaderboardEntry.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<List<AuctionWinner>> winners(String id) async {
    final data = await _api.get('/auctions/$id/winners');
    return (data as List).map((e) => AuctionWinner.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<MyParticipation> join(String id, String idempotencyKey) async {
    final data = await _api.post('/auctions/$id/join', body: {'idempotencyKey': idempotencyKey});
    return MyParticipation.fromJson(data as Map<String, dynamic>);
  }

  Future<MyParticipation?> myParticipation(String id) async {
    try {
      final data = await _api.get('/auctions/$id/my-participation');
      return MyParticipation.fromJson(data as Map<String, dynamic>);
    } catch (_) {
      return null;
    }
  }

  /// Places one bid. [idempotencyKey] makes a retry safe: the server returns the original result.
  Future<BidResult> placeBid(String auctionId, String idempotencyKey) async {
    final data = await _api.post('/auctions/$auctionId/bids',
        body: {'idempotencyKey': idempotencyKey});
    return BidResult.fromJson(data as Map<String, dynamic>);
  }
}
