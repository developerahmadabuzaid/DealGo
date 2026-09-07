import '../../../core/network/api_client.dart';
import '../../../core/storage/token_store.dart';

class UserProfile {
  const UserProfile({
    required this.id,
    required this.fullName,
    required this.email,
    required this.phoneNumber,
    required this.language,
    required this.isActive,
    required this.roles,
  });

  final String id;
  final String fullName;
  final String email;
  final String? phoneNumber;
  final String language;
  final bool isActive;
  final List<String> roles;

  factory UserProfile.fromJson(Map<String, dynamic> j) => UserProfile(
        id: j['id'] as String,
        fullName: j['fullName'] as String? ?? '',
        email: j['email'] as String? ?? '',
        phoneNumber: j['phoneNumber'] as String?,
        language: j['language'] as String? ?? 'ar',
        isActive: j['isActive'] as bool? ?? true,
        roles: ((j['roles'] as List?) ?? const []).map((e) => e.toString()).toList(),
      );
}

class AuthRepository {
  AuthRepository(this._api, this._tokens);

  final ApiClient _api;
  final TokenStore _tokens;

  Future<UserProfile> register({
    required String fullName,
    required String email,
    required String password,
    String? phoneNumber,
    String language = 'ar',
  }) async {
    final data = await _api.post('/auth/register', body: {
      'fullName': fullName,
      'email': email,
      'password': password,
      'phoneNumber': phoneNumber,
      'language': language,
    });

    return _persist(data as Map<String, dynamic>);
  }

  Future<UserProfile> login(String email, String password) async {
    final data = await _api.post('/auth/login', body: {'email': email, 'password': password});
    return _persist(data as Map<String, dynamic>);
  }

  Future<UserProfile> _persist(Map<String, dynamic> body) async {
    await _tokens.write(AuthTokens(
      accessToken: body['accessToken'] as String,
      refreshToken: body['refreshToken'] as String,
      accessTokenExpiresAt: DateTime.parse(body['accessTokenExpiresAt'] as String).toUtc(),
    ));

    return UserProfile.fromJson(body['user'] as Map<String, dynamic>);
  }

  Future<UserProfile?> currentUser() async {
    if (await _tokens.read() == null) return null;
    try {
      final data = await _api.get('/me');
      return UserProfile.fromJson(data as Map<String, dynamic>);
    } catch (_) {
      return null;
    }
  }

  Future<UserProfile> updateProfile({String? fullName, String? phoneNumber, String? language}) async {
    final data = await _api.put('/me', body: {
      'fullName': fullName,
      'phoneNumber': phoneNumber,
      'language': language,
    });
    return UserProfile.fromJson(data as Map<String, dynamic>);
  }

  Future<void> logout() async {
    final tokens = await _tokens.read();
    try {
      await _api.post('/auth/logout',
          body: {'refreshToken': tokens?.refreshToken, 'allDevices': false});
    } catch (_) {
      // Even if the server call fails, the local session must end.
    }
    await _tokens.clear();
  }

  /// Registers this device for push so the backend can reach the user when the app is closed.
  Future<void> registerDevice(String token, String platform) async {
    try {
      await _api.post('/notifications/devices', body: {'token': token, 'platform': platform});
    } catch (_) {
      // Push registration is never allowed to block sign-in.
    }
  }
}
