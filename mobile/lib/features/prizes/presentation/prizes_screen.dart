import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/auto_refresh.dart';
import '../../../shared/app_header.dart';
import '../../../shared/widgets.dart';
import '../data/prize_repository.dart';

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

    final page = Scaffold(
      appBar: BrandAppBar(title: Text(t.myWins)),
      body: claimsAsync.when(
        // A refresh that fails keeps the list on screen rather than swapping in an error.
        skipError: true,
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.displayMessage : e.toString(),
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
              itemBuilder: (context, i) =>
                  _ClaimCard(claim: paged.items[i], locale: locale),
            ),
          );
        },
      ),
    );

    return AutoRefresh(onRefresh: () => ref.invalidate(myClaimsProvider), child: page);
  }
}

/// One won prize: its picture and details, then what it is worth against what the winner pays.
class _ClaimCard extends StatelessWidget {
  const _ClaimCard({required this.claim, required this.locale});

  final PrizeClaim claim;
  final String locale;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return SurfaceCard(
      padding: EdgeInsets.zero,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          ClipRRect(
            borderRadius: const BorderRadius.vertical(
              top: Radius.circular(AppTheme.radiusLg),
            ),
            child: Stack(
              children: [
                RemoteImage(
                  claim.prizeImageUrl,
                  height: 170,
                  width: double.infinity,
                  radius: 0,
                ),
                PositionedDirectional(
                  top: 12,
                  start: 12,
                  child: StatusPill(
                    t.prizeRank(claim.rank),
                    color: AppTheme.gold,
                    icon: Icons.emoji_events_rounded,
                    filled: true,
                  ),
                ),
              ],
            ),
          ),
          Padding(
            padding: const EdgeInsets.all(AppTheme.space4),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  claim.prizeName,
                  style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700),
                ),
                const SizedBox(height: 2),
                Text(claim.auctionTitle, style: theme.textTheme.bodySmall),
                if (claim.prizeDescription != null &&
                    claim.prizeDescription!.trim().isNotEmpty) ...[
                  const SizedBox(height: AppTheme.space2),
                  Text(
                    claim.prizeDescription!,
                    style: theme.textTheme.bodyMedium,
                  ),
                ],
                const SizedBox(height: AppTheme.space3),
                PrizePriceBox(
                  fullPrice: claim.prizeOriginalPrice > 0 ? claim.prizeOriginalPrice : null,
                  price: claim.paymentAmount,
                  discount: claim.discount,
                  currency: claim.currency,
                  exact: true,
                ),
                const SizedBox(height: AppTheme.space3),
                Row(
                  children: [
                    StatusPill(
                      t.claimStatus(claim.status),
                      color: claim.isPaid ? AppTheme.success : AppTheme.gold,
                    ),
                    const Spacer(),
                    if (claim.needsPayment && claim.paymentDeadlineAt != null)
                      Flexible(
                        child: Text(
                          '${t.paymentRequired} · ${Fmt.dateTime(claim.paymentDeadlineAt, locale: locale)}',
                          style: theme.textTheme.bodySmall,
                          textAlign: TextAlign.end,
                        ),
                      ),
                  ],
                ),
                if (claim.needsPayment) ...[
                  const SizedBox(height: AppTheme.space3),
                  _PayButton(
                      claimId: claim.id, amount: claim.paymentAmount, currency: claim.currency),
                ],
              ],
            ),
          ),
        ],
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
          SnackBar(content: Text(e.displayMessage), backgroundColor: AppTheme.urgentDeep),
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
      appBar: BrandAppBar(title: Text(t.myOrders)),
      body: ordersAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.displayMessage : e.toString(),
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
                                        SnackBar(content: Text(e.displayMessage)),
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
