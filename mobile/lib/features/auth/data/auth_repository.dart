import '../../../core/network/api_client.dart';
import '../../../core/storage/token_store.dart';

/// What the server tells the client about a code it just issued. Never the code itself, except
/// outside Production where the API echoes it so the flow is testable.
class OtpChallenge {
  const OtpChallenge({
    required this.destination,
    required this.channel,
    required this.expiresAt,
    required this.codeLength,
    required this.resendAvailableInSeconds,
    this.developmentCode,
  });

  /// Masked, e.g. "us***@example.com" or "*******4567".
  final String destination;

  /// "Sms" or "Email".
  final String channel;

  final DateTime expiresAt;
  final int codeLength;
  final int resendAvailableInSeconds;

  /// Present only outside Production.
  final String? developmentCode;

  factory OtpChallenge.fromJson(Map<String, dynamic> j) => OtpChallenge(
        destination: j['destination'] as String? ?? '',
        channel: j['channel'] as String? ?? 'Sms',
        expiresAt: DateTime.parse(j['expiresAt'] as String).toUtc(),
        codeLength: j['codeLength'] as int? ?? 6,
        resendAvailableInSeconds: j['resendAvailableInSeconds'] as int? ?? 60,
        developmentCode: j['developmentCode'] as String?,
      );
}

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

  Future<UserProfile> _persist(Map<String, dynamic> body) async {
    await _tokens.write(AuthTokens(
      accessToken: body['accessToken'] as String,
      refreshToken: body['refreshToken'] as String,
      accessTokenExpiresAt: DateTime.parse(body['accessTokenExpiresAt'] as String).toUtc(),
    ));

    return UserProfile.fromJson(body['user'] as Map<String, dynamic>);
  }

  /// Whether a session is stored on this device at all.
  Future<bool> hasStoredSession() async => await _tokens.read() != null;

  /// Loads the signed-in user.
  ///
  /// Returns null only when there is genuinely no session. A network failure throws instead, so
  /// the caller can keep the user signed in while offline rather than bouncing them to the login
  /// screen because the server was briefly unreachable.
  Future<UserProfile?> currentUser() async {
    if (await _tokens.read() == null) return null;

    final data = await _api.get('/me');
    return UserProfile.fromJson(data as Map<String, dynamic>);
  }

  // ---- OTP sign-in ----

  /// Requests a sign-in code. Succeeds whether or not the destination has an account, so the
  /// response cannot be used to discover who is registered.
  Future<OtpChallenge> requestLoginOtp(String destination, String language) async {
    final data = await _api.post('/auth/otp/request',
        body: {'destination': destination, 'language': language});
    return OtpChallenge.fromJson(data as Map<String, dynamic>);
  }

  Future<UserProfile> verifyLoginOtp(String destination, String code) async {
    final data = await _api.post('/auth/otp/verify',
        body: {'destination': destination, 'code': code});
    return _persist(data as Map<String, dynamic>);
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
