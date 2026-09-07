import 'dart:convert';

import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// The authenticated session as the client holds it.
class AuthTokens {
  const AuthTokens({
    required this.accessToken,
    required this.refreshToken,
    required this.accessTokenExpiresAt,
  });

  final String accessToken;
  final String refreshToken;
  final DateTime accessTokenExpiresAt;

  bool get isExpired => DateTime.now().toUtc().isAfter(accessTokenExpiresAt);

  Map<String, dynamic> toJson() => {
        'accessToken': accessToken,
        'refreshToken': refreshToken,
        'accessTokenExpiresAt': accessTokenExpiresAt.toIso8601String(),
      };

  factory AuthTokens.fromJson(Map<String, dynamic> json) => AuthTokens(
        accessToken: json['accessToken'] as String,
        refreshToken: json['refreshToken'] as String,
        accessTokenExpiresAt:
            DateTime.parse(json['accessTokenExpiresAt'] as String).toUtc(),
      );
}

/// Tokens live in the platform keystore (Keychain / EncryptedSharedPreferences),
/// never in plain preferences.
class TokenStore {
  TokenStore([FlutterSecureStorage? storage])
      : _storage = storage ?? const FlutterSecureStorage();

  static const _key = 'auction.auth.tokens';

  final FlutterSecureStorage _storage;

  Future<AuthTokens?> read() async {
    try {
      final raw = await _storage.read(key: _key);
      if (raw == null) return null;
      return AuthTokens.fromJson(jsonDecode(raw) as Map<String, dynamic>);
    } catch (_) {
      // A corrupt or undecryptable entry is treated as "signed out" rather than a crash.
      await clear();
      return null;
    }
  }

  Future<void> write(AuthTokens tokens) =>
      _storage.write(key: _key, value: jsonEncode(tokens.toJson()));

  Future<void> clear() => _storage.delete(key: _key);
}
