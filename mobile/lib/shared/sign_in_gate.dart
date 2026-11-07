import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../core/providers.dart';

/// Guests may look at everything that is not about their own account: the home page, the
/// auctions, the store and its products. App Store guideline 5.1.1(v) requires it, and it is also
/// how people decide whether to sign up.
///
/// These pages are someone's own: their money, prizes, orders, addresses, notifications and
/// profile, and the cart's checkout. A guest who opens one is asked to sign in first.
abstract final class SignInGate {
  static const _accountPages = [
    '/wallet',
    '/profile',
    '/prizes',
    '/addresses',
    '/notifications',
    '/store/orders',
    '/store/cart',
    '/complete-profile',
  ];

  /// Whether [location] (a path, with or without a query) needs an account.
  static bool isAccountPage(String location) {
    final path = Uri.parse(location).path;
    return _accountPages.any((p) => path == p || path.startsWith('$p/'));
  }

  /// True when the user is signed in, or signs in now. A guest sees the sign-in screen on top of
  /// the page they are on, and comes back to it either way.
  static Future<bool> ensureSignedIn(BuildContext context, WidgetRef ref) async {
    if (ref.read(authControllerProvider).isSignedIn) return true;
    await context.push('/login');
    return ref.read(authControllerProvider).isSignedIn;
  }
}
