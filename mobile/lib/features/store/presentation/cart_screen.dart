import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
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
      if (mounted) setState(() => _error = e.message);
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
      if (mounted) setState(() => _error = e.message);
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
      appBar: AppBar(title: Text(t.cart)),
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

                const SizedBox(height: 12),
                Text(t.payWith,
                    style: Theme.of(context).textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700)),
                const SizedBox(height: 8),

                SegmentedButton<String>(
                  segments: [
                    ButtonSegment(value: 'ShoppingWallet', label: Text(t.shoppingCredit)),
                    ButtonSegment(value: 'MainWallet', label: Text(t.mainWallet)),
                  ],
                  selected: {_paymentMethod},
                  onSelectionChanged: (s) {
                    setState(() => _paymentMethod = s.first);
                    _refreshQuote();
                  },
                ),

                const SizedBox(height: 18),

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
