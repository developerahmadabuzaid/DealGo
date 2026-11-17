import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/analytics/tiktok_tracking.dart';
import 'package:tiktok_events_sdk/tiktok_events_sdk.dart';

class _FakeBridge implements TikTokBridge {
  _FakeBridge({this.allowTracking = true, this.failing = false});

  final bool allowTracking;
  final bool failing;
  final calls = <String>[];
  final events = <TikTokEvent>[];
  bool? startedWithTracking;

  void _maybeFail() {
    if (failing) throw StateError('native SDK unavailable');
  }

  @override
  Future<bool> askTrackingPermission() async {
    calls.add('ask');
    return allowTracking;
  }

  @override
  Future<void> start(TikTokKeys keys, {required bool trackingAllowed}) async {
    calls.add('start');
    startedWithTracking = trackingAllowed;
    _maybeFail();
  }

  @override
  Future<void> identify(String externalId) async {
    calls.add('identify:$externalId');
    _maybeFail();
  }

  @override
  Future<void> logout() async {
    calls.add('logout');
    _maybeFail();
  }

  @override
  Future<void> send(TikTokEvent event) async {
    calls.add('event:${event.eventName}');
    events.add(event);
    _maybeFail();
  }
}

const _keys = TikTokKeys(
  iosAppId: '1',
  iosTikTokAppId: '2',
  androidAppId: 'com.example',
  androidTikTokAppId: '3',
  appSecret: 'test-secret',
  androidAppSecret: '',
);

void _everything(TikTokTracking t) {
  t.identify('user-1');
  t.login();
  t.registration();
  t.search('ساعة');
  t.viewContent(productId: 'p1', name: 'Watch', price: 100);
  t.addToCart(productId: 'p1', name: 'Watch', price: 100, quantity: 2);
  t.checkout(value: 200);
  t.purchase(orderId: 'o1', value: 200, currency: 'SAR');
  t.logout();
}

Future<void> _settle() => Future<void>.delayed(Duration.zero);

void main() {
  group('without keys', () {
    test('the build as tested has no TikTok keys, so it does nothing', () {
      expect(TikTokTracking().isEnabled, isFalse);
    });

    test('nothing reaches the SDK and nothing throws', () async {
      final bridge = _FakeBridge();
      final t = TikTokTracking(keys: TikTokKeys.none, bridge: bridge, platform: TargetPlatform.iOS);

      expect(t.isEnabled, isFalse);
      expect(await t.start(), isFalse);
      _everything(t);
      await _settle();

      expect(bridge.calls, isEmpty);
    });

    testWidgets('startSoon sets no timer', (tester) async {
      // A pending timer would fail this test when it ends.
      TikTokTracking(keys: TikTokKeys.none, bridge: _FakeBridge()).startSoon();
    });

    test('an app ID without the secret is not enough', () {
      const noSecret = TikTokKeys(
        iosAppId: '1',
        iosTikTokAppId: '2',
        androidAppId: 'com.example',
        androidTikTokAppId: '3',
        appSecret: '',
        androidAppSecret: '',
      );
      expect(noSecret.readyFor(TargetPlatform.iOS), isFalse);
      expect(noSecret.readyFor(TargetPlatform.android), isFalse);
    });

    test('the web build does nothing even with keys', () async {
      final bridge = _FakeBridge();
      final t = TikTokTracking(keys: _keys, bridge: bridge, platform: TargetPlatform.android, isWeb: true);

      expect(await t.start(), isFalse);
      _everything(t);
      await _settle();
      expect(bridge.calls, isEmpty);
    });
  });

  group('with keys', () {
    test('events from before the start are sent once it has started', () async {
      final bridge = _FakeBridge();
      final t = TikTokTracking(keys: _keys, bridge: bridge, platform: TargetPlatform.android);

      t.identify('user-1');
      t.login();
      await _settle();
      expect(bridge.calls, isEmpty);

      expect(await t.start(), isTrue);
      await _settle();
      expect(bridge.calls, ['start', 'identify:user-1', 'event:Login']);
    });

    test('the purchase carries its value in riyals and the order as its event ID', () async {
      final bridge = _FakeBridge();
      final t = TikTokTracking(keys: _keys, bridge: bridge, platform: TargetPlatform.android);
      await t.start();

      t.purchase(orderId: 'o1', value: 250.5, currency: 'sar', quantity: 3);
      await _settle();

      final purchase = bridge.events.single;
      expect(purchase.eventName, 'Purchase');
      expect(purchase.eventType, TTEventType.purchase);
      expect(purchase.eventId, 'o1');
      expect(purchase.properties!.value, 250.5);
      expect(purchase.properties!.currency, CurrencyCode.SAR);
    });

    test('an iPhone owner who says no to tracking is not identified', () async {
      final bridge = _FakeBridge(allowTracking: false);
      final t = TikTokTracking(keys: _keys, bridge: bridge, platform: TargetPlatform.iOS);

      await t.start();
      t.identify('user-1');
      t.login();
      await _settle();

      expect(bridge.calls, ['ask', 'start', 'event:Login']);
      expect(bridge.startedWithTracking, isFalse);
    });

    test('the tracking question is asked once', () async {
      final bridge = _FakeBridge();
      final t = TikTokTracking(keys: _keys, bridge: bridge, platform: TargetPlatform.iOS);

      await Future.wait([t.start(), t.start()]);
      await t.start();

      expect(bridge.calls.where((c) => c == 'ask'), hasLength(1));
      expect(bridge.calls.where((c) => c == 'start'), hasLength(1));
    });

    test('a failing SDK never reaches the app', () async {
      final bridge = _FakeBridge(failing: true);
      final t = TikTokTracking(keys: _keys, bridge: bridge, platform: TargetPlatform.android);

      expect(await t.start(), isFalse);
      _everything(t);
      await _settle();

      // It did not start, so nothing after the attempt was sent.
      expect(bridge.calls, ['start']);
    });
  });
}
