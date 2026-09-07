/// Build-time configuration.
///
/// Override at build time, e.g.
///   flutter run --dart-define=API_BASE_URL=https://api.example.com
class AppConfig {
  const AppConfig._();

  /// 10.0.2.2 is how the Android emulator reaches the host machine's localhost.
  static const String apiBaseUrl = String.fromEnvironment(
    'API_BASE_URL',
    defaultValue: 'http://10.0.2.2:5199',
  );

  static String get apiRoot => '$apiBaseUrl/api/v1';

  static String get hubUrl => '$apiBaseUrl/hubs/auction';

  /// Arabic is the platform default; English is fully supported.
  static const String defaultLanguage = 'ar';

  static const Duration requestTimeout = Duration(seconds: 20);

  /// How long the live auction screen waits before forcing a snapshot resync.
  static const Duration resyncInterval = Duration(seconds: 20);
}
