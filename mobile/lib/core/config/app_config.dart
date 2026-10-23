import 'package:flutter/foundation.dart' show kIsWeb;

/// Build-time configuration.
///
/// Override at build time, e.g.
///   flutter run --dart-define=API_BASE_URL=https://api.example.com
class AppConfig {
  const AppConfig._();

  static const String _configuredApiBaseUrl = String.fromEnvironment('API_BASE_URL');

  /// The default server. For a local API, pass
  /// --dart-define=API_BASE_URL=http://10.0.2.2:5199 (10.0.2.2 is how the Android emulator
  /// reaches the host machine's localhost).
  static const String _defaultApiBaseUrl = 'https://dealgosa.com';

  /// Where the API lives.
  ///
  /// On the web the app is served by the API itself (at /app/), so it talks to whichever host
  /// the page came from: the same build then works on localhost, through a tunnel, and in
  /// production without being rebuilt. A build-time value still wins when one is given.
  static final String apiBaseUrl = _configuredApiBaseUrl.isNotEmpty
      ? _configuredApiBaseUrl
      : (kIsWeb ? Uri.base.origin : _defaultApiBaseUrl);

  static String get apiRoot => '$apiBaseUrl/api/v1';

  static String get hubUrl => '$apiBaseUrl/hubs/auction';

  /// The brand logo, served as a static file by the API and shared with the admin console.
  static String get logoUrl => '$apiBaseUrl/brand/logo.png';

  /*
   * Turns a stored image path into an address this client can actually fetch.
   *
   * The server stores what an administrator uploaded as a root-relative path, "/uploads/….jpg",
   * and that is the right thing to store: the pictures survive a move to another host, and no
   * hostname is ever baked into the database. The admin console is served from the API's own
   * origin, so a relative path resolves there by itself — but the app is not, and a relative path
   * is not a URL it can open at all. Every uploaded picture was therefore falling through to the
   * "no image" placeholder in the app while showing correctly in the console.
   *
   * Anything that already carries its own scheme is left exactly as it is, which covers the
   * seeded catalog pictures that point at an outside host.
   */
  static String? mediaUrl(String? path) {
    final value = path?.trim();
    if (value == null || value.isEmpty) return null;

    final parsed = Uri.tryParse(value);
    if (parsed != null && parsed.hasScheme) return value;

    return '$apiBaseUrl${value.startsWith('/') ? '' : '/'}$value';
  }

  /// Arabic is the platform default; English is fully supported.
  static const String defaultLanguage = 'ar';

  static const Duration requestTimeout = Duration(seconds: 20);

  /// How long the live auction screen waits before forcing a snapshot resync.
  static const Duration resyncInterval = Duration(seconds: 20);
}
