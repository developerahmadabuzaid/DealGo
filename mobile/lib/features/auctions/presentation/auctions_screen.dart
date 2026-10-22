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
import 'ended_auction_screen.dart';

class AuctionsScreen extends ConsumerStatefulWidget {
  const AuctionsScreen({super.key, this.initialTab});

  /// Which list opens first: `live`, `upcoming` or `completed`.
  final String? initialTab;

  static int _indexOf(String? tab) => switch (tab) { 'upcoming' => 1, 'completed' => 2, _ => 0 };

  @override
  ConsumerState<AuctionsScreen> createState() => _AuctionsScreenState();
}

class _AuctionsScreenState extends ConsumerState<AuctionsScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabs = TabController(
      length: 3, vsync: this, initialIndex: AuctionsScreen._indexOf(widget.initialTab));

  @override
  void didUpdateWidget(covariant AuctionsScreen oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Opened again from a link that names a list, while this tab was already alive.
    if (widget.initialTab != null && widget.initialTab != oldWidget.initialTab) {
      _tabs.animateTo(AuctionsScreen._indexOf(widget.initialTab));
    }
  }

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
            itemBuilder: (context, i) => group == 'completed'
                ? EndedAuctionCard(auction: paged.items[i])
                : AuctionCard(auction: paged.items[i]),
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

/// One running or upcoming auction in a list, laid out like a finished one: the picture on a dark
/// tile with the auction's state across its corner, and beside it the name, the price that matters
/// now, and one figure of context.
class AuctionCard extends StatelessWidget {
  const AuctionCard({super.key, required this.auction});

  final AuctionSummary auction;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final (ribbon, ribbonColor) = _ribbon(context, t);

    // Before it starts, what it opens at; once it is running, where it stands now.
    final priceLabel = auction.isLive ? t.currentPrice : t.startingPrice;
    final price = auction.isLive ? auction.currentPrice : auction.startingPrice;

    // The second figure: how far it can go while it runs, what it costs to join before.
    final sideLabel = auction.isLive ? t.targetPrice : t.entryFee;
    final side = auction.isLive ? auction.targetPrice : auction.entryFee;

    final progress = auction.isLive
        ? (auction.targetPrice > auction.startingPrice
            ? ((auction.currentPrice - auction.startingPrice) /
                    (auction.targetPrice - auction.startingPrice))
                .clamp(0.0, 1.0)
            : 1.0)
        : auction.fillProgress;

    return Stack(
      children: [
        SurfaceCard(
          padding: const EdgeInsets.all(AppTheme.space3),
          onTap: () => context.push('/auctions/${auction.id}'),
          child: IntrinsicHeight(
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                ClipRRect(
                  borderRadius: BorderRadius.circular(AppTheme.radiusMd),
                  child: Container(
                    width: 96,
                    constraints: const BoxConstraints(minHeight: 96),
                    color: const Color(0xFF0B0C0E),
                    child: ArtOrPicture(imageUrl: auction.imageUrl, seed: auction.id),
                  ),
                ),
                const SizedBox(width: AppTheme.space3),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        auction.title,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w800),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        auction.mainProductName,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.bodySmall,
                      ),
                      const SizedBox(height: AppTheme.space2),
                      Divider(height: 1, color: theme.colorScheme.outline),
                      const SizedBox(height: AppTheme.space2),
                      Row(
                        crossAxisAlignment: CrossAxisAlignment.end,
                        children: [
                          Expanded(
                            child: _Figure(
                              label: priceLabel,
                              value: Fmt.money(price, auction.currency, locale: locale),
                              color: auction.isLive ? ribbonColor : AppTheme.brand,
                            ),
                          ),
                          const SizedBox(width: AppTheme.space2),
                          Flexible(
                            child: _Figure(
                              label: sideLabel,
                              value: Fmt.money(side, auction.currency, locale: locale),
                              end: true,
                            ),
                          ),
                        ],
                      ),
                      const SizedBox(height: AppTheme.space2),
                      // Filling up before it starts; climbing toward the target once it runs.
                      TargetProgressBar(
                        progress: progress,
                        urgent: auction.isFinalPhase,
                        height: 5,
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
        ),
        PositionedDirectional(
          top: 0,
          start: 0,
          child: ClipRRect(
            borderRadius: const BorderRadiusDirectional.only(
              topStart: Radius.circular(AppTheme.radiusLg),
            ).resolve(Directionality.of(context)),
            child: CornerRibbon(label: ribbon, size: 58, color: ribbonColor),
          ),
        ),
      ],
    );
  }

  /// The state, as a word and a colour, so it is never signalled by hue alone.
  (String, Color) _ribbon(BuildContext context, AppStrings t) {
    if (auction.isFinalPhase) return (t.ribbonFinal, AppTheme.finalPhase);
    if (auction.isLive) return (t.ribbonLive, AppTheme.live);
    // Not started yet, but the user is already in it: that is the fact worth showing.
    if (auction.hasJoined) return (t.joinedBadge, AppTheme.success);
    return (t.ribbonUpcoming, Theme.of(context).colorScheme.primary);
  }
}

/// A small label over a figure, as the finished-auction card has them.
class _Figure extends StatelessWidget {
  const _Figure({required this.label, required this.value, this.color, this.end = false});

  final String label;
  final String value;
  final Color? color;
  final bool end;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: end ? CrossAxisAlignment.end : CrossAxisAlignment.start,
      children: [
        Text(label, style: theme.textTheme.labelSmall),
        const SizedBox(height: 2),
        FittedBox(
          fit: BoxFit.scaleDown,
          alignment: end ? AlignmentDirectional.centerEnd : AlignmentDirectional.centerStart,
          child: Text(
            value,
            style: theme.textTheme.titleSmall?.copyWith(
              color: color ?? theme.colorScheme.onSurface,
              fontWeight: FontWeight.w800,
              fontFeatures: AppTheme.numeric,
            ),
          ),
        ),
      ],
    );
  }
}