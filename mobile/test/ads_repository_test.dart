import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/network/api_client.dart';
import 'package:mobile/core/storage/token_store.dart';
import 'package:mobile/features/home/data/ads_repository.dart';

/// Answers one request with a fixed body, and remembers what was asked for.
class _Canned implements HttpClientAdapter {
  _Canned(this.body);

  final Object body;
  final requested = <String>[];

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? data, Future<void>? cancel) async {
    requested.add(options.path);
    return ResponseBody.fromString(
      jsonEncode(body),
      200,
      headers: {
        Headers.contentTypeHeader: ['application/json'],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

AdsRepository _repo(_Canned adapter) {
  final dio = Dio(BaseOptions(baseUrl: 'https://example.test/api/v1', validateStatus: (_) => true))
    ..httpClientAdapter = adapter;

  return AdsRepository(ApiClient(tokenStore: TokenStore(), dio: dio));
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  FlutterSecureStorage.setMockInitialValues({});

  test('the advertisements are read with their place, their words and where they go', () async {
    final adapter = _Canned([
      {
        'id': '1',
        'placement': 'HomeSlideshow',
        'imageUrl': '/uploads/a.png',
        'title': 'عرض الأسبوع',
        'note': 'وفّر حتى 60%',
        'targetKind': 'Auction',
        'route': '/auctions/7',
      },
      {
        'id': '2',
        'placement': 'HomeStrip',
        'imageUrl': '/uploads/b.png',
        'title': null,
        'note': null,
        'targetKind': 'None',
        'route': null,
      },
    ]);

    final ads = await _repo(adapter).list();

    expect(adapter.requested.single, '/ads');
    expect(ads.length, 2);

    expect(ads.first.placement, AdPlacement.slideshow);
    expect(ads.first.title, 'عرض الأسبوع');
    expect(ads.first.route, '/auctions/7');
    expect(ads.first.isExternal, isFalse);

    expect(ads[1].placement, AdPlacement.strip);
    expect(ads[1].title, isNull);
    expect(ads[1].route, isNull);
  });

  test('an address outside the app is marked as one, so it opens in the browser', () async {
    final ads = await _repo(_Canned([
      {
        'id': '3',
        'placement': 'HomeUnderCategories',
        'imageUrl': '/uploads/c.png',
        'targetKind': 'Url',
        'route': 'https://dealgosa.com/offers',
      },
    ])).list();

    expect(ads.single.placement, AdPlacement.underCategories);
    expect(ads.single.isExternal, isTrue);
  });

  test('an advertisement with no picture is left out rather than drawn as an empty box', () async {
    final ads = await _repo(_Canned([
      {'id': '4', 'placement': 'HomeStrip', 'imageUrl': '', 'targetKind': 'None'},
      {'id': '5', 'placement': 'HomeStrip', 'imageUrl': '/uploads/d.png', 'targetKind': 'None'},
    ])).list();

    expect(ads.single.id, '5');
  });
}
