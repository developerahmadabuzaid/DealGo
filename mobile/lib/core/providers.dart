import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../features/auth/data/auth_repository.dart';
import '../features/auctions/data/auction_repository.dart';
import '../features/home/data/ads_repository.dart';
import '../features/support/support.dart';
import '../features/prizes/data/prize_repository.dart';
import '../features/store/data/store_repository.dart';
import '../features/wallets/data/wallet_repository.dart';
import 'errors/api_exception.dart';
import 'network/api_client.dart';
import 'realtime/auction_hub_client.dart';
import 'storage/token_store.dart';

// ---------------------------------------------------------------- infrastructure

final tokenStoreProvider = Provider<TokenStore>((ref) => TokenStore());

final apiClientProvider = Provider<ApiClient>((ref) {
  final client = ApiClient(tokenStore: ref.watch(tokenStoreProvider));

  // When refresh fails, drop the session so the router sends the user back to sign-in.
  client.onSessionExpired = () => ref.read(authControllerProvider.notifier).forceSignOut();

  return client;
});

final hubClientProvider = Provider<AuctionHubClient>((ref) {
  final api = ref.watch(apiClientProvider);
  final hub = AuctionHubClient(accessTokenProvider: api.currentAccessToken);
  ref.onDispose(hub.dispose);
  return hub;
});

// ---------------------------------------------------------------- repositories

final authRepositoryProvider = Provider(
    (ref) => AuthRepository(ref.watch(apiClientProvider), ref.watch(tokenStoreProvider)));

final auctionRepositoryProvider =
    Provider((ref) => AuctionRepository(ref.watch(apiClientProvider)));

final walletRepositoryProvider =
    Provider((ref) => WalletRepository(ref.watch(apiClientProvider)));

final storeRepositoryProvider =
    Provider((ref) => StoreRepository(ref.watch(apiClientProvider)));

final adsRepositoryProvider = Provider((ref) => AdsRepository(ref.watch(apiClientProvider)));

final supportRepositoryProvider =
    Provider((ref) => SupportRepository(ref.watch(apiClientProvider)));

final prizeRepositoryProvider =
    Provider((ref) => PrizeRepository(ref.watch(apiClientProvider)));

final notificationRepositoryProvider =
    Provider((ref) => NotificationRepository(ref.watch(apiClientProvider)));

// ---------------------------------------------------------------- session

class AuthState {
  const AuthState({
    this.user,
    this.isLoading = false,
    this.checked = false,
    this.isOffline = false,
    this.hasSession = false,
  });

  final UserProfile? user;
  final bool isLoading;

  /// True once the app has finished looking for a stored session.
  final bool checked;

  /// True when tokens exist but the server could not be reached on launch.
  final bool isOffline;

  /// True when tokens are stored, even if the profile could not be loaded.
  final bool hasSession;

  bool get isSignedIn => user != null || hasSession;

  AuthState copyWith({
    UserProfile? user,
    bool? isLoading,
    bool? checked,
    bool? isOffline,
    bool? hasSession,
    bool clearUser = false,
  }) =>
      AuthState(
        user: clearUser ? null : (user ?? this.user),
        isLoading: isLoading ?? this.isLoading,
        checked: checked ?? this.checked,
        isOffline: isOffline ?? this.isOffline,
        hasSession: hasSession ?? this.hasSession,
      );
}

class AuthController extends StateNotifier<AuthState> {
  AuthController(this._ref) : super(const AuthState()) {
    _restore();
  }

  final Ref _ref;

  AuthRepository get _repo => _ref.read(authRepositoryProvider);

  /// Restores the stored session on launch.
  ///
  /// The session lasts until the user signs out, so a failure to reach the server must not end it.
  /// Only an outright missing session, or a refresh the server rejected, signs the user out — the
  /// API client raises that through [forceSignOut].
  Future<void> _restore() async {
    if (!await _repo.hasStoredSession()) {
      state = const AuthState(checked: true);
      return;
    }

    try {
      final user = await _repo.currentUser();
      state = AuthState(user: user, checked: true, hasSession: user != null);
      if (user != null) await _ref.read(hubClientProvider).connect();
    } on ApiException catch (e) {
      if (e.code == ErrorCodes.unauthorized || e.code == ErrorCodes.accountDisabled) {
        // The server refused the session; the tokens are already cleared.
        state = const AuthState(checked: true);
        return;
      }

      // Offline or the server is briefly unavailable. The tokens are still valid, so keep the
      // session: signing the user out here would end a session they never chose to end.
      state = const AuthState(checked: true, isOffline: true, hasSession: true);
      unawaited(_ref.read(hubClientProvider).connect());
      unawaited(refreshProfile());
    }
  }

  /// Re-attempts the profile load after an offline start.
  Future<void> refreshProfile() async {
    try {
      final user = await _repo.currentUser();
      if (user != null) {
        state = state.copyWith(user: user, isOffline: false, hasSession: true);
      }
    } catch (_) {
      // Still unreachable; the session stays as it is.
    }
  }

  /// Requests a sign-in code for a phone number.
  Future<OtpChallenge> requestLoginOtp(String destination, String language) =>
      _repo.requestLoginOtp(destination, language);

