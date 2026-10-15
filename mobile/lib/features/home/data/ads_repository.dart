import '../../../core/network/api_client.dart';

/// Where on the front page an advertisement belongs. The names come from the server.
class AdPlacement {
  static const strip = 'HomeStrip';
  static const slideshow = 'HomeSlideshow';
  static const underCategories = 'HomeUnderCategories';
}

/// One advertisement an administrator set in the console.
class AppAd {
  const AppAd({
    required this.id,
    required this.placement,
    required this.imageUrl,
    required this.title,
    required this.note,
    required this.route,
  });

  final String id;
  final String placement;
  final String imageUrl;

  /// Written over the picture in the large slideshow; the two strips ignore both of these.
  final String? title;
  final String? note;

  /// Where a tap goes, worked out by the server. Null means the picture opens nothing.
  final String? route;

  /// An address outside the app, rather than a page inside it.
  bool get isExternal => route != null && (route!.startsWith('http://') || route!.startsWith('https://'));

  factory AppAd.fromJson(Map<String, dynamic> j) => AppAd(
        id: j['id'] as String,
        placement: j['placement'] as String? ?? AdPlacement.strip,
        imageUrl: j['imageUrl'] as String? ?? '',
        title: j['title'] as String?,
        note: j['note'] as String?,
        route: j['route'] as String?,
      );
}

class AdsRepository {
  AdsRepository(this._api);

  final ApiClient _api;

  /// Everything the server is advertising right now, already in the order it should be shown.
  Future<List<AppAd>> list() async {
    final data = await _api.get('/ads');
    return ((data as List?) ?? const [])
        .map((e) => AppAd.fromJson(e as Map<String, dynamic>))
        .where((ad) => ad.imageUrl.isNotEmpty)
        .toList();
  }
}
