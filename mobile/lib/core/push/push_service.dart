import 'dart:async';

import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../providers.dart';
import 'firebase_config.dart';

/// Shows a short message whatever page is open; used for a notification that arrives while the
/// app is on screen, which Android does not draw itself.
final rootMessengerKey = GlobalKey<ScaffoldMessengerState>();

final pushServiceProvider = Provider<PushService>((ref) => PushService(ref));

/// Push notifications on the phone: asks permission, tells the server where to reach this phone,
/// and opens the right page when a notification is tapped.
///
/// The notifications themselves are written by the server, which also keeps them in the in-app
/// list, so nothing here is needed for a notification to exist; push only wakes the phone. Every
/// step therefore fails quietly: a phone that refuses permission, has no Google services, or is
/// offline still has the list.
class PushService {
  PushService(this._ref);

  final Ref _ref;

  bool _initialised = false;
  String? _token;
  final _subscriptions = <StreamSubscription<dynamic>>[];

  /// Where a tapped notification leads; set by the app once its router exists.
  void Function(String location)? _open;

  /// A notification tapped while the app was closed, held until the router is ready.
  String? _pendingLocation;

  set open(void Function(String location)? value) {
    _open = value;
    final location = _pendingLocation;
    if (value != null && location != null) {
      _pendingLocation = null;
      // After this frame: the router is being built when it hands this over.
      WidgetsBinding.instance.addPostFrameCallback((_) => value(location));
    }
  }

  /// Called once somebody is signed in.
  Future<void> start() async {
    if (!FirebaseConfig.isConfigured) return;

    try {
      if (!_initialised) {
        if (Firebase.apps.isEmpty) {
          await Firebase.initializeApp(options: FirebaseConfig.options);
        }
        _listen();
        _initialised = true;

        final initial = await FirebaseMessaging.instance.getInitialMessage();
        if (initial != null) _openFor(initial);
      }

      final messaging = FirebaseMessaging.instance;
      final settings = await messaging.requestPermission();
      if (settings.authorizationStatus == AuthorizationStatus.denied) return;

      // The iPhone shows the banner itself even while the app is open.
      await messaging.setForegroundNotificationPresentationOptions(
          alert: true, badge: true, sound: true);

      // On an iPhone the Firebase token only exists once Apple has handed over its own.
      if (defaultTargetPlatform == TargetPlatform.iOS) {
        for (var i = 0; i < 10 && await messaging.getAPNSToken() == null; i++) {
          await Future<void>.delayed(const Duration(seconds: 1));
        }
      }

      // The classic registration token, which the server sends to through FCM's HTTP v1 API. The
      // newer register() hands out an installation id meant for a different way of sending, so
      // the deprecated calls stay until the server moves too.
      // ignore: deprecated_member_use
      final token = await messaging.getToken();
      if (token != null) await _register(token);
    } catch (e) {
      debugPrint('Push is not available: $e');
    }
  }

  /// Called before signing out, while the session can still tell the server to forget this phone.
  Future<void> stop() async {
    final token = _token;
    _token = null;
    if (token == null) return;

    try {
      await _ref.read(notificationRepositoryProvider).unregisterDevice(token);
    } catch (_) {
      // The server also forgets a token once Firebase reports it gone.
    }
    try {
      // ignore: deprecated_member_use
      await FirebaseMessaging.instance.deleteToken();
    } catch (_) {}
  }

  void _listen() {
    _subscriptions
      // ignore: deprecated_member_use
      ..add(FirebaseMessaging.instance.onTokenRefresh.listen((t) => unawaited(_register(t))))
      ..add(FirebaseMessaging.onMessageOpenedApp.listen(_openFor))
      ..add(FirebaseMessaging.onMessage.listen(_onForeground));
  }

  Future<void> _register(String token) async {
    try {
      await _ref.read(notificationRepositoryProvider).registerDevice(
            token,
            defaultTargetPlatform == TargetPlatform.iOS ? 'ios' : 'android',
          );
      _token = token;
    } catch (e) {
      debugPrint('Could not register for push: $e');
    }
  }

  void _onForeground(RemoteMessage message) {
    _ref.invalidate(unreadCountProvider);
    _ref.invalidate(notificationsProvider);

    // The iPhone has already shown the banner; Android shows nothing for an open app.
    if (defaultTargetPlatform != TargetPlatform.android) return;
    final n = message.notification;
    if (n == null) return;

    rootMessengerKey.currentState?.showSnackBar(SnackBar(
      content: Text([n.title, n.body].whereType<String>().join('\n')),
      action: SnackBarAction(label: 'عرض', onPressed: () => _openFor(message)),
    ));
  }

  void _openFor(RemoteMessage message) {
    unawaited(_markRead(message.data['notificationId']));

    final location = locationFor(message.data);
    final open = _open;
    if (open != null) {
      open(location);
    } else {
      _pendingLocation = location;
    }
  }

  /// Opening a notification from the phone counts as reading it, as tapping it in the list does.
  Future<void> _markRead(Object? id) async {
    try {
      if (id is String && id.isNotEmpty) {
        await _ref.read(notificationRepositoryProvider).markRead(id);
      }
    } catch (_) {
      // Only the unread dot is affected.
    }
    _ref.invalidate(unreadCountProvider);
    _ref.invalidate(notificationsProvider);
  }

  /// Where a tapped push leads: the page it is about, or the list for a plain message.
  static String locationFor(Map<String, dynamic> data) => pageFor(data) ?? '/notifications';

  /// The page a notification is about, or null when it is only a message (an announcement).
  static String? pageFor(Map<String, dynamic> data) {
    String? value(String key) {
      final v = data[key];
      return v is String && v.isNotEmpty ? v : null;
    }

    switch (value('type')) {
      // A prize won, or one waiting to be paid for or claimed: the prizes page.
      case 'YOU_WON':
      case 'PRIZE_CLAIM_AVAILABLE':
      case 'PRIZE_PAYMENT_REMINDER':
        return '/prizes';
      // Money back or shopping credit: the wallet shows the new balance.
      case 'SHOPPING_CREDIT_ADDED':
      case 'SHOPPING_CREDIT_EXPIRING':
      case 'ENTRY_FEE_REFUNDED':
        return '/wallet';
    }

    if (value('orderId') != null) return '/store/orders';
    if (value('prizeClaimId') != null) return '/prizes';

    final auctionId = value('auctionId');
    if (auctionId != null) return '/auctions/$auctionId';

    return null;
  }

  /// The tabs live in the app's shell: opening one switches tab; any other page goes on top.
  static const _tabs = {'/', '/auctions', '/store', '/wallet', '/profile'};

  static void openIn(GoRouter router, String location) {
    if (_tabs.contains(location)) {
      router.go(location);
    } else {
      router.push(location);
    }
  }
}
