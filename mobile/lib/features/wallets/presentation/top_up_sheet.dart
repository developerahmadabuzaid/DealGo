import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../data/wallet_repository.dart';

/// Opens the top-up sheet. With [amount] it starts filled in, and [reason] says above it what the
/// money is for. Resolves true once the payment is confirmed, false if the sheet was closed first.
Future<bool> showTopUpSheet(
  BuildContext context, {
  required String currency,
  double? amount,
  String? reason,
}) async {
  final done = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    builder: (_) => TopUpSheet(currency: currency, initialAmount: amount, reason: reason),
  );
  return done == true;
}

/// Makes sure the Main wallet holds [amount] before something is paid from it.
///
/// When it falls short, the top-up sheet opens filled in with what is missing (at least the
/// minimum top-up), so one tap on a pay button leads straight to paying. True means go ahead: the
/// money is there, or was just added. False means the person closed the sheet without paying.
///
/// The balance is read fresh from the server. If it cannot be read, this says go ahead and lets
/// the payment itself decide; the server still refuses one the wallet cannot cover.
Future<bool> ensureMainBalance(
  BuildContext context,
  WidgetRef ref, {
  required double amount,
  required String currency,
}) async {
  final Wallets wallets;
  try {
    wallets = await ref.read(walletRepositoryProvider).get();
  } on ApiException {
    return true;
  }

  final missing = amount - wallets.main.balance;
  if (missing <= 0.004) return true;
  if (!context.mounted) return false;

  final t = AppStrings.of(context);
  final locale = Localizations.localeOf(context).languageCode;
  final topUp = math.max(missing.ceilToDouble(), TopUpSheet.minimum);

  return showTopUpSheet(
    context,
    currency: currency,
    amount: topUp,
    reason: t.topUpToPay(
      Fmt.money(amount, currency, locale: locale),
      Fmt.money(wallets.main.balance, currency, locale: locale),
    ),
  );
}

/// Tops up the Main wallet with a card, mada or Apple Pay, paid on Paymob's own page.
///
/// The app never marks a top-up as paid: it opens Paymob's checkout in the phone's browser (Apple
/// Pay works there and not inside the app), then waits, asking the server every few seconds and
/// again whenever the app comes back to the front, until Paymob has confirmed the payment to the
/// server, or said it failed.
class TopUpSheet extends ConsumerStatefulWidget {
  const TopUpSheet({super.key, required this.currency, this.initialAmount, this.reason});

  final String currency;

  /// Filled in when the sheet opens, so paying for something takes one tap.
  final double? initialAmount;

  /// What the money is for, shown at the top: "you need 150, your balance is 40".
  final String? reason;

  static const minimum = 10.0;
  static const quickAmounts = [50.0, 100.0, 200.0, 500.0];

  @override
  ConsumerState<TopUpSheet> createState() => _TopUpSheetState();
}

class _TopUpSheetState extends ConsumerState<TopUpSheet> with WidgetsBindingObserver {
  final _amount = TextEditingController();

