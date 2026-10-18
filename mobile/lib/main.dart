import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import 'core/localization/app_strings.dart';
import 'core/providers.dart';
import 'core/theme/app_theme.dart';
import 'features/auctions/presentation/auction_detail_screen.dart';
import 'features/auctions/presentation/auction_results_screen.dart';
import 'features/auctions/presentation/ended_auction_screen.dart';
import 'features/auctions/presentation/auctions_screen.dart';
import 'features/auth/presentation/complete_profile_screen.dart';
import 'features/auth/presentation/login_screen.dart';
import 'features/home/presentation/home_screen.dart';
import 'features/prizes/presentation/prizes_screen.dart';
import 'features/profile/presentation/profile_screen.dart';
import 'features/store/presentation/cart_screen.dart';
import 'features/store/presentation/store_screen.dart';
import 'features/wallets/presentation/wallet_screen.dart';
import 'shared/app_header.dart';

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
        home: const Scaffold(body: Center(child: CircularProgressIndicator())),
      );
    }

    return MaterialApp.router(
      title: 'DealGo',
      debugShowCheckedModeBanner: false,
      theme: AppTheme.light(),

      // The app is light whatever the phone is set to: the catalogue's photographs, the price
      // colours and the brand read the same for everyone, and screenshots match what people see.
      themeMode: ThemeMode.light,
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
      builder: (context, child) => _ReadableWidth(child: child),
    );
  }
}

/// Keeps the app readable on a laptop or a large monitor.
///
/// These are mobile-first layouts. Left to fill a 27-inch screen they stretch a card to a metre
/// wide and put the bid button an arm's length from the price. Capping the width and centring it
/// keeps the reading distance the same everywhere; the surrounding band is painted in the page
/// background so the app still looks deliberate rather than letterboxed.
class _ReadableWidth extends StatelessWidget {
  const _ReadableWidth({required this.child});

  static const _maxWidth = 640.0;

  final Widget? child;

  @override
  Widget build(BuildContext context) {
    if (child == null) return const SizedBox.shrink();

    return ColoredBox(
      color: Theme.of(context).scaffoldBackgroundColor,
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: _maxWidth),
          child: child!,
        ),
      ),
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
      final auth = ref.read(authControllerProvider);
      final atLogin = state.matchedLocation == '/login';
      final atProfile = state.matchedLocation == '/complete-profile';

      if (!auth.isSignedIn) return atLogin ? null : '/login';

      // An account created by phone sign-in has no name until the user gives one.
      final needsName = auth.user != null && auth.user!.fullName.trim().isEmpty;
      if (needsName) return atProfile ? null : '/complete-profile';

      if (atLogin || atProfile) return '/';
      return null;
    },
    routes: [
      GoRoute(path: '/login', builder: (_, _) => const LoginScreen()),
      GoRoute(
          path: '/complete-profile', builder: (_, _) => const CompleteProfileScreen()),

      // The five main tabs, per the specification's navigation.
      StatefulShellRoute.indexedStack(
        builder: (context, state, navigationShell) => _MainShell(shell: navigationShell),
        branches: [
          StatefulShellBranch(navigatorKey: _shellKey, routes: [
            GoRoute(path: '/', builder: (_, _) => const HomeScreen()),
          ]),
          StatefulShellBranch(routes: [
            GoRoute(
                path: '/auctions',
                builder: (_, state) =>
                    AuctionsScreen(initialTab: state.uri.queryParameters['tab'])),
          ]),
          StatefulShellBranch(routes: [
            GoRoute(
              path: '/store',
              builder: (_, state) =>
                  StoreScreen(categoryId: state.uri.queryParameters['categoryId']),
            ),
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
        path: '/auctions/:id/ended',
        builder: (_, state) => EndedAuctionScreen(auctionId: state.pathParameters['id']!),
      ),
      GoRoute(
        parentNavigatorKey: _rootKey,
        path: '/auctions/:id/results',
        builder: (_, state) => AuctionResultsScreen(auctionId: state.pathParameters['id']!),
      ),
      GoRoute(
        parentNavigatorKey: _rootKey,
        path: '/store/products',
        builder: (_, state) =>
            StoreProductsScreen(
              categoryId: state.uri.queryParameters['categoryId'],
              initialSearch: state.uri.queryParameters['q'],
              offersOnly: state.uri.queryParameters['offers'] == '1',
              creditOnly: state.uri.queryParameters['credit'] == '1',
              sortBy: state.uri.queryParameters['sort'],
              title: state.uri.queryParameters['title'],
            ),
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
      // One bar for the whole app: it is drawn above the tabs' own pages and never scrolls away.
      body: Column(children: [const AppHeader(), Expanded(child: shell)]),
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
