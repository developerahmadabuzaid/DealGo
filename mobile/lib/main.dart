import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'core/localization/app_strings.dart';
import 'core/providers.dart';
import 'core/theme/app_theme.dart';
import 'features/auctions/presentation/auction_detail_screen.dart';
import 'features/auctions/presentation/auctions_screen.dart';
import 'features/auth/presentation/login_screen.dart';
import 'features/home/presentation/home_screen.dart';
import 'features/prizes/presentation/prizes_screen.dart';
import 'features/profile/presentation/profile_screen.dart';
import 'features/store/presentation/cart_screen.dart';
import 'features/store/presentation/store_screen.dart';
import 'features/wallets/presentation/wallet_screen.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const ProviderScope(child: AuctionApp()));
}

class AuctionApp extends ConsumerWidget {
  const AuctionApp({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final locale = ref.watch(localeProvider);
    final auth = ref.watch(authControllerProvider);

    // Nothing renders until the stored session has been checked, so the app never flashes the
    // login screen at a user who is already signed in.
    if (!auth.checked) {
      return MaterialApp(
        theme: AppTheme.light(),
        darkTheme: AppTheme.dark(),
        home: const Scaffold(body: Center(child: CircularProgressIndicator())),
      );
    }

    return MaterialApp.router(
      title: 'Auction Platform',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.light(),
      darkTheme: AppTheme.dark(),
      themeMode: ThemeMode.system,
      locale: locale,
      supportedLocales: AppStrings.supportedLocales,
      localizationsDelegates: const [
        AppStringsDelegate(),
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      // Arabic lays out right-to-left; Flutter derives the direction from the locale.
      routerConfig: _router(ref),
    );
  }
}

final _rootKey = GlobalKey<NavigatorState>();
final _shellKey = GlobalKey<NavigatorState>();

GoRouter _router(WidgetRef ref) {
  return GoRouter(
    navigatorKey: _rootKey,
    initialLocation: '/',
    refreshListenable: _AuthListenable(ref),
    redirect: (context, state) {
      final signedIn = ref.read(authControllerProvider).isSignedIn;
      final atLogin = state.matchedLocation == '/login';

      if (!signedIn) return atLogin ? null : '/login';
      if (atLogin) return '/';
      return null;
    },
    routes: [
      GoRoute(path: '/login', builder: (_, _) => const LoginScreen()),

      // The five main tabs, per the specification's navigation.
      StatefulShellRoute.indexedStack(
        builder: (context, state, navigationShell) => _MainShell(shell: navigationShell),
        branches: [
          StatefulShellBranch(navigatorKey: _shellKey, routes: [
            GoRoute(path: '/', builder: (_, _) => const HomeScreen()),
          ]),
          StatefulShellBranch(routes: [
            GoRoute(path: '/auctions', builder: (_, _) => const AuctionsScreen()),
          ]),
          StatefulShellBranch(routes: [
            GoRoute(path: '/store', builder: (_, _) => const StoreScreen()),
          ]),
          StatefulShellBranch(routes: [
            GoRoute(path: '/wallet', builder: (_, _) => const WalletScreen()),
          ]),
          StatefulShellBranch(routes: [
            GoRoute(path: '/profile', builder: (_, _) => const ProfileScreen()),
          ]),
        ],
      ),

      // Pushed above the tabs.
      GoRoute(
        parentNavigatorKey: _rootKey,
        path: '/auctions/:id',
        builder: (_, state) => AuctionDetailScreen(auctionId: state.pathParameters['id']!),
      ),
      GoRoute(
        parentNavigatorKey: _rootKey,
        path: '/store/products',
        builder: (_, state) =>
            StoreProductsScreen(categoryId: state.uri.queryParameters['categoryId']),
      ),
      GoRoute(
        parentNavigatorKey: _rootKey,
        path: '/store/products/:id',
        builder: (_, state) => ProductDetailScreen(productId: state.pathParameters['id']!),
      ),
      GoRoute(
        parentNavigatorKey: _rootKey,
        path: '/store/cart',
        builder: (_, _) => const CartScreen(),
      ),
      GoRoute(
        parentNavigatorKey: _rootKey,
        path: '/store/orders',
        builder: (_, _) => const OrdersScreen(),
      ),
      GoRoute(
        parentNavigatorKey: _rootKey,
        path: '/prizes',
        builder: (_, _) => const PrizesScreen(),
      ),
      GoRoute(
        parentNavigatorKey: _rootKey,
        path: '/notifications',
        builder: (_, _) => const NotificationsScreen(),
      ),
    ],
  );
}

/// Bridges Riverpod auth changes into go_router's redirect evaluation.
class _AuthListenable extends ChangeNotifier {
  _AuthListenable(WidgetRef ref) {
    ref.listenManual(authControllerProvider, (_, _) => notifyListeners());
  }
}

class _MainShell extends ConsumerWidget {
  const _MainShell({required this.shell});

  /// The shell widget itself renders the active branch as an IndexedStack.
  final StatefulNavigationShell shell;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);

    return Scaffold(
      body: shell,
      bottomNavigationBar: NavigationBar(
        selectedIndex: shell.currentIndex,
        onDestinationSelected: (i) => shell.goBranch(i, initialLocation: i == shell.currentIndex),
        destinations: [
          NavigationDestination(
              icon: const Icon(Icons.home_outlined),
              selectedIcon: const Icon(Icons.home_rounded),
              label: t.home),
          NavigationDestination(
              icon: const Icon(Icons.gavel_outlined),
              selectedIcon: const Icon(Icons.gavel_rounded),
              label: t.auctions),
          NavigationDestination(
              icon: const Icon(Icons.storefront_outlined),
              selectedIcon: const Icon(Icons.storefront_rounded),
              label: t.store),
          NavigationDestination(
              icon: const Icon(Icons.account_balance_wallet_outlined),
              selectedIcon: const Icon(Icons.account_balance_wallet_rounded),
              label: t.wallet),
          NavigationDestination(
              icon: const Icon(Icons.person_outline_rounded),
              selectedIcon: const Icon(Icons.person_rounded),
              label: t.profile),
        ],
      ),
    );
  }
}