  TopUp? _topUp;
  bool _busy = false;
  bool _failed = false;
  String? _error;
  Timer? _poll;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    final amount = widget.initialAmount;
    if (amount != null && amount > 0) {
      _amount.text = amount == amount.roundToDouble()
          ? amount.toStringAsFixed(0)
          : amount.toStringAsFixed(2);
    }
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _poll?.cancel();
    _amount.dispose();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // Back from the browser: the answer is most likely in by now.
    if (state == AppLifecycleState.resumed && _topUp != null) _check();
  }

  double? get _parsed => double.tryParse(_amount.text.trim().replaceAll(',', '.'));

  Future<void> _start() async {
    final t = AppStrings.of(context);
    final amount = _parsed;
    if (amount == null || amount < TopUpSheet.minimum) {
      setState(() => _error = t.topUpMinimum(Fmt.money(TopUpSheet.minimum, widget.currency)));
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
      _failed = false;
    });

    try {
      final topUp = await ref.read(walletRepositoryProvider).startTopUp(amount);
      if (!mounted) return;
      setState(() => _topUp = topUp);
      // Checking starts before the page opens: opening it may not return until the customer comes
      // back, and the answer can arrive before then.
      _poll?.cancel();
      _poll = Timer.periodic(const Duration(seconds: 4), (_) => _check());
      unawaited(_openCheckout());
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.displayMessage);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _openCheckout() async {
    final url = _topUp?.checkoutUrl;
    if (url == null) return;
    try {
      await launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);
    } catch (_) {
      // No browser answered. The sheet keeps waiting and offers to open the page again.
    }
  }

  Future<void> _check() async {
    final topUp = _topUp;
    if (topUp == null || !mounted) return;

    try {
      final status = await ref.read(walletRepositoryProvider).topUpStatus(topUp.paymentId);
      if (!mounted) return;

      if (status.isDone) {
        _poll?.cancel();
        ref.invalidate(walletsProvider);
        final t = AppStrings.of(context);
        final messenger = ScaffoldMessenger.of(context);
        // True tells whoever opened the sheet that the money is in, so it can carry on paying.
        Navigator.of(context).pop(true);
        messenger.showSnackBar(SnackBar(
          content: Text(t.topUpDone(Fmt.money(topUp.amount, topUp.currency))),
          backgroundColor: AppTheme.success,
        ));
      } else if (status.isFailed) {
        _poll?.cancel();
        setState(() {
          _failed = true;
          _topUp = null;
        });
      }
    } on ApiException {
      // A missed check is not an answer; the next one will ask again.
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final waiting = _topUp != null;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, MediaQuery.of(context).viewInsets.bottom + 20),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(t.topUpTitle, style: theme.textTheme.titleLarge),
          const SizedBox(height: 6),
          Text(t.topUpHint, style: theme.textTheme.bodySmall),
          if (widget.reason != null) ...[
            const SizedBox(height: 12),
            Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: AppTheme.brand.withValues(alpha: 0.08),
                borderRadius: BorderRadius.circular(AppTheme.radiusMd),
                border: Border.all(color: AppTheme.brand.withValues(alpha: 0.35)),
              ),
              child: Row(
                children: [
                  const Icon(Icons.info_outline_rounded, size: 18, color: AppTheme.brand),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      widget.reason!,
                      style: theme.textTheme.bodySmall?.copyWith(fontWeight: FontWeight.w700),
                    ),
                  ),
                ],
              ),
            ),
          ],
          const SizedBox(height: 16),
          if (waiting) ...[
            const Center(child: CircularProgressIndicator()),
            const SizedBox(height: 14),
            Text(t.waitingForPayment,
                textAlign: TextAlign.center,
                style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700)),
            const SizedBox(height: 6),
            Text(t.waitingForPaymentHint, textAlign: TextAlign.center, style: theme.textTheme.bodySmall),
            const SizedBox(height: 16),
            OutlinedButton.icon(
              onPressed: _openCheckout,
              icon: const Icon(Icons.open_in_new_rounded, size: 18),
              label: Text(t.openPaymentAgain),
            ),
          ] else ...[
            if (_failed) ...[
              Text(t.topUpFailed,
                  textAlign: TextAlign.center, style: TextStyle(color: theme.colorScheme.error)),
              const SizedBox(height: 12),
            ],
            TextField(
              controller: _amount,
              enabled: !_busy,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: InputDecoration(labelText: t.amount, suffixText: widget.currency),
              autofocus: widget.initialAmount == null,
              onChanged: (_) => setState(() => _error = null),
            ),
            const SizedBox(height: 10),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final a in TopUpSheet.quickAmounts)
                  ActionChip(
                    label: Text(Fmt.money(a, widget.currency)),
                    onPressed: _busy
                        ? null
                        : () => setState(() {
                              _amount.text = a.toStringAsFixed(0);
                              _error = null;
                            }),
                  ),
              ],
            ),
            if (_error != null) ...[
              const SizedBox(height: 10),
              Text(_error!, style: TextStyle(color: theme.colorScheme.error)),
            ],
            const SizedBox(height: 18),
            FilledButton.icon(
              onPressed: _busy ? null : _start,
              icon: _busy
                  ? const SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                  : const Icon(Icons.lock_rounded, size: 18),
              label: Text(_failed ? t.tryAgain : t.payNow),
            ),
          ],
        ],
      ),
    );
  }
}
