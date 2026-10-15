import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/auto_refresh.dart';
import '../../../shared/art_fallback.dart';
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
      appBar: PreferredSize(
        preferredSize: const Size.fromHeight(46),
        child: Material(
          color: Theme.of(context).colorScheme.surface,
          child: TabBar(
            controller: _tabs,
            tabs: [
              Tab(text: t.liveAuctions),
              Tab(text: t.upcomingAuctions),
              Tab(text: t.completedAuctions),
            ],
          ),
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

    final list = async.when(
      // A refresh that fails keeps the last list on screen rather than swapping in an error.
      skipError: true,
      // Cards in outline, so the list does not jump when the real ones arrive.
      loading: () => ListView.separated(
        padding: const EdgeInsets.fromLTRB(
            AppTheme.space4, AppTheme.space4, AppTheme.space4, AppTheme.space8),
        itemCount: 3,
        separatorBuilder: (_, _) => const SizedBox(height: AppTheme.space3),
        itemBuilder: (_, _) => const SkeletonCard(),
      ),
      error: (e, _) => ErrorView(
        message: e is ApiException ? e.displayMessage : e.toString(),
        onRetry: () => ref.invalidate(auctionListProvider(group)),
      ),
      data: (paged) {
        if (paged.items.isEmpty) {
          return EmptyState(
            icon: Icons.gavel_rounded,
            title: AppStrings.of(context).nothingHere,
          );
        }

        return RefreshIndicator(
          onRefresh: () async => ref.invalidate(auctionListProvider(group)),
          child: ListView.separated(
            padding: const EdgeInsets.fromLTRB(
                AppTheme.space4, AppTheme.space4, AppTheme.space4, AppTheme.space8),
            itemCount: paged.items.length,
            separatorBuilder: (_, _) => const SizedBox(height: AppTheme.space3),
            itemBuilder: (context, i) => AuctionCard(auction: paged.items[i]),
          ),
        );
      },
    );

    return AutoRefresh(
      onRefresh: () => ref.invalidate(auctionListProvider(group)),
      child: list,
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

    final accent = AppTheme.statusColor(
      theme.brightness,
      isLive: auction.isLive,
      isFinal: auction.isFinalPhase,
    );

    // The price that matters depends on the phase: before it starts, what it opens at; once it
    // is running, where it stands now.
    final headlineLabel = auction.isLive ? t.currentPrice : t.startingPrice;
    final headlineAmount = Fmt.money(
      auction.isLive ? auction.currentPrice : auction.startingPrice,
      auction.currency,
      locale: locale,
    );

    return SurfaceCard(
      padding: EdgeInsets.zero,
      accent: auction.isLive ? accent : null,
      onTap: () => context.push('/auctions/${auction.id}'),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          ClipRRect(
            borderRadius: const BorderRadius.vertical(top: Radius.circular(AppTheme.radiusLg)),
            child: Stack(
              children: [
                // The auction's own picture; the first prize's picture when it has none.
                SizedBox(
                  height: 156,
                  width: double.infinity,
                  child: ArtOrPicture(imageUrl: auction.imageUrl, seed: auction.auctionNumber),
                ),

                // A scrim under the badge so it stays legible over any photograph.
                Positioned.fill(
                  child: DecoratedBox(
                    decoration: const BoxDecoration(
                      gradient: LinearGradient(
                        begin: Alignment.topCenter,
                        end: Alignment.bottomCenter,
                        colors: [Color(0x59000000), Color(0x00000000)],
                        stops: [0, 0.55],
                      ),
                    ),
                  ),
                ),

                PositionedDirectional(top: 12, start: 12, child: _statusPill(context, t)),
              ],
            ),
          ),
          Padding(
            padding: const EdgeInsets.all(AppTheme.space4),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  auction.title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700),
                ),
                const SizedBox(height: 2),
                Text(
                  auction.mainProductName,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: theme.textTheme.bodySmall,
                ),

                const SizedBox(height: AppTheme.space4),

                // The live price is the one figure that gets size and colour. The other two are
                // context, so they stay quiet and keep the card from reading as a data table.
                Row(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  children: [
                    Expanded(
                      flex: 5,
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            headlineLabel.toUpperCase(),
                            style: theme.textTheme.labelSmall
                                ?.copyWith(fontSize: 10.5, letterSpacing: 0.7),
                          ),
                          const SizedBox(height: 1),
                          FittedBox(
                            fit: BoxFit.scaleDown,
                            alignment: AlignmentDirectional.centerStart,
                            child: Text(
                              headlineAmount,
                              style: theme.textTheme.headlineSmall?.copyWith(
                                fontSize: 23,
                                color: auction.isLive ? accent : theme.colorScheme.onSurface,
                                fontFeatures: AppTheme.numeric,
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                    const SizedBox(width: AppTheme.space2),
                    Expanded(
                      flex: 4,
                      child: Stat(
                        label: t.targetPrice,
                        value: Fmt.money(auction.targetPrice, auction.currency, locale: locale),
                      ),
                    ),
                    const SizedBox(width: AppTheme.space2),
                    Expanded(
                      flex: 4,
                      child: Stat(
                        label: t.entryFee,
                        value: Fmt.money(auction.entryFee, auction.currency, locale: locale),
                      ),
                    ),
                  ],
                ),

                const SizedBox(height: AppTheme.space4),

                // Before the threshold, filling the participant bar is the story. The bar alone:
                // the app does not show participant numbers.
                if (auction.acceptsRegistration) ...[
                  Row(
                    children: [
                      Icon(Icons.group_rounded,
                          size: 15, color: theme.colorScheme.onSurfaceVariant),
                      const SizedBox(width: 5),
                      Text(t.participantsFilling, style: theme.textTheme.bodySmall),
                    ],
                  ),
                  const SizedBox(height: AppTheme.space2),
                  TargetProgressBar(progress: auction.fillProgress, height: 7),
                ] else if (auction.isLive) ...[
                  TargetProgressBar(
                    progress: auction.targetPrice > auction.startingPrice
                        ? (auction.currentPrice - auction.startingPrice) /
                            (auction.targetPrice - auction.startingPrice)
                        : 1,
                    urgent: auction.isFinalPhase,
                    height: 7,
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }

  /// The badge carries an icon and a word as well as a colour, so the state is never signalled
  /// by hue alone.
  Widget _statusPill(BuildContext context, AppStrings t) {
    if (auction.isFinalPhase) {
      return StatusPill(t.finalPhase,
          color: AppTheme.finalPhase,
          icon: Icons.local_fire_department_rounded,
          filled: true,
          pulse: true);
    }
    if (auction.isLive) {
      return StatusPill(t.liveAuctions,
          color: AppTheme.live, icon: Icons.circle, filled: true, pulse: true);
    }
    if (auction.isComplete) {
      return StatusPill(t.auctionFinished,
          color: Theme.of(context).colorScheme.onSurfaceVariant,
          icon: Icons.check_circle_rounded);
    }
    return StatusPill(t.upcomingAuctions,
        color: Theme.of(context).colorScheme.primary,
        icon: Icons.schedule_rounded,
        filled: true);
  }
}
