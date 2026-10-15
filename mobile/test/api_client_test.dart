import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/errors/api_exception.dart';
import 'package:mobile/core/network/api_client.dart';
import 'package:mobile/core/storage/token_store.dart';

/// Answers every request with 401 and the server's INVALID_CREDENTIALS envelope.
class _WrongPassword implements HttpClientAdapter {
  final requested = <String>[];

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? body, Future<void>? cancel) async {
    requested.add(options.path);
    return ResponseBody.fromString(
      jsonEncode({'code': 'INVALID_CREDENTIALS', 'message': 'Incorrect email or password.'}),
      401,
      headers: {
        Headers.contentTypeHeader: ['application/json'],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  FlutterSecureStorage.setMockInitialValues({});

  test('a wrong password is reported as a wrong password, not as an expired session', () async {
    final adapter = _WrongPassword();
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test/api/v1', validateStatus: (_) => true))
      ..httpClientAdapter = adapter;

    var sessionEnded = false;
    final client = ApiClient(tokenStore: TokenStore(), dio: dio)
      ..onSessionExpired = () => sessionEnded = true;

    await expectLater(
      client.post('/auth/login', body: {'email': 'a@b.c', 'password': 'wrong'}),
      throwsA(isA<ApiException>().having((e) => e.code, 'code', ErrorCodes.invalidCredentials)),
    );

    expect(sessionEnded, isFalse);
    expect(adapter.requested, ['/auth/login'], reason: 'no refresh is attempted for a sign-in');
  });
}
