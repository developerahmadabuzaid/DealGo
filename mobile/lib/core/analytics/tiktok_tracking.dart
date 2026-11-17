import 'dart:async';

import 'package:app_tracking_transparency/app_tracking_transparency.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:tiktok_events_sdk/tiktok_events_sdk.dart';

/// TikTok's keys for this app.
///
/// The app IDs are public (they are in every copy of the app). The app secret is not committed:
/// the repository is public. It is given at build time, the same way as the API address:
///
///   flutter build appbundle --release --dart-define=TIKTOK_APP_SECRET=...
///
/// On Codemagic it comes from the secure variable of the same name (see codemagic.yaml). A build
/// without the secret, a web build, and the tests have no keys, and every call here does nothing.
class TikTokKeys {
  const TikTokKeys({
    required this.iosAppId,
    required this.iosTikTokAppId,
    required this.androidAppId,
    required this.androidTikTokAppId,
    required this.appSecret,
    required this.androidAppSecret,
    this.debug = false,
  });

  /// The keys given to this build.
  factory TikTokKeys.fromEnvironment() => const TikTokKeys(
        iosAppId: '6828769870',
        iosTikTokAppId: String.fromEnvironment('TIKTOK_IOS_APP_ID', defaultValue: '7708838750298456085'),
        androidAppId: 'com.dealgosa.app',
        // TikTok gives the Android app its own ID when it is added in Events Manager.
        androidTikTokAppId: String.fromEnvironment('TIKTOK_ANDROID_APP_ID'),
        appSecret: String.fromEnvironment('TIKTOK_APP_SECRET'),
        // Only needed if TikTok gave the Android app a different secret.
        androidAppSecret: String.fromEnvironment('TIKTOK_ANDROID_APP_SECRET'),
        // Sends to Events Manager's "Test events" tab instead of the live data.
        debug: bool.fromEnvironment('TIKTOK_DEBUG'),
      );

  /// No keys at all, for tests.
  static const none = TikTokKeys(
    iosAppId: '',
    iosTikTokAppId: '',
    androidAppId: '',
    androidTikTokAppId: '',
    appSecret: '',
    androidAppSecret: '',
  );

  /// The Apple ID of the app (App Store Connect > App Information).
  final String iosAppId;
  final String iosTikTokAppId;

  /// The Android package name.
  final String androidAppId;
  final String androidTikTokAppId;
  final String appSecret;
  final String androidAppSecret;
  final bool debug;

  String get secretForAndroid => androidAppSecret.isNotEmpty ? androidAppSecret : appSecret;

  /// Whether there is enough to start TikTok on [platform].
  bool readyFor(TargetPlatform platform) => switch (platform) {
        TargetPlatform.iOS => iosAppId.isNotEmpty && iosTikTokAppId.isNotEmpty && appSecret.isNotEmpty,
        TargetPlatform.android => androidAppId.isNotEmpty &&
            androidTikTokAppId.isNotEmpty &&
            secretForAndroid.isNotEmpty,
        _ => false,
      };
}

/// What the tracking needs from the TikTok SDK and the iPhone's tracking question; replaced in
/// tests.
abstract class TikTokBridge {
  /// Asks the iPhone's App Tracking Transparency question if it has not been answered, and
  /// returns whether tracking is allowed.
  Future<bool> askTrackingPermission();

  Future<void> start(TikTokKeys keys, {required bool trackingAllowed});

  Future<void> identify(String externalId);

  Future<void> logout();

  Future<void> send(TikTokEvent event);
}

/// The real bridge: the tiktok_events_sdk and app_tracking_transparency plugins.
class PluginTikTokBridge implements TikTokBridge {
  const PluginTikTokBridge();

  @override
  Future<bool> askTrackingPermission() async {
    var status = await AppTrackingTransparency.trackingAuthorizationStatus;
    if (status == TrackingStatus.notDetermined) {
      status = await AppTrackingTransparency.requestTrackingAuthorization();
    }
    return status == TrackingStatus.authorized;
  }

  @override
  Future<void> start(TikTokKeys keys, {required bool trackingAllowed}) => TikTokEventsSdk.initSdk(
        androidAppId: keys.androidAppId,
        tikTokAndroidId: keys.androidTikTokAppId,
        iosAppId: keys.iosAppId,
        tiktokIosId: keys.iosTikTokAppId,
        isDebugMode: keys.debug,
        logLevel: keys.debug ? TikTokLogLevel.debug : TikTokLogLevel.warn,
        androidOptions: TikTokAndroidOptions(appSecret: keys.secretForAndroid),
        iosOptions: TikTokIosOptions(
          accessToken: keys.appSecret,
          // The app has already asked the tracking question itself, at a moment of its choosing;
          // the SDK must not ask again at start-up. The plugin requires the answer and its time.
          displayAtt: false,
          disableAppTrackingDialog: true,
          externalConsentStatus: trackingAllowed ? 'granted' : 'denied',
          externalConsentTimestamp: DateTime.now().toUtc().toIso8601String(),
        ),
      );

  @override
  Future<void> identify(String externalId) =>
      TikTokEventsSdk.identify(identifier: TikTokIdentifier(externalId: externalId));

  @override
  Future<void> logout() => TikTokEventsSdk.logout();

  @override
  Future<void> send(TikTokEvent event) => TikTokEventsSdk.logEvent(event: event);
}

final tiktokTrackingProvider = Provider<TikTokTracking>((ref) => TikTokTracking());

