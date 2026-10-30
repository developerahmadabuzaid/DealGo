import '../../../core/network/api_client.dart';
import '../../auth/data/auth_repository.dart' show OtpChallenge;

class Wallet {
  const Wallet({
    required this.id,
    required this.walletType,
    required this.balance,
    required this.currency,
    required this.isWithdrawable,
  });

  final String id;
  final String walletType;
  final double balance;
  final String currency;
  final bool isWithdrawable;

  factory Wallet.fromJson(Map<String, dynamic> j) => Wallet(
        id: j['id'] as String,
        walletType: j['walletType'] as String? ?? 'Main',
        balance: (j['balance'] as num?)?.toDouble() ?? 0,
        currency: j['currency'] as String? ?? 'USD',
        isWithdrawable: j['isWithdrawable'] as bool? ?? false,
      );
}

/// The two balances, always reported separately.
///
/// There is deliberately no combined total: showing one would imply that shopping credit is
/// withdrawable cash, which it never is.
class Wallets {
  const Wallets({required this.main, required this.shopping, required this.currency});

  final Wallet main;
  final Wallet shopping;
  final String currency;

  factory Wallets.fromJson(Map<String, dynamic> j) => Wallets(
        main: Wallet.fromJson(j['main'] as Map<String, dynamic>),
        shopping: Wallet.fromJson(j['shopping'] as Map<String, dynamic>),
        currency: j['currency'] as String? ?? 'USD',
      );
}

class WalletTransaction {
  const WalletTransaction({
    required this.id,
    required this.walletType,
    required this.transactionType,
    required this.amount,
    required this.currency,
    required this.balanceAfter,
    required this.creditSource,
    required this.description,
    required this.createdAt,
    required this.expiresAt,
  });

  final String id;
  final String walletType;
  final String transactionType;
  final double amount;
  final String currency;
  final double balanceAfter;
  final String? creditSource;
  final String? description;
  final DateTime createdAt;
  final DateTime? expiresAt;

  bool get isCredit => amount > 0;

  factory WalletTransaction.fromJson(Map<String, dynamic> j) => WalletTransaction(
        id: j['id'] as String,
        walletType: j['walletType'] as String? ?? 'Main',
        transactionType: j['transactionType'] as String? ?? '',
        amount: (j['amount'] as num?)?.toDouble() ?? 0,
        currency: j['currency'] as String? ?? 'USD',
        balanceAfter: (j['balanceAfter'] as num?)?.toDouble() ?? 0,
        creditSource: j['creditSource'] as String?,
        description: j['description'] as String?,
        createdAt: DateTime.parse(j['createdAt'] as String).toUtc(),
        expiresAt: j['expiresAt'] == null ? null : DateTime.parse(j['expiresAt'] as String).toUtc(),
      );
}

class WalletRepository {
  WalletRepository(this._api);

  final ApiClient _api;

  Future<Wallets> get() async {
    final data = await _api.get('/wallet');
    return Wallets.fromJson(data as Map<String, dynamic>);
  }

  /// Main and Shopping histories are kept completely separate, as the two balances are.
  Future<Paged<WalletTransaction>> transactions({
    String? walletType,
    int page = 1,
    int pageSize = 30,
  }) async {
    final path = switch (walletType) {
      'Shopping' => '/wallet/shopping/transactions',
      'Main' => '/wallet/main/transactions',
      _ => '/wallet/transactions',
    };

    final data = await _api.get(path, query: {'page': page, 'pageSize': pageSize});
    return Paged.fromJson(data as Map<String, dynamic>, WalletTransaction.fromJson);
  }

  /// Opens a card, mada or Apple Pay payment for a top-up, and returns the page to pay it on. The
  /// wallet is credited when Paymob confirms the payment to the server, never by this call.
  Future<TopUp> startTopUp(double amount) async {
    final data = await _api.post('/wallet/topups', body: {'amount': amount});
    final j = data as Map<String, dynamic>;
    return TopUp(
      paymentId: j['paymentId'] as String,
      amount: (j['amount'] as num).toDouble(),
      currency: j['currency'] as String? ?? 'SAR',
      checkoutUrl: j['checkoutUrl'] as String,
    );
  }

  /// "Pending", "Processing", "Completed" or "Failed", as the server last heard from Paymob.
  Future<TopUpStatus> topUpStatus(String paymentId) async {
    final j = await _api.get('/wallet/topups/$paymentId') as Map<String, dynamic>;
    return TopUpStatus(
      status: j['status'] as String? ?? 'Pending',
      failureReason: j['failureReason'] as String?,
    );
  }

  /// Sends the code that confirms a specific withdrawal.
  ///
  /// The code is bound to this exact amount and destination, so it cannot approve a different
  /// transfer. Changing either after requesting it invalidates the code.
  Future<OtpChallenge> requestWithdrawalOtp(double amount, {String? destination}) async {
    final data = await _api.post('/wallet/withdraw/request-otp', body: {
      'amount': amount,
      'currency': null,
      'destination': destination,
    });
    return OtpChallenge.fromJson(data as Map<String, dynamic>);
  }

  /// Withdrawals draw on the Main wallet only; the server rejects any attempt on shopping credit.
  ///
  /// [otpCode] is required whenever withdrawal confirmation is enabled on the server.
  Future<void> withdraw(
    double amount,
    String idempotencyKey, {
    String? destination,
    String? otpCode,
  }) =>
      _api.post('/wallet/withdraw', body: {
        'amount': amount,
        'currency': null,
        'idempotencyKey': idempotencyKey,
        'destination': destination,
        'otpCode': otpCode,
      });
}

class TopUp {
  const TopUp({
    required this.paymentId,
    required this.amount,
    required this.currency,
    required this.checkoutUrl,
  });

  final String paymentId;
  final double amount;
  final String currency;
  final String checkoutUrl;
}

class TopUpStatus {
  const TopUpStatus({required this.status, this.failureReason});

  final String status;
  final String? failureReason;

  bool get isDone => status == 'Completed';
  bool get isFailed => status == 'Failed' || status == 'Cancelled';
}