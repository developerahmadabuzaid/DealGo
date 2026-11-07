import '../../../core/network/api_client.dart';

/// Delivery by M5azn, the supplier that packs and ships some store products from its own
/// warehouse. Store orders only. Only present once the order has actually gone to M5azn.
class ShipmentTracking {
  const ShipmentTracking({
    required this.provider,
    required this.status,
    this.remoteStatus,
    this.trackingNumber,
    this.trackingUrl,
    this.carrier,
    this.submittedAt,
    this.shippedAt,
    this.deliveredAt,
    this.lastSyncedAt,
  });

  final String provider;

  /// Submitted, Processing, Shipped, Delivered or Cancelled.
  final String status;

  /// The supplier's own word for it, shown when ours is too coarse.
  final String? remoteStatus;
  final String? trackingNumber;
  final String? trackingUrl;
  final String? carrier;
  final DateTime? submittedAt;
  final DateTime? shippedAt;
  final DateTime? deliveredAt;
  final DateTime? lastSyncedAt;

  bool get isCancelled => status == 'Cancelled';
  bool get isDelivered => status == 'Delivered';

  /// How far along it is, 0 to 3, for the progress line.
  int get step => switch (status) {
        'Delivered' => 3,
        'Shipped' => 2,
        'Processing' => 1,
        _ => 0,
      };

  /// Only an http(s) address is ever opened.
  Uri? get trackingUri {
    final url = trackingUrl;
    if (url == null) return null;
    final uri = Uri.tryParse(url);
    return uri != null && (uri.scheme == 'https' || uri.scheme == 'http') ? uri : null;
  }

  factory ShipmentTracking.fromJson(Map<String, dynamic> j) => ShipmentTracking(
        provider: j['provider'] as String? ?? 'M5azn',
        status: j['status'] as String? ?? 'Submitted',
        remoteStatus: _text(j['remoteStatus']),
        trackingNumber: _text(j['trackingNumber']),
        trackingUrl: _text(j['trackingUrl']),
        carrier: _text(j['carrier']),
        submittedAt: _date(j['submittedAt']),
        shippedAt: _date(j['shippedAt']),
        deliveredAt: _date(j['deliveredAt']),
        lastSyncedAt: _date(j['lastSyncedAt']),
      );

  static String? _text(Object? v) {
    final s = v as String?;
    return s == null || s.trim().isEmpty ? null : s.trim();
  }

  static DateTime? _date(Object? v) => v is String ? DateTime.tryParse(v)?.toUtc() : null;
}

class ShipmentRepository {
  ShipmentRepository(this._api);

  final ApiClient _api;

  /// Null when the order is not delivered by M5azn, or not sent to it yet (the server answers 204).
  Future<ShipmentTracking?> forStoreOrder(String orderId) =>
      _read('/store/orders/$orderId/shipment');

  Future<ShipmentTracking?> _read(String path) async {
    final data = await _api.get(path);
    return data is Map<String, dynamic> ? ShipmentTracking.fromJson(data) : null;
  }
}
