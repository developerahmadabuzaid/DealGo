import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/app_header.dart';
import '../../../shared/widgets.dart';
import '../data/store_repository.dart';

/// Cart and checkout.
///
/// The line totals shown while browsing are a local convenience. Before paying, the app asks the
/// server for a quote and shows those figures instead: the backend decides the real total.
class CartScreen extends ConsumerStatefulWidget {
  const CartScreen({super.key});

  @override
  ConsumerState<CartScreen> createState() => _CartScreenState();
}

class _CartScreenState extends ConsumerState<CartScreen> {
  String _paymentMethod = 'ShoppingWallet';
  CheckoutQuote? _quote;
  bool _loadingQuote = false;
  bool _placing = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _refreshQuote());
  }

  Future<void> _refreshQuote() async {
    final cart = ref.read(cartProvider);
    if (cart.isEmpty) {
      setState(() => _quote = null);
      return;
    }

    setState(() {
      _loadingQuote = true;
      _error = null;
    });

    try {
      final quote = await ref.read(storeRepositoryProvider).quote(cart, _paymentMethod);
      if (mounted) setState(() => _quote = quote);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.displayMessage);
    } finally {
      if (mounted) setState(() => _loadingQuote = false);
    }
  }

  Future<void> _checkout() async {
    final t = AppStrings.of(context);
    final cart = ref.read(cartProvider);
    if (cart.isEmpty) return;

    setState(() {
      _placing = true;
      _error = null;
    });

    // One key per attempt: if the response is lost, retrying returns the same order rather
    // than charging twice.
    final key = 'order-${DateTime.now().microsecondsSinceEpoch}';

    try {
      final order = await ref.read(storeRepositoryProvider).checkout(cart, _paymentMethod, key);

      ref.read(cartProvider.notifier).clear();
      ref.invalidate(walletsProvider);
      ref.invalidate(myOrdersProvider);
      ref.invalidate(storeHomeProvider);

      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('${t.orderPlaced} · ${order.orderNumber}')),
        );
        Navigator.of(context).pop();
      }
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.displayMessage);
    } finally {
      if (mounted) setState(() => _placing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final cart = ref.watch(cartProvider);

    return Scaffold(
      appBar: BrandAppBar(title: Text(t.cart)),
      body: cart.isEmpty
          ? EmptyState(message: t.emptyCart, icon: Icons.shopping_cart_outlined)
          : ListView(
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 200),
              children: [
                for (final line in cart)
                  Card(
                    margin: const EdgeInsets.only(bottom: 10),
                    child: ListTile(
                      leading: RemoteImage(line.product.imageUrl, height: 48, width: 48, radius: 8),
                      title: Text(line.product.name),
                      subtitle: Text(Fmt.money(line.product.price, line.product.currency, locale: locale)),
                      trailing: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          IconButton(
                            onPressed: () {
                              ref.read(cartProvider.notifier)
                                  .setQuantity(line.product.id, line.quantity - 1);
                              _refreshQuote();
                            },
                            icon: const Icon(Icons.remove_circle_outline, size: 20),
                          ),
                          Text('${line.quantity}'),
                          IconButton(
                            onPressed: () {
                              ref.read(cartProvider.notifier)
                                  .setQuantity(line.product.id, line.quantity + 1);
                              _refreshQuote();
                            },
                            icon: const Icon(Icons.add_circle_outline, size: 20),
                          ),
                        ],
                      ),
                    ),
                  ),

                const SizedBox(height: AppTheme.space5),
                Text(t.payWith, style: Theme.of(context).textTheme.titleMedium),
                const SizedBox(height: AppTheme.space3),

                // Both wallets can pay here. Showing each balance beside its option is the
                // point: a bare pair of labels gave no reason to believe the main wallet was
                // usable, and no way to tell which one actually covers the total.
                _PaymentChoice(
                  selected: _paymentMethod,
                  onChanged: (m) {
                    setState(() => _paymentMethod = m);
                    _refreshQuote();
                  },
                ),

                const SizedBox(height: AppTheme.space5),

                if (_loadingQuote)
                  const Center(child: Padding(
                      padding: EdgeInsets.all(16), child: CircularProgressIndicator()))
                else if (_quote != null)
                  _QuoteCard(quote: _quote!),

                if (_error != null) ...[
                  const SizedBox(height: 12),
                  Text(_error!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
                ],
              ],
            ),
      bottomNavigationBar: cart.isEmpty
          ? null
          : SafeArea(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: FilledButton(
                  onPressed: (_placing || _quote == null || !_quote!.canAfford) ? null : _checkout,
                  child: _placing
                      ? const SizedBox(
                          height: 20, width: 20,
                          child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                      : Text(_quote == null
                          ? t.checkout
                          : '${t.checkout} · ${Fmt.money(_quote!.total, _quote!.currency, locale: locale)}'),
                ),
              ),
            ),
    );
  }
}

