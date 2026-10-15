import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';
import '../../../shared/otp_input.dart';
import '../../auth/data/auth_repository.dart' show OtpChallenge;
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
      body: walletsAsync.when(
        loading: () => const Padding(
            padding: EdgeInsets.all(AppTheme.space4),
            child: Column(
              children: [
                Skeleton(height: 210, radius: AppTheme.radiusLg),
                SizedBox(height: AppTheme.space3),
                Skeleton(height: 210, radius: AppTheme.radiusLg),
              ],
            )),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.displayMessage : e.toString(),
          onRetry: () => ref.invalidate(walletsProvider),
        ),
        data: (wallets) => RefreshIndicator(
          onRefresh: () async => ref.invalidate(walletsProvider),
          child: ListView(
            padding: const EdgeInsets.fromLTRB(
              AppTheme.space4,
              AppTheme.space4,
              AppTheme.space4,
              AppTheme.space8,
            ),
            children: [
              _MainWalletCard(wallet: wallets.main),
              const SizedBox(height: AppTheme.space3),
              _ShoppingWalletCard(wallet: wallets.shopping),
              const SizedBox(height: AppTheme.space5),
              _TransactionSection(walletType: 'Main', title: t.mainWalletHistory),
              const SizedBox(height: AppTheme.space4),
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

    return WalletCard(
      title: t.mainWallet,
      amount: Fmt.money(wallet.balance, wallet.currency, locale: locale),
      accent: Theme.of(context).colorScheme.primary,
      icon: Icons.account_balance_wallet_rounded,
      ruleLabel: t.withdrawable,
      ruleIcon: Icons.check_circle_rounded,
      actions: [
        // Tonal buttons take their colour from the secondary role, which is gold here — the
        // shopping-credit colour. On the cash card that would say the wrong thing, so this one
        // is tinted from the primary role instead.
        FilledButton.icon(
          onPressed: () => _depositSheet(context, ref),
          style: FilledButton.styleFrom(
            backgroundColor: Theme.of(context).colorScheme.primaryContainer,
            foregroundColor: Theme.of(context).colorScheme.onPrimaryContainer,
          ),
          icon: const Icon(Icons.add_rounded, size: 18),
          label: Text(t.deposit),
        ),
        OutlinedButton.icon(
          onPressed: () => showModalBottomSheet<void>(
            context: context,
            isScrollControlled: true,
            builder: (_) => WithdrawSheet(wallet: wallet),
          ),
          icon: const Icon(Icons.arrow_outward_rounded, size: 18),
          label: Text(t.withdraw),
        ),
      ],
    );
  }

  Future<void> _depositSheet(BuildContext context, WidgetRef ref) async {
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
            Text(t.deposit, style: Theme.of(sheetContext).textTheme.titleLarge),
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

                // The key makes a retried request safe if the response is lost in transit.
                final key = 'dep-${DateTime.now().microsecondsSinceEpoch}';

                try {
                  await ref.read(walletRepositoryProvider).deposit(amount, key);
                  ref.invalidate(walletsProvider);
                } on ApiException catch (e) {
                  if (context.mounted) {
                    ScaffoldMessenger.of(context).showSnackBar(
                      SnackBar(
                          content: Text(e.displayMessage), backgroundColor: AppTheme.urgentDeep),
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

/// Withdrawal, in two steps: enter the amount, then confirm with a code sent to the account's
/// phone or email.
///
/// The server binds the code to this exact amount and destination, so a code obtained for a small
/// transfer cannot approve a larger one, or one to a different account.
class WithdrawSheet extends ConsumerStatefulWidget {
  const WithdrawSheet({super.key, required this.wallet});

  final Wallet wallet;

  @override
  ConsumerState<WithdrawSheet> createState() => _WithdrawSheetState();
}

class _WithdrawSheetState extends ConsumerState<WithdrawSheet> {
  final _amount = TextEditingController();
  final _destination = TextEditingController();

  OtpChallenge? _challenge;
  String? _error;
  bool _busy = false;

  @override
  void dispose() {
    _amount.dispose();
    _destination.dispose();
    super.dispose();
  }

  double? get _parsedAmount => double.tryParse(_amount.text.trim());

  Future<void> _requestCode() async {
    final amount = _parsedAmount;
    if (amount == null || amount <= 0) return;

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final challenge = await ref.read(walletRepositoryProvider).requestWithdrawalOtp(
            amount,
            destination: _destination.text.trim().isEmpty ? null : _destination.text.trim(),
          );
      setState(() => _challenge = challenge);
    } on ApiException catch (e) {
      setState(() => _error = e.displayMessage);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _confirm(String code) async {
    final amount = _parsedAmount;
    if (amount == null) return;

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      await ref.read(walletRepositoryProvider).withdraw(
            amount,
            'wd-${DateTime.now().microsecondsSinceEpoch}',
            destination: _destination.text.trim().isEmpty ? null : _destination.text.trim(),
            otpCode: code,
          );

      ref.invalidate(walletsProvider);

      if (mounted) {
        Navigator.of(context).pop();
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(AppStrings.of(context).withdrawalSent)),
        );
      }
    } on ApiException catch (e) {
      setState(() => _error = e.displayMessage);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, MediaQuery.of(context).viewInsets.bottom + 20),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(_challenge == null ? t.withdraw : t.confirmWithdrawal,
              style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 8),
          Text(t.withdrawalNeedsCode, style: Theme.of(context).textTheme.bodySmall),
          const SizedBox(height: 16),

          if (_challenge == null) ...[
            TextField(
              controller: _amount,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: InputDecoration(
                labelText: t.amount,
                suffixText: widget.wallet.currency,
                helperText:
                    '${t.available}: ${Fmt.money(widget.wallet.balance, widget.wallet.currency, locale: locale)}',
              ),
              autofocus: true,
              onChanged: (_) => setState(() {}),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _destination,
              decoration: InputDecoration(labelText: t.withdrawalDestination),
            ),

            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(_error!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
            ],

            const SizedBox(height: 18),
            FilledButton(
              onPressed: _busy ||
                      _parsedAmount == null ||
                      _parsedAmount! <= 0 ||
                      _parsedAmount! > widget.wallet.balance
                  ? null
                  : _requestCode,
              child: _busy
                  ? const SizedBox(
                      height: 20, width: 20,
                      child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                  : Text(t.sendCode),
            ),
          ] else ...[
            // Restate exactly what the code authorises, so confirming is an informed act.
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: Theme.of(context).colorScheme.surfaceContainerHighest,
                borderRadius: BorderRadius.circular(10),
              ),
              child: Text(
                t.withdrawalSummary(
                  Fmt.money(_parsedAmount, widget.wallet.currency, locale: locale),
                  _destination.text.trim().isEmpty ? '-' : _destination.text.trim(),
                ),
                textAlign: TextAlign.center,
                style: const TextStyle(fontWeight: FontWeight.w600),
              ),
            ),
            const SizedBox(height: 18),
            OtpInput(
              challenge: _challenge!,
              busy: _busy,
              error: _error,
              onSubmit: _confirm,
              onResend: _requestCode,
            ),
          ],
        ],
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

    return WalletCard(
      title: t.shoppingCredit,
      amount: Fmt.money(wallet.balance, wallet.currency, locale: locale),
      accent: AppTheme.gold, icon: Icons.card_giftcard_rounded,
      // A padlock, not a tick. The badge has to read as a restriction at a glance.
      ruleLabel: t.storePurchasesOnly,
      ruleIcon: Icons.lock_rounded,
      note: t.shoppingCreditCannotBeWithdrawn,
      actions: [
        FilledButton.icon(
          onPressed: () => context.go('/store'),
          style: FilledButton.styleFrom(
            backgroundColor: AppTheme.gold,
            foregroundColor: AppTheme.onGold,
          ),
          icon: const Icon(Icons.storefront_rounded, size: 18),
          label: Text(t.shopNow),
        ),
      ],
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
                          // The sign belongs in front of the figure. Left to Arabic's own
                          // ordering, "-150.00 $" comes out as "$ 150.00-", which reads as a
                          // credit at a glance — the opposite of what it is.
                          trailing: LtrText(
                            '${tx.isCredit ? '+' : ''}${Fmt.money(tx.amount, tx.currency, locale: locale)}',
                            style: TextStyle(
                              fontWeight: FontWeight.w700,
                              fontFeatures: AppTheme.numeric,
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
