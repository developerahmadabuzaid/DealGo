import '../../../core/network/api_client.dart';

double _num(dynamic v) => (v as num?)?.toDouble() ?? 0;

class PrizeClaim {
  const PrizeClaim({
    required this.id,
    required this.auctionId,
    required this.auctionTitle,
    required this.rank,
    required this.prizeName,
    required this.prizeImageUrl,
    required this.paymentAmount,
    required this.currency,
    required this.status,
    required this.paymentDeadlineAt,
    this.prizeOriginalPrice = 0,
    this.prizeDescription,
  });

  final String id;
  final String auctionId;
  final String auctionTitle;
  final int rank;
  final String prizeName;
  final String? prizeImageUrl;
  final double paymentAmount;
  final String currency;
  final String status;
  final DateTime? paymentDeadlineAt;

  /// The prize's full price. [paymentAmount] is that price after the auction's discount.
  final double prizeOriginalPrice;
  final String? prizeDescription;

  /// The discount the winner got, 0..1. Zero when the server sent no full price to compare with.
  double get discount => prizeOriginalPrice > paymentAmount && prizeOriginalPrice > 0
      ? 1 - paymentAmount / prizeOriginalPrice
      : 0;

  bool get needsPayment => status == 'PaymentRequired' || status == 'Pending';
  bool get isPaid => status == 'Paid' || status == 'Claimed' || status == 'Fulfilled';

  factory PrizeClaim.fromJson(Map<String, dynamic> j) => PrizeClaim(
        id: j['id'] as String,
        auctionId: j['auctionId'] as String? ?? '',
        auctionTitle: j['auctionTitle'] as String? ?? '',
        rank: j['rank'] as int? ?? 0,
        prizeName: j['prizeName'] as String? ?? '',
        prizeImageUrl: j['prizeImageUrl'] as String?,
        paymentAmount: _num(j['paymentAmount']),
        currency: j['currency'] as String? ?? 'USD',
        status: j['status'] as String? ?? '',
        paymentDeadlineAt: j['paymentDeadlineAt'] == null
            ? null
            : DateTime.parse(j['paymentDeadlineAt'] as String).toUtc(),
        prizeOriginalPrice: _num(j['prizeOriginalPrice']),
        prizeDescription: j['prizeDescription'] as String?,
      );
}

class MyWin {
  const MyWin({
    required this.auctionId,
    required this.auctionTitle,
    required this.rank,
    required this.highestPrice,
    required this.prizeName,
    required this.prizeImageUrl,
    required this.prizeValue,
    required this.currency,
    required this.prizeClaimId,
    required this.claimStatus,
  });

  final String auctionId;
  final String auctionTitle;
  final int rank;
  final double highestPrice;
  final String prizeName;
  final String? prizeImageUrl;
  final double prizeValue;
  final String currency;
  final String? prizeClaimId;
  final String? claimStatus;

  factory MyWin.fromJson(Map<String, dynamic> j) => MyWin(
        auctionId: j['auctionId'] as String? ?? '',
        auctionTitle: j['auctionTitle'] as String? ?? '',
        rank: j['rank'] as int? ?? 0,
        highestPrice: _num(j['highestPrice']),
        prizeName: j['prizeName'] as String? ?? '',
        prizeImageUrl: j['prizeImageUrl'] as String?,
        prizeValue: _num(j['prizeValue']),
        currency: j['currency'] as String? ?? 'USD',
        prizeClaimId: j['prizeClaimId'] as String?,
        claimStatus: j['claimStatus'] as String?,
      );
}

class AppNotification {
  const AppNotification({
    required this.id,
    required this.type,
    required this.title,
    required this.body,
    required this.readAt,
    required this.createdAt,
  });

  final String id;
  final String type;
  final String title;
  final String body;
  final DateTime? readAt;
  final DateTime createdAt;

  bool get isUnread => readAt == null;

  factory AppNotification.fromJson(Map<String, dynamic> j) => AppNotification(
        id: j['id'] as String,
        type: j['type'] as String? ?? '',
        title: j['title'] as String? ?? '',
        body: j['body'] as String? ?? '',
        readAt: j['readAt'] == null ? null : DateTime.parse(j['readAt'] as String).toUtc(),
        createdAt: DateTime.parse(j['createdAt'] as String).toUtc(),
      );
}

class PrizeRepository {
  PrizeRepository(this._api);

  final ApiClient _api;

  Future<Paged<MyWin>> myWins({int page = 1}) async {
    final data = await _api.get('/me/wins', query: {'page': page, 'pageSize': 20});
    return Paged.fromJson(data as Map<String, dynamic>, MyWin.fromJson);
  }

  Future<Paged<PrizeClaim>> myClaims({int page = 1}) async {
    final data = await _api.get('/me/prize-claims', query: {'page': page, 'pageSize': 20});
    return Paged.fromJson(data as Map<String, dynamic>, PrizeClaim.fromJson);
  }

  /// Prize payments come from the Main wallet; shopping credit is not accepted.
  Future<PrizeClaim> pay(String claimId, String idempotencyKey) async {
    final data = await _api.post('/prize-claims/$claimId/pay',
        body: {'idempotencyKey': idempotencyKey, 'shippingAddressId': null});
    return PrizeClaim.fromJson(data as Map<String, dynamic>);
  }

  Future<PrizeClaim> claim(String claimId) async {
    final data = await _api.post('/prize-claims/$claimId/claim', body: {'shippingAddressId': null});
    return PrizeClaim.fromJson(data as Map<String, dynamic>);
  }
}

class NotificationRepository {
  NotificationRepository(this._api);

  final ApiClient _api;

  Future<Paged<AppNotification>> list({int page = 1}) async {
    final data = await _api.get('/notifications', query: {'page': page, 'pageSize': 30});
    return Paged.fromJson(data as Map<String, dynamic>, AppNotification.fromJson);
  }

  Future<int> unreadCount() async {
    final data = await _api.get('/notifications/unread-count');
    return (data as num?)?.toInt() ?? 0;
  }

  Future<void> markRead(String id) => _api.post('/notifications/$id/read');

  Future<void> markAllRead() => _api.post('/notifications/read-all');
}
