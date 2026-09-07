import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';
import '../data/auction_models.dart';

class AuctionsScreen extends ConsumerStatefulWidget {
  const AuctionsScreen({super.key});

  @override
  ConsumerState<AuctionsScreen> createState() => _AuctionsScreenState();
}

class _AuctionsScreenState extends ConsumerState<AuctionsScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(length: 3, vsync: this);

  @override
  void dispose() {
    _tabs.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    return Scaffold(
      appBar: AppBar(
        title: Text(t.auctions),
        bottom: TabBar(
          controller: _tabs,
          tabs: [
            Tab(text: t.liveAuctions),
            Tab(text: t.upcomingAuctions),
            Tab(text: t.completedAuctions),
          ],
        ),
      ),
      body: TabBarView(
        controller: _tabs,
        children: const [
          _AuctionList(group: 'live'),
          _AuctionList(group: 'upcoming'),
          _AuctionList(group: 'completed'),
        ],
      ),
    );
  }
}

class _AuctionList extends ConsumerWidget {
  const _AuctionList({required this.group});

  final String group;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(auctionListProvider(group));

    return async.when(
      loading: () => const Center(child: CircularProgressIndicator()),
      error: (e, _) => ErrorView(
        message: e.toString(),
        onRetry: () => ref.invalidate(auctionListProvider(group)),
      ),
      data: (paged) {
        if (paged.items.isEmpty) return const EmptyState(icon: Icons.gavel_rounded);

        return RefreshIndicator(
          onRefresh: () async => ref.invalidate(auctionListProvider(group)),
          child: ListView.separated(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
            itemCount: paged.items.length,
            separatorBuilder: (_, _) => const SizedBox(height: 12),
            itemBuilder: (context, i) => AuctionCard(auction: paged.items[i]),
          ),
        );
      },
    );
  }
}

class AuctionCard extends StatelessWidget {
  const AuctionCard({super.key, required this.auction});

  final AuctionSummary auction;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: () => context.push('/auctions/${auction.id}'),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Stack(
              children: [
                RemoteImage(auction.mainImageUrl, height: 150, width: double.infinity, radius: 0),
                Positioned(
                  top: 10,
                  left: 10,
                  child: _statusPill(context, t),
                ),
              ],
            ),
            Padding(
              padding: const EdgeInsets.all(14),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(auction.title,
                      style: theme.textTheme.titleMedium
                          ?.copyWith(fontWeight: FontWeight.w700)),
                  const SizedBox(height: 2),
                  Text(auction.mainProductName,
                      style: theme.textTheme.bodySmall
                          ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
                  const SizedBox(height: 14),

                  Row(
                    children: [
                      Expanded(
                        child: Stat(
                          label: auction.isLive ? t.currentPrice : t.startingPrice,
                          value: Fmt.money(
                              auction.isLive ? auction.currentPrice : auction.startingPrice,
                              auction.currency,
                              locale: locale),
                          valueColor: auction.isLive ? AppTheme.urgent : null,
                        ),
                      ),
                      Expanded(
                        child: Stat(
                          label: t.targetPrice,
                          value: Fmt.money(auction.targetPrice, auction.currency, locale: locale),
                        ),
                      ),
                      Expanded(
                        child: Stat(
                          label: t.entryFee,
                          value: Fmt.money(auction.entryFee, auction.currency, locale: locale),
                        ),
                      ),
                    ],
                  ),

                  const SizedBox(height: 14),

                  // Before the threshold, filling the participant bar is the story.
                  if (auction.acceptsRegistration) ...[
                    Row(
                      mainAxisAlignment: MainAxisAlignment.spaceBetween,
                      children: [
                        Text('${auction.participantCount} / ${auction.requiredParticipants}',
                            style: theme.textTheme.bodyMedium
                                ?.copyWith(fontWeight: FontWeight.w600)),
                        Text(t.participantsRemaining(auction.participantsRemaining),
                            style: theme.textTheme.bodySmall
                                ?.copyWith(color: theme.colorScheme.onSurfaceVariant)),
                      ],
                    ),
                    const SizedBox(height: 6),
                    TargetProgressBar(progress: auction.fillProgress),
                  ] else if (auction.isLive) ...[
                    TargetProgressBar(
                      progress: auction.targetPrice > auction.startingPrice
                          ? (auction.currentPrice - auction.startingPrice) /
                              (auction.targetPrice - auction.startingPrice)
                          : 1,
                      urgent: auction.isFinalPhase,
                    ),
                  ],
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _statusPill(BuildContext context, AppStrings t) {
    if (auction.isFinalPhase) {
      return StatusPill('🔥 ${t.finalPhase}', color: AppTheme.urgentDeep, icon: null);
    }
    if (auction.isLive) {
      return StatusPill(t.liveAuctions, color: AppTheme.urgent, icon: Icons.circle);
    }
    if (auction.isComplete) {
      return StatusPill(t.auctionFinished, color: Colors.grey);
    }
    return StatusPill(t.upcomingAuctions, color: Theme.of(context).colorScheme.primary);
  }
}
