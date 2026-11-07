import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/shared/sign_in_gate.dart';

void main() {
  test('guests can open everything that is not about their own account', () {
    for (final page in [
      '/',
      '/auctions',
      '/auctions?tab=completed',
      '/auctions/abc',
      '/auctions/abc/results',
      '/store',
      '/store/products?q=iphone',
      '/store/products/abc',
      '/terms',
    ]) {
      expect(SignInGate.isAccountPage(page), isFalse, reason: page);
    }
  });

  test('pages about an account ask a guest to sign in', () {
    for (final page in [
      '/wallet',
      '/profile',
      '/prizes',
      '/addresses',
      '/notifications',
      '/store/orders',
      '/store/cart',
      '/complete-profile',
    ]) {
      expect(SignInGate.isAccountPage(page), isTrue, reason: page);
    }
  });
}