/// Which wallet pays.
///
/// Two balances that behave differently, so each option carries its own colour, icon and figure.
/// The main wallet is real cash; shopping credit can only be spent here. Neither is presented as
/// a default "the balance" — the user picks, knowing what each one is.
class _PaymentChoice extends ConsumerWidget {
  const _PaymentChoice({required this.selected, required this.onChanged});

  final String selected;
  final ValueChanged<String> onChanged;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final wallets = ref.watch(walletsProvider);

    return Column(
      children: [
        _option(
          context,
          value: 'ShoppingWallet',
          label: t.shoppingCredit,
          note: t.storePurchasesOnly,
          icon: Icons.card_giftcard_rounded,
          accent: AppTheme.gold,
          balance: wallets.valueOrNull == null
              ? null
              : Fmt.money(
                  wallets.value!.shopping.balance,
                  wallets.value!.shopping.currency,
                  locale: locale,
                ),
        ),
        const SizedBox(height: AppTheme.space2),
        _option(
          context,
          value: 'MainWallet',
          label: t.mainWallet,
          note: t.withdrawable,
          icon: Icons.account_balance_wallet_rounded,
          accent: Theme.of(context).colorScheme.primary,
          balance: wallets.valueOrNull == null
              ? null
              : Fmt.money(
                  wallets.value!.main.balance,
                  wallets.value!.main.currency,
                  locale: locale,
                ),
        ),
      ],
    );
  }

  Widget _option(
    BuildContext context, {
    required String value,
    required String label,
    required String note,
    required IconData icon,
    required Color accent,
    required String? balance,
  }) {
    final theme = Theme.of(context);
    final isSelected = selected == value;

    return SurfaceCard(
      accent: isSelected ? accent : null,
      padding: const EdgeInsets.symmetric(
        horizontal: AppTheme.space4,
        vertical: AppTheme.space3,
      ),
      onTap: () => onChanged(value),
      child: Row(
        children: [
          // A radio as well as the tint, so the choice is not signalled by colour alone.
          Icon(
            isSelected ? Icons.radio_button_checked_rounded : Icons.radio_button_unchecked_rounded,
            size: 20,
            color: isSelected ? accent : theme.colorScheme.outline,
          ),
          const SizedBox(width: AppTheme.space3),
          Icon(icon, size: 19, color: accent),
          const SizedBox(width: AppTheme.space2),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  label,
                  style: theme.textTheme.titleSmall?.copyWith(
                    fontWeight: isSelected ? FontWeight.w700 : FontWeight.w600,
                  ),
                ),
                Text(
                  note,
                  style: theme.textTheme.labelSmall?.copyWith(fontSize: 11),
                ),
              ],
            ),
          ),
          if (balance == null)
            const Skeleton(height: 16, width: 70)
          else
            Text(
              balance,
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w800,
                fontFeatures: AppTheme.numeric,
              ),
            ),
        ],
      ),
    );
  }
}

/// The server's numbers, shown verbatim.
class _QuoteCard extends StatelessWidget {
  const _QuoteCard({required this.quote});

  final CheckoutQuote quote;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    Widget row(String label, double value, {bool bold = false}) => Padding(
          padding: const EdgeInsets.symmetric(vertical: 4),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Text(label,
                  style: TextStyle(fontWeight: bold ? FontWeight.bold : FontWeight.normal)),
              Text(Fmt.money(value, quote.currency, locale: locale),
                  style: TextStyle(fontWeight: bold ? FontWeight.bold : FontWeight.w500)),
            ],
          ),
        );

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          children: [
            row(t.subtotal, quote.subtotal),
            if (quote.discount > 0) row(t.discount, -quote.discount),
            if (quote.shippingFee > 0) row(t.shipping, quote.shippingFee),
            if (quote.tax > 0) row(t.tax, quote.tax),
            const Divider(height: 20),
            row(t.total, quote.total, bold: true),
            const SizedBox(height: 12),
            row(t.available, quote.availableBalance),
            row(quote.canAfford ? t.remaining : t.required,
                quote.canAfford ? quote.remainingAfterPurchase : quote.total - quote.availableBalance),
            if (!quote.canAfford) ...[
              const SizedBox(height: 10),
              StatusPill(t.insufficientCredit, color: Theme.of(context).colorScheme.error),
            ],
          ],
        ),
      ),
    );
  }
}

extension on AppStrings {
  String get insufficientCredit =>
      isArabic ? 'الرصيد غير كافٍ' : 'Not enough balance';
}
