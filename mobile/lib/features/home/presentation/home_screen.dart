import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';
import '../../auctions/presentation/auctions_screen.dart';

class HomeScreen extends ConsumerWidget {
  const HomeScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final user = ref.watch(authControllerProvider).user;
    final wallets = ref.watch(walletsProvider);
    final unread = ref.watch(unreadCountProvider);

    return Scaffold(
      appBar: AppBar(
        title: Text(t.appName),
        actions: [
          IconButton(
            onPressed: () => context.push('/notifications'),
            icon: Badge(
              isLabelVisible: (unread.valueOrNull ?? 0) > 0,
              label: Text('${unread.valueOrNull ?? 0}'),
              child: const Icon(Icons.notifications_outlined),
            ),
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(walletsProvider);
          ref.invalidate(auctionListProvider('live'));
          ref.invalidate(auctionListProvider('upcoming'));
          ref.invalidate(unreadCountProvider);
        },
        child: ListView(
          padding: const EdgeInsets.only(bottom: 32),
          children: [
            if (user != null)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
                child: Text(user.fullName,
                    style: Theme.of(context)
                        .textTheme
                        .titleLarge
                        ?.copyWith(fontWeight: FontWeight.bold)),
              ),

            // Two balances, never summed into one figure.
            wallets.when(
              loading: () => const SizedBox(height: 96),
              error: (_, _) => const SizedBox.shrink(),
              data: (w) => Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
                child: Row(
                  children: [
                    Expanded(
                      child: _BalanceTile(
                        icon: Icons.account_balance_wallet_rounded,
                        label: t.mainWallet,
                        value: Fmt.money(w.main.balance, w.main.currency, locale: locale),
                        note: t.withdrawable,
                        color: Theme.of(context).colorScheme.primary,
                        onTap: () => context.go('/wallet'),
                      ),
                    ),
                    const SizedBox(width: 10),
                    Expanded(
                      child: _BalanceTile(
                        icon: Icons.card_giftcard_rounded,
                        label: t.shoppingCredit,
                        value: Fmt.money(w.shopping.balance, w.shopping.currency, locale: locale),
                        note: t.storePurchasesOnly,
                        color: AppTheme.gold,
                        onTap: () => context.go('/store'),
                      ),
                    ),
                  ],
                ),
              ),
            ),

            _AuctionSection(group: 'live', title: t.liveAuctions),
            _AuctionSection(group: 'upcoming', title: t.upcomingAuctions),
          ],
        ),
      ),
    );
  }
}

class _BalanceTile extends StatelessWidget {
  const _BalanceTile({
    required this.icon,
    required this.label,
    required this.value,
    required this.note,
    required this.color,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final String value;
  final String note;
  final Color color;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Card(
      color: color.withValues(alpha: 0.09),
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(14),
        child: Padding(
          padding: const EdgeInsets.all(14),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(icon, color: color, size: 20),
              const SizedBox(height: 8),
              Text(label, style: Theme.of(context).textTheme.bodySmall),
              const SizedBox(height: 2),
              Text(value,
                  style: Theme.of(context)
                      .textTheme
                      .titleMedium
                      ?.copyWith(fontWeight: FontWeight.bold)),
              const SizedBox(height: 4),
              Text(note,
                  style: Theme.of(context)
                      .textTheme
                      .labelSmall
                      ?.copyWith(color: Theme.of(context).colorScheme.onSurfaceVariant)),
            ],
          ),
        ),
      ),
    );
  }
}

class _AuctionSection extends ConsumerWidget {
  const _AuctionSection({required this.group, required this.title});

  final String group;
  final String title;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(auctionListProvider(group));

    return async.when(
      loading: () => const Padding(
          padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
      error: (_, _) => const SizedBox.shrink(),
      data: (paged) {
        if (paged.items.isEmpty) return const SizedBox.shrink();

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            SectionHeader(title,
                action: TextButton(
                  onPressed: () => context.go('/auctions'),
                  child: const Icon(Icons.chevron_right_rounded),
                )),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16),
              child: Column(
                children: [
                  for (final auction in paged.items.take(3))
                    Padding(
                      padding: const EdgeInsets.only(bottom: 12),
                      child: AuctionCard(auction: auction),
                    ),
                ],
              ),
            ),
          ],
        );
      },
    );
  }
}
