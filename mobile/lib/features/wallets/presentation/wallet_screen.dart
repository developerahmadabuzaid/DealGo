import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';
import '../data/wallet_repository.dart';

/// The two balances, shown as two separate cards.
///
/// There is deliberately no "total balance" anywhere on this screen: combining 150 cash with
/// 40 store credit into "190" would imply the whole amount can be withdrawn, which it cannot.
class WalletScreen extends ConsumerWidget {
  const WalletScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final walletsAsync = ref.watch(walletsProvider);

    return Scaffold(
      appBar: AppBar(title: Text(t.wallet)),
      body: walletsAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.message : e.toString(),
          onRetry: () => ref.invalidate(walletsProvider),
        ),
        data: (wallets) => RefreshIndicator(
          onRefresh: () async => ref.invalidate(walletsProvider),
          child: ListView(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
            children: [
              _MainWalletCard(wallet: wallets.main),
              const SizedBox(height: 12),
              _ShoppingWalletCard(wallet: wallets.shopping),
              const SizedBox(height: 24),
              _TransactionSection(walletType: 'Main', title: t.mainWalletHistory),
              const SizedBox(height: 20),
              _TransactionSection(walletType: 'Shopping', title: t.shoppingWalletHistory),
            ],
          ),
        ),
      ),
    );
  }
}

class _MainWalletCard extends ConsumerWidget {
  const _MainWalletCard({required this.wallet});

  final Wallet wallet;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.account_balance_wallet_rounded),
                const SizedBox(width: 8),
                Text(t.mainWallet,
                    style: Theme.of(context)
                        .textTheme
                        .titleMedium
                        ?.copyWith(fontWeight: FontWeight.w700)),
              ],
            ),
            const SizedBox(height: 12),
            Text(Fmt.money(wallet.balance, wallet.currency, locale: locale),
                style: Theme.of(context)
                    .textTheme
                    .headlineMedium
                    ?.copyWith(fontWeight: FontWeight.bold)),
            const SizedBox(height: 6),
            StatusPill(t.withdrawable, color: AppTheme.success, icon: Icons.check_rounded),
            const SizedBox(height: 16),
            Row(
              children: [
                Expanded(
                  child: FilledButton.tonalIcon(
                    onPressed: () => _amountSheet(context, ref, deposit: true),
                    icon: const Icon(Icons.add_rounded),
                    label: Text(t.deposit),
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: OutlinedButton.icon(
                    onPressed: () => _amountSheet(context, ref, deposit: false),
                    icon: const Icon(Icons.arrow_outward_rounded),
                    label: Text(t.withdraw),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _amountSheet(BuildContext context, WidgetRef ref, {required bool deposit}) async {
    final t = AppStrings.of(context);
    final controller = TextEditingController();

    await showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      builder: (sheetContext) => Padding(
        padding: EdgeInsets.fromLTRB(
            20, 20, 20, MediaQuery.of(sheetContext).viewInsets.bottom + 20),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(deposit ? t.deposit : t.withdraw,
                style: Theme.of(sheetContext).textTheme.titleLarge),
            const SizedBox(height: 16),
            TextField(
              controller: controller,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: InputDecoration(labelText: t.amount, suffixText: wallet.currency),
              autofocus: true,
            ),
            const SizedBox(height: 18),
            FilledButton(
              onPressed: () async {
                final amount = double.tryParse(controller.text.trim());
                if (amount == null || amount <= 0) return;

                Navigator.of(sheetContext).pop();

                final repo = ref.read(walletRepositoryProvider);
                // The key makes a retried request safe if the response is lost in transit.
                final key = '${deposit ? 'dep' : 'wd'}-${DateTime.now().microsecondsSinceEpoch}';

                try {
                  if (deposit) {
                    await repo.deposit(amount, key);
                  } else {
                    await repo.withdraw(amount, key);
                  }
                  ref.invalidate(walletsProvider);
                } on ApiException catch (e) {
                  if (context.mounted) {
                    ScaffoldMessenger.of(context).showSnackBar(
                      SnackBar(content: Text(e.message), backgroundColor: AppTheme.urgentDeep),
                    );
                  }
                }
              },
              child: Text(t.confirm),
            ),
          ],
        ),
      ),
    );
  }
}

class _ShoppingWalletCard extends StatelessWidget {
  const _ShoppingWalletCard({required this.wallet});

  final Wallet wallet;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    return Card(
      color: AppTheme.gold.withValues(alpha: 0.08),
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.card_giftcard_rounded, color: AppTheme.gold),
                const SizedBox(width: 8),
                Text(t.shoppingCredit,
                    style: Theme.of(context)
                        .textTheme
                        .titleMedium
                        ?.copyWith(fontWeight: FontWeight.w700)),
              ],
            ),
            const SizedBox(height: 12),
            Text(Fmt.money(wallet.balance, wallet.currency, locale: locale),
                style: Theme.of(context)
                    .textTheme
                    .headlineMedium
                    ?.copyWith(fontWeight: FontWeight.bold)),
            const SizedBox(height: 6),
            StatusPill(t.storePurchasesOnly, color: AppTheme.gold, icon: Icons.lock_rounded),
            const SizedBox(height: 10),
            Text(t.shoppingCreditCannotBeWithdrawn,
                style: Theme.of(context).textTheme.bodySmall),
            const SizedBox(height: 16),
            FilledButton.icon(
              onPressed: () => context.go('/store'),
              style: FilledButton.styleFrom(backgroundColor: AppTheme.gold),
              icon: const Icon(Icons.storefront_rounded),
              label: Text(t.shopNow),
            ),
          ],
        ),
      ),
    );
  }
}

