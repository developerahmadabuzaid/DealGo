import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/errors/api_exception.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/storage/token_store.dart';
import 'package:mobile/features/auth/data/auth_repository.dart';

void main() {
  group('OtpChallenge', () {
    Map<String, dynamic> json({String? devCode}) => {
          'destination': 'us***@example.com',
          'channel': 'Email',
          'expiresAt': '2026-09-08T12:05:00.000Z',
          'codeLength': 6,
          'resendAvailableInSeconds': 60,
          'developmentCode': ?devCode,
        };

    test('parses the challenge the server returns', () {
      final c = OtpChallenge.fromJson(json());

      expect(c.destination, 'us***@example.com');
      expect(c.channel, 'Email');
      expect(c.codeLength, 6);
      expect(c.resendAvailableInSeconds, 60);
      expect(c.expiresAt.isUtc, isTrue);
    });

    test('carries no code when the server does not send one', () {
      // Production never echoes the code; the client must cope with it being absent.
      expect(OtpChallenge.fromJson(json()).developmentCode, isNull);
    });

    test('picks up the echoed code outside production', () {
      expect(OtpChallenge.fromJson(json(devCode: '123456')).developmentCode, '123456');
    });

    test('never exposes the full destination', () {
      final c = OtpChallenge.fromJson(json());
      expect(c.destination, contains('*'));
      expect(c.destination, isNot(contains('user@')));
    });
  });

  group('AuthTokens', () {
    test('round-trips through storage', () {
      final tokens = AuthTokens(
        accessToken: 'access',
        refreshToken: 'refresh',
        accessTokenExpiresAt: DateTime.utc(2026, 9, 8, 12, 15),
      );

      final restored = AuthTokens.fromJson(tokens.toJson());

      expect(restored.accessToken, 'access');
      expect(restored.refreshToken, 'refresh');
      expect(restored.accessTokenExpiresAt, tokens.accessTokenExpiresAt);
    });

    test('reports an elapsed access token as expired', () {
      final expired = AuthTokens(
        accessToken: 'a',
        refreshToken: 'r',
        accessTokenExpiresAt: DateTime.now().toUtc().subtract(const Duration(minutes: 1)),
      );

      expect(expired.isExpired, isTrue);
    });

    test('a far-future expiry is what a never-expiring session looks like', () {
      // The server sends DateTime.MaxValue when the session lasts until sign-out; the client must
      // simply treat it as not expired rather than special-casing it.
      final persistent = AuthTokens(
        accessToken: 'a',
        refreshToken: 'r',
        accessTokenExpiresAt: DateTime.utc(9999, 12, 31),
      );

      expect(persistent.isExpired, isFalse);
    });
  });

  group('OTP error codes', () {
    test('the client can tell the OTP failure modes apart', () {
      String codeOf(String raw) =>
          ApiException.fromResponse({'code': raw, 'message': 'x'}, 400).code;

      expect(codeOf('OTP_EXPIRED'), ErrorCodes.otpExpired);
      expect(codeOf('OTP_REQUIRED'), ErrorCodes.otpRequired);
      expect(codeOf('OTP_ALREADY_USED'), ErrorCodes.otpAlreadyUsed);
      expect(codeOf('OTP_TOO_MANY_ATTEMPTS'), ErrorCodes.otpTooManyAttempts);
      expect(codeOf('OTP_REQUEST_TOO_SOON'), ErrorCodes.otpRequestTooSoon);
      expect(codeOf('INVALID_OTP'), ErrorCodes.invalidOtp);
    });
  });

  group('AppStrings for the OTP flows', () {
    test('both languages cover the code screens', () {
      const ar = AppStrings(Locale('ar'));
      const en = AppStrings(Locale('en'));

      expect(ar.signInWithCode, isNotEmpty);
      expect(ar.emailOrPhone, isNotEmpty);
      expect(ar.confirmWithdrawal, isNotEmpty);

      expect(en.signInWithCode, 'Sign in with a code');
      expect(en.sendCode, 'Send code');
      expect(en.confirmWithdrawal, 'Confirm withdrawal');
      expect(en.signInWithCode, isNot(ar.signInWithCode));
    });

    test('the countdown strings interpolate their seconds', () {
      const en = AppStrings(Locale('en'));

      expect(en.resendIn(45), contains('45'));
      expect(en.codeExpiresIn(120), contains('120'));
    });

    test('the withdrawal summary names the amount and destination', () {
      const en = AppStrings(Locale('en'));
      final summary = en.withdrawalSummary('100.00 USD', 'IBAN123');

      expect(summary, contains('100.00 USD'));
      expect(summary, contains('IBAN123'));
    });
  });
}
