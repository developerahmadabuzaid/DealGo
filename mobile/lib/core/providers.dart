import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../features/auth/data/auth_repository.dart';
import '../features/auctions/data/auction_repository.dart';
import '../features/prizes/data/prize_repository.dart';
import '../features/store/data/store_repository.dart';
import '../features/wallets/data/wallet_repository.dart';
import 'config/app_config.dart';
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

final prizeRepositoryProvider =
    Provider((ref) => PrizeRepository(ref.watch(apiClientProvider)));

final notificationRepositoryProvider =
    Provider((ref) => NotificationRepository(ref.watch(apiClientProvider)));

// ---------------------------------------------------------------- session

class AuthState {
  const AuthState({this.user, this.isLoading = false, this.checked = false});

  final UserProfile? user;
  final bool isLoading;

  /// True once the app has finished looking for a stored session.
  final bool checked;

  bool get isSignedIn => user != null;

  AuthState copyWith({UserProfile? user, bool? isLoading, bool? checked, bool clearUser = false}) =>
      AuthState(
        user: clearUser ? null : (user ?? this.user),
        isLoading: isLoading ?? this.isLoading,
        checked: checked ?? this.checked,
      );
}

class AuthController extends StateNotifier<AuthState> {
  AuthController(this._ref) : super(const AuthState()) {
    _restore();
  }

  final Ref _ref;

  AuthRepository get _repo => _ref.read(authRepositoryProvider);

  Future<void> _restore() async {
    final user = await _repo.currentUser();
    state = AuthState(user: user, checked: true);
    if (user != null) await _ref.read(hubClientProvider).connect();
  }

  Future<void> signIn(String email, String password) async {
    state = state.copyWith(isLoading: true);
    try {
      final user = await _repo.login(email, password);
      state = AuthState(user: user, checked: true);
      await _ref.read(hubClientProvider).connect();
    } finally {
      state = state.copyWith(isLoading: false);
    }
  }

  Future<void> register({
    required String fullName,
    required String email,
    required String password,
    String? phone,
    String language = 'ar',
  }) async {
    state = state.copyWith(isLoading: true);
    try {
      final user = await _repo.register(
        fullName: fullName,
        email: email,
        password: password,
        phoneNumber: phone,
        language: language,
      );
      state = AuthState(user: user, checked: true);
      await _ref.read(hubClientProvider).connect();
    } finally {
      state = state.copyWith(isLoading: false);
    }
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

/// Arabic unless the signed-in user chose otherwise.
final localeProvider = Provider<Locale>((ref) {
  final language = ref.watch(authControllerProvider).user?.language ?? AppConfig.defaultLanguage;
  return Locale(language == 'en' ? 'en' : 'ar');
});

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

final myOrdersProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(storeRepositoryProvider).orders());

final auctionListProvider = FutureProvider.autoDispose
    .family((ref, String group) => ref.watch(auctionRepositoryProvider).list(group: group));

final auctionDetailProvider = FutureProvider.autoDispose
    .family((ref, String id) => ref.watch(auctionRepositoryProvider).detail(id));

final auctionWinnersProvider = FutureProvider.autoDispose
    .family((ref, String id) => ref.watch(auctionRepositoryProvider).winners(id));

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
