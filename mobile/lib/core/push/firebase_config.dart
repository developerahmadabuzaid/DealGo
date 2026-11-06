import 'package:firebase_core/firebase_core.dart';
import 'package:flutter/foundation.dart';

/// The app's Firebase project, used only for push notifications.
///
/// These values are not secrets: Firebase puts them inside every copy of the app, and they only
/// say which project the phone belongs to. Sending a notification needs the server's service
/// account, which never leaves the server.
///
/// Copy them from the Firebase console: Project settings > General > Your apps. Each platform has
/// its own app id and API key; the sender id and project id are the project's. While they are
/// empty the app simply runs without push.
abstract final class FirebaseConfig {
  static const projectId = 'dealgo-121bf';
  static const messagingSenderId = '420004607897';

  static const androidApiKey = 'AIzaSyDwzoLnxqiLEis90Tkop5Z7jLcDRG0sN9o';
  static const androidAppId = '1:420004607897:android:40e0d293dc3970687a8508';

  static const iosApiKey = 'AIzaSyAp90uVCgVm5vEy2Irrgu8omfwIyFYfe68';
  static const iosAppId = '1:420004607897:ios:31adfd07462dcce37a8508';
  static const iosBundleId = 'com.dealgosa.app';

  /// Push is for the installed apps; the browser version keeps its in-app list only.
  static bool get isConfigured {
    if (kIsWeb || projectId.isEmpty || messagingSenderId.isEmpty) return false;
    return switch (defaultTargetPlatform) {
      TargetPlatform.android => androidApiKey.isNotEmpty && androidAppId.isNotEmpty,
      TargetPlatform.iOS => iosApiKey.isNotEmpty && iosAppId.isNotEmpty,
      _ => false,
    };
  }

  static FirebaseOptions get options => defaultTargetPlatform == TargetPlatform.iOS
      ? const FirebaseOptions(
          apiKey: iosApiKey,
          appId: iosAppId,
          messagingSenderId: messagingSenderId,
          projectId: projectId,
          iosBundleId: iosBundleId,
        )
      : const FirebaseOptions(
          apiKey: androidApiKey,
          appId: androidAppId,
          messagingSenderId: messagingSenderId,
          projectId: projectId,
        );
}
