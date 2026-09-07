import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';

/// Wins and prize claims.
///
/// Winning grants the right to a prize, not the prize itself: the value is still owed, which is
/// why every claim opens in the PaymentRequired state.
class PrizesScreen extends ConsumerWidget {
  const PrizesScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final claimsAsync = ref.watch(myClaimsProvider);
    final locale = Localizations.localeOf(context).languageCode;

    return Scaffold(
      appBar: AppBar(title: Text(t.myWins)),
      body: claimsAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.message : e.toString(),
          onRetry: () => ref.invalidate(myClaimsProvider),
        ),
        data: (paged) {
          if (paged.items.isEmpty) {
            return const EmptyState(icon: Icons.emoji_events_outlined);
          }

          return RefreshIndicator(
            onRefresh: () async {
              ref.invalidate(myClaimsProvider);
              ref.invalidate(myWinsProvider);
            },
            child: ListView.separated(
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
              itemCount: paged.items.length,
              separatorBuilder: (_, _) => const SizedBox(height: 12),
              itemBuilder: (context, i) {
                final claim = paged.items[i];

                return Card(
                  child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Row(
                          children: [
                            RemoteImage(claim.prizeImageUrl, height: 56, width: 56, radius: 10),
                            const SizedBox(width: 12),
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(claim.prizeName,
                                      style: const TextStyle(fontWeight: FontWeight.w700)),
                                  Text(claim.auctionTitle,
                                      style: Theme.of(context).textTheme.bodySmall),
                                ],
                              ),
                            ),
                            CircleAvatar(
                              radius: 16,
                              backgroundColor: AppTheme.gold.withValues(alpha: 0.25),
                              child: Text('#${claim.rank}',
                                  style: const TextStyle(fontSize: 12, fontWeight: FontWeight.bold)),
                            ),
                          ],
                        ),

                        const Divider(height: 24),

                        Row(
                          mainAxisAlignment: MainAxisAlignment.spaceBetween,
                          children: [
                            Stat(
                              label: t.prizeValue,
                              value: Fmt.money(claim.paymentAmount, claim.currency, locale: locale),
                            ),
                            StatusPill(
                              claim.status,
                              color: claim.isPaid ? AppTheme.success : AppTheme.gold,
                            ),
                          ],
                        ),

                        if (claim.needsPayment) ...[
                          const SizedBox(height: 14),
                          if (claim.paymentDeadlineAt != null)
                            Padding(
                              padding: const EdgeInsets.only(bottom: 8),
                              child: Text(
                                '${t.paymentRequired} · ${Fmt.dateTime(claim.paymentDeadlineAt, locale: locale)}',
                                style: Theme.of(context).textTheme.bodySmall,
                              ),
                            ),
                          _PayButton(claimId: claim.id, amount: claim.paymentAmount, currency: claim.currency),
                        ],
                      ],
                    ),
                  ),
                );
              },
            ),
          );
        },
      ),
    );
  }
}

class _PayButton extends ConsumerStatefulWidget {
  const _PayButton({required this.claimId, required this.amount, required this.currency});

  final String claimId;
  final double amount;
  final String currency;

  @override
  ConsumerState<_PayButton> createState() => _PayButtonState();
}

class _PayButtonState extends ConsumerState<_PayButton> {
  bool _busy = false;

  Future<void> _pay() async {
    setState(() => _busy = true);

    try {
      // Prize payments come from the Main wallet; the server rejects shopping credit here.
      final key = 'prize-${widget.claimId}-${DateTime.now().microsecondsSinceEpoch}';
      await ref.read(prizeRepositoryProvider).pay(widget.claimId, key);

      ref.invalidate(myClaimsProvider);
      ref.invalidate(walletsProvider);
    } on ApiException catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(e.message), backgroundColor: AppTheme.urgentDeep),
        );
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    return FilledButton(
      onPressed: _busy ? null : _pay,
      child: _busy
          ? const SizedBox(
              height: 20, width: 20,
              child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
          : Text('${t.payForPrize} · ${Fmt.money(widget.amount, widget.currency, locale: locale)}'),
    );
  }
}

class OrdersScreen extends ConsumerWidget {
  const OrdersScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final ordersAsync = ref.watch(myOrdersProvider);

    return Scaffold(
      appBar: AppBar(title: Text(t.myOrders)),
      body: ordersAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.message : e.toString(),
          onRetry: () => ref.invalidate(myOrdersProvider),
        ),
        data: (paged) => paged.items.isEmpty
            ? const EmptyState(icon: Icons.receipt_long_outlined)
            : RefreshIndicator(
                onRefresh: () async => ref.invalidate(myOrdersProvider),
                child: ListView.separated(
                  padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
                  itemCount: paged.items.length,
                  separatorBuilder: (_, _) => const SizedBox(height: 10),
                  itemBuilder: (context, i) {
                    final o = paged.items[i];

                    return Card(
                      child: Padding(
                        padding: const EdgeInsets.all(14),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Row(
                              mainAxisAlignment: MainAxisAlignment.spaceBetween,
                              children: [
                                Text(o.orderNumber,
                                    style: const TextStyle(fontWeight: FontWeight.w700)),
                                StatusPill(o.status,
                                    color: o.status == 'Cancelled' || o.status == 'Refunded'
                                        ? Theme.of(context).colorScheme.error
                                        : AppTheme.success),
                              ],
                            ),
                            const SizedBox(height: 6),
                            Text(Fmt.dateTime(o.createdAt, locale: locale),
                                style: Theme.of(context).textTheme.bodySmall),
                            const Divider(height: 20),
                            for (final item in o.items)
                              Padding(
                                padding: const EdgeInsets.only(bottom: 4),
                                child: Row(
                                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                                  children: [
                                    Expanded(child: Text('${item.quantity} × ${item.productName}')),
                                    Text(Fmt.money(item.totalPrice, o.currency, locale: locale)),
                                  ],
                                ),
                              ),
                            const Divider(height: 20),
                            Row(
                              mainAxisAlignment: MainAxisAlignment.spaceBetween,
                              children: [
                                Text('${t.payWith}: ${o.paymentMethod == 'ShoppingWallet' ? t.shoppingCredit : t.mainWallet}',
                                    style: Theme.of(context).textTheme.bodySmall),
                                Text(Fmt.money(o.total, o.currency, locale: locale),
                                    style: const TextStyle(fontWeight: FontWeight.bold)),
                              ],
                            ),
                            if (o.isCancellable) ...[
                              const SizedBox(height: 10),
                              OutlinedButton(
                                onPressed: () async {
                                  try {
                                    await ref
                                        .read(storeRepositoryProvider)
                                        .cancelOrder(o.id, 'Cancelled by customer');
                                    ref.invalidate(myOrdersProvider);
                                    ref.invalidate(walletsProvider);
                                  } on ApiException catch (e) {
                                    if (context.mounted) {
                                      ScaffoldMessenger.of(context).showSnackBar(
                                        SnackBar(content: Text(e.message)),
                                      );
                                    }
                                  }
                                },
                                child: Text(t.cancelOrder),
                              ),
                            ],
                          ],
                        ),
                      ),
                    );
                  },
                ),
              ),
      ),
    );
  }
}