final _walletTransactionsProvider = FutureProvider.autoDispose
    .family((ref, String walletType) =>
        ref.watch(walletRepositoryProvider).transactions(walletType: walletType, pageSize: 10));

/// Main and Shopping histories are shown separately, matching how the balances are kept.
class _TransactionSection extends ConsumerWidget {
  const _TransactionSection({required this.walletType, required this.title});

  final String walletType;
  final String title;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(_walletTransactionsProvider(walletType));
    final locale = Localizations.localeOf(context).languageCode;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(title,
            style: Theme.of(context).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 8),
        async.when(
          loading: () => const Padding(
              padding: EdgeInsets.all(16), child: Center(child: CircularProgressIndicator())),
          error: (_, _) => const SizedBox.shrink(),
          data: (paged) => paged.items.isEmpty
              ? Card(
                  child: Padding(
                    padding: const EdgeInsets.all(20),
                    child: Center(child: Text(AppStrings.of(context).nothingHere)),
                  ),
                )
              : Card(
                  child: Column(
                    children: [
                      for (final tx in paged.items)
                        ListTile(
                          dense: true,
                          leading: Icon(
                            tx.isCredit ? Icons.south_west_rounded : Icons.north_east_rounded,
                            color: tx.isCredit ? AppTheme.success : AppTheme.urgentDeep,
                            size: 20,
                          ),
                          title: Text(_label(tx.transactionType, context)),
                          subtitle: Text(Fmt.dateTime(tx.createdAt, locale: locale)),
                          trailing: Text(
                            '${tx.isCredit ? '+' : ''}${Fmt.money(tx.amount, tx.currency, locale: locale)}',
                            style: TextStyle(
                              fontWeight: FontWeight.w600,
                              color: tx.isCredit ? AppTheme.success : null,
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
        ),
      ],
    );
  }

  String _label(String type, BuildContext context) {
    final ar = Localizations.localeOf(context).languageCode == 'ar';

    return switch (type) {
      'Deposit' => ar ? 'إيداع' : 'Deposit',
      'Withdrawal' => ar ? 'سحب' : 'Withdrawal',
      'AuctionEntryFee' => ar ? 'رسوم اشتراك مزاد' : 'Auction entry fee',
      'PrizePayment' => ar ? 'دفع قيمة جائزة' : 'Prize payment',
      'ShoppingCredit' => ar ? 'رصيد تسوق' : 'Shopping credit',
      'StorePurchase' => ar ? 'شراء من المتجر' : 'Store purchase',
      'Refund' => ar ? 'استرداد' : 'Refund',
      'Adjustment' => ar ? 'تسوية' : 'Adjustment',
      'Expiration' => ar ? 'انتهاء صلاحية' : 'Expiration',
      _ => type,
    };
  }
}