  /// Exchanges a sign-in code for a session.
  Future<void> signInWithOtp(String destination, String code) async {
    state = state.copyWith(isLoading: true);
    try {
      final user = await _repo.verifyLoginOtp(destination, code);
      state = AuthState(user: user, checked: true, hasSession: true);
      await _ref.read(hubClientProvider).connect();
    } finally {
      state = state.copyWith(isLoading: false);
    }
  }

  /// Saves the name of an account that was just created by phone sign-in.
  Future<void> completeProfile(String fullName) async {
    final updated = await _repo.updateProfile(fullName: fullName);
    state = state.copyWith(user: updated);
  }

  /// Deletes the account after the user confirms with a code, then leaves the user signed out.
  Future<void> deleteAccount(String code) async {
    await _repo.deleteAccount(code);
    await _ref.read(hubClientProvider).disconnect();
    state = const AuthState(checked: true);
  }

  Future<void> signOut() async {
    await _repo.logout();
    await _ref.read(hubClientProvider).disconnect();
    state = const AuthState(checked: true);
  }

  /// Called when a refresh fails: the tokens are already gone, so only local state is cleared.
  void forceSignOut() {
    _ref.read(hubClientProvider).disconnect();
    state = const AuthState(checked: true);
  }

  Future<void> setLanguage(String language) async {
    final updated = await _repo.updateProfile(language: language);
    state = state.copyWith(user: updated);
  }
}

final authControllerProvider =
    StateNotifierProvider<AuthController, AuthState>((ref) => AuthController(ref));

/// The app is Arabic only. The English strings are kept in [AppStrings] so a second
/// language can be switched back on by reading the user's choice here again, but nothing
/// in the running app selects it.
final localeProvider = Provider<Locale>((ref) => const Locale('ar'));

// ---------------------------------------------------------------- data providers

final walletsProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(walletRepositoryProvider).get());

final storeHomeProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(storeRepositoryProvider).home());

final myWinsProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(prizeRepositoryProvider).myWins());

final myClaimsProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(prizeRepositoryProvider).myClaims());

final notificationsProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(notificationRepositoryProvider).list());

final unreadCountProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(notificationRepositoryProvider).unreadCount());

/// One store category's offers, biggest saving first.
final categoryOffersProvider = FutureProvider.autoDispose.family(
    (ref, String categoryId) => ref.watch(storeRepositoryProvider).products(
          categoryId: categoryId,
          onOfferOnly: true,
          sortBy: 'discount',
          pageSize: 12,
        ));

/// One store category's products, for the home page's grid under the banner.
/// One product's page. The page and its buy button both read this, so they ask the server once and
/// can never show two different versions of the same product.
final productDetailProvider = FutureProvider.autoDispose
    .family((ref, String id) => ref.watch(storeRepositoryProvider).product(id));

final categoryProductsProvider = FutureProvider.autoDispose.family((ref, String categoryId) =>
    ref.watch(storeRepositoryProvider).products(categoryId: categoryId, pageSize: 30));

final myOrdersProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(storeRepositoryProvider).orders());

final auctionListProvider = FutureProvider.autoDispose
    .family((ref, String group) => ref.watch(auctionRepositoryProvider).list(group: group));

/// The advertisements an administrator set in the console. An error is not worth a message on
/// the front page: with none, the app advertises the auctions and products it already has.
final appAdsProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(adsRepositoryProvider).list());

/// The WhatsApp number support answers on. Read when the button is tapped, so a number changed in
/// the console reaches the app without an update.
final supportWhatsappProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(supportRepositoryProvider).whatsappNumber());

/// The auctions an administrator picked out for the front page.
final featuredAuctionsProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(auctionRepositoryProvider).list(featured: true, pageSize: 10));

final auctionDetailProvider = FutureProvider.autoDispose
    .family((ref, String id) => ref.watch(auctionRepositoryProvider).detail(id));

final auctionWinnersProvider = FutureProvider.autoDispose
    .family((ref, String id) => ref.watch(auctionRepositoryProvider).winners(id));

/// A finished auction's bid log, newest first.
final auctionBidsProvider = FutureProvider.autoDispose
    .family((ref, String id) => ref.watch(auctionRepositoryProvider).bids(id));

// ---------------------------------------------------------------- cart

/// The cart is local and advisory. Prices, stock and the order total are all re-decided by the
/// server at checkout.
class CartController extends StateNotifier<List<CartLine>> {
  CartController() : super([]);

  void add(StoreProduct product, {int quantity = 1}) {
    final existing = state.indexWhere((l) => l.product.id == product.id);

    if (existing >= 0) {
      final updated = [...state];
      updated[existing].quantity += quantity;
      state = updated;
    } else {
      state = [...state, CartLine(product: product, quantity: quantity)];
    }
  }

  void setQuantity(String productId, int quantity) {
    if (quantity <= 0) {
      remove(productId);
      return;
    }
    state = [
      for (final line in state)
        if (line.product.id == productId) (line..quantity = quantity) else line
    ];
  }

  void remove(String productId) =>
      state = state.where((l) => l.product.id != productId).toList();

  void clear() => state = [];

  int get itemCount => state.fold(0, (sum, l) => sum + l.quantity);

  double get subtotal => state.fold(0, (sum, l) => sum + l.lineTotal);
}

final cartProvider =
    StateNotifierProvider<CartController, List<CartLine>>((ref) => CartController());