/// TikTok app events, so TikTok can tell which of its ads brought a customer.
///
/// Measuring ads must never get in the way of the app: every call returns at once, runs in the
/// background, and swallows any failure. Without keys (see [TikTokKeys]) or on the web nothing is
/// done at all, not even loading the plugin.
///
/// Install, launch and retention are reported by TikTok's SDK itself once it starts, so there is no
/// launch event here: sending one as well would count every launch twice.
class TikTokTracking {
  TikTokTracking({TikTokKeys? keys, TikTokBridge? bridge, TargetPlatform? platform, bool? isWeb})
      : _keys = keys ?? TikTokKeys.fromEnvironment(),
        _bridge = bridge ?? const PluginTikTokBridge(),
        _platform = platform ?? defaultTargetPlatform,
        _isWeb = isWeb ?? kIsWeb;

  final TikTokKeys _keys;
  final TikTokBridge _bridge;
  final TargetPlatform _platform;
  final bool _isWeb;

  /// How long the app is on screen before the tracking question is asked, so it is never the first
  /// thing someone sees.
  static const startDelay = Duration(seconds: 3);

  Timer? _timer;
  Future<bool>? _started;
  bool _trackingAllowed = false;

  /// Whether this build reports to TikTok at all.
  bool get isEnabled => !_isWeb && _keys.readyFor(_platform);

  /// Starts TikTok a moment after the app is on screen. Called from the app's main page every time
  /// it is built; only the first call does anything.
  void startSoon() {
    if (!isEnabled || _timer != null || _started != null) return;
    _timer = Timer(startDelay, () => unawaited(start()));
  }

  /// Asks the iPhone's tracking question (once; iOS remembers the answer), then starts TikTok.
  /// Completes with whether it started.
  Future<bool> start() {
    if (!isEnabled) return Future.value(false);
    _timer?.cancel();
    final existing = _started;
    if (existing != null) return existing;

    final started = _started = _start();
    for (final action in _pending) {
      _run(started, action);
    }
    _pending.clear();
    return started;
  }

  Future<bool> _start() async {
    try {
      if (_platform == TargetPlatform.iOS) {
        _trackingAllowed = await _bridge.askTrackingPermission();
      } else {
        // Android has no such question; the advertising ID follows the phone's own ad settings.
        _trackingAllowed = true;
      }
      await _bridge.start(_keys, trackingAllowed: _trackingAllowed);
      return true;
    } catch (e) {
      debugPrint('TikTok did not start: $e');
      return false;
    }
  }

  /// What happened before TikTok started (a restored sign-in, say), sent once it has.
  final _pending = <Future<void> Function()>[];

  static const _maxPending = 20;

  /// Runs [action] once TikTok has started; dropped when it never does.
  void _whenStarted(Future<void> Function() action) {
    if (!isEnabled) return;
    final started = _started;
    if (started == null) {
      if (_pending.length < _maxPending) _pending.add(action);
      return;
    }
    _run(started, action);
  }

  void _run(Future<bool> started, Future<void> Function() action) {
    unawaited(() async {
      try {
        if (await started) await action();
      } catch (e) {
        debugPrint('TikTok event not sent: $e');
      }
    }());
  }

  void _event(String name, {TTEventType type = TTEventType.none, EventProperties? properties, String? id}) {
    _whenStarted(() => _bridge.send(
          TikTokEvent(eventName: name, eventType: type, properties: properties, eventId: id),
        ));
  }

  /// Ties later events to this account, by its ID only: no phone number or email is given to TikTok.
  /// Skipped on an iPhone whose owner said no to tracking.
  void identify(String userId) {
    if (userId.isEmpty) return;
    _whenStarted(() async {
      if (_trackingAllowed) await _bridge.identify(userId);
    });
  }

  void logout() => _whenStarted(_bridge.logout);

  /// A new account, once it has its name.
  void registration() => _event('Registration');

  /// An existing account signed in again.
  void login() => _event('Login');

  void search(String query) {
    final q = query.trim();
    if (q.isEmpty) return;
    _event('Search', properties: EventProperties(customProperties: {'query': q}));
  }

  void viewContent({required String productId, required String name, required double price, String? currency}) =>
      _event('ViewContent',
          type: TTEventType.viewContent,
          properties: EventProperties(
            contentId: productId,
            contentName: name,
            contentType: 'product',
            price: price,
            value: price,
            currency: _currency(currency),
          ));

  void addToCart({
    required String productId,
    required String name,
    required double price,
    required int quantity,
    String? currency,
  }) =>
      _event('AddToCart',
          type: TTEventType.addToCart,
          properties: EventProperties(
            contentId: productId,
            contentName: name,
            contentType: 'product',
            price: price,
            quantity: quantity,
            value: price * quantity,
            currency: _currency(currency),
          ));

  /// The customer pressed the checkout button.
  void checkout({required double value, String? currency, int? quantity}) => _event('Checkout',
      type: TTEventType.checkout,
      properties: EventProperties(value: value, currency: _currency(currency), quantity: quantity));

  /// A store order the customer has paid for. [orderId] keeps a retried report from counting twice.
  void purchase({required String orderId, required double value, String? currency, int? quantity}) =>
      _event('Purchase',
          type: TTEventType.purchase,
          id: orderId,
          properties: EventProperties(
            value: value,
            currency: _currency(currency),
            quantity: quantity,
            contentType: 'product',
            customProperties: {'order_id': orderId},
          ));

  /// The store prices in riyals; anything TikTok does not know is reported as riyals too.
  static CurrencyCode _currency(String? code) {
    final upper = code?.trim().toUpperCase();
    for (final c in CurrencyCode.values) {
      if (c.name == upper) return c;
    }
    return CurrencyCode.SAR;
  }
}
