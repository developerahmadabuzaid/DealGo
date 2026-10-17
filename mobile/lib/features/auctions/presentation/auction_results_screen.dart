import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/app_header.dart';
import '../../../shared/widgets.dart';
import '../data/auction_models.dart';

/// Every participant's final place, in full.
final auctionStandingsProvider = FutureProvider.autoDispose.family(
    (ref, String id) => ref.watch(auctionRepositoryProvider).leaderboard(id, take: 100));

/// The results of a finished auction: how the user did, who won what, and where everyone ended.
///
/// The user's own result comes first, because it is what they opened the page to learn — their
/// place out of how many, and what follows from it: a prize to pay for, or an entry fee returned as
/// store credit. Then the top three on a podium, the one flourish here, and the complete standings
/// with the user's row marked, so a sixth place is a place on a list rather than "you did not win".
class AuctionResultsScreen extends ConsumerWidget {
  const AuctionResultsScreen({super.key, required this.auctionId});

  final String auctionId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final detailAsync = ref.watch(auctionDetailProvider(auctionId));
    final winnersAsync = ref.watch(auctionWinnersProvider(auctionId));
    final standingsAsync = ref.watch(auctionStandingsProvider(auctionId));

    Future<void> refresh() async {
      ref.invalidate(auctionDetailProvider(auctionId));
      ref.invalidate(auctionWinnersProvider(auctionId));
      ref.invalidate(auctionStandingsProvider(auctionId));
    }

    final loading = detailAsync.isLoading || winnersAsync.isLoading || standingsAsync.isLoading;
    final error = detailAsync.error ?? standingsAsync.error;

    Widget body;
    if (detailAsync.hasValue && standingsAsync.hasValue) {
      body = _Results(
        detail: detailAsync.requireValue,
        winners: winnersAsync.valueOrNull ?? const [],
        standings: standingsAsync.requireValue,
      );
    } else if (error != null && !loading) {
      body = ErrorView(
        message: error is ApiException ? error.displayMessage : error.toString(),
        onRetry: refresh,
      );
    } else {
      body = const Center(child: CircularProgressIndicator());
    }

    return Scaffold(
      appBar: BrandAppBar(title: Text(t.auctionResults)),
      body: RefreshIndicator(onRefresh: refresh, child: body),
    );
  }
}

class _Results extends StatelessWidget {
  const _Results({required this.detail, required this.winners, required this.standings});

  final AuctionDetail detail;
  final List<AuctionWinner> winners;
  final List<LeaderboardEntry> standings;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    final bidders = standings.where((e) => e.bidCount > 0).toList()
      ..sort((a, b) => a.rank.compareTo(b.rank));
    final silent = detail.participantCount - bidders.length;
    final prizeByRank = {for (final w in winners) w.rank: w};
    final totalBids = bidders.fold<int>(0, (sum, e) => sum + e.bidCount);

    return ListView(
      padding: const EdgeInsets.fromLTRB(AppTheme.space4, AppTheme.space4, AppTheme.space4, 40),
      children: [
        Text(detail.title, style: theme.textTheme.headlineSmall),
        const SizedBox(height: 2),
        LtrText(detail.auctionNumber, style: theme.textTheme.bodySmall),
        const SizedBox(height: AppTheme.space4),

        // The figures of the auction as a whole.
        SurfaceCard(
          child: Row(
            children: [
              Expanded(
                child: Stat(
                  label: t.closingPrice,
                  value: Fmt.money(detail.currentPrice, detail.currency, locale: locale),
                  align: CrossAxisAlignment.center,
                ),
              ),
              Expanded(
                child: Stat(
                  label: t.participants,
                  value: '${detail.participantCount}',
                  align: CrossAxisAlignment.center,
                  ltrValue: true,
                ),
              ),
              Expanded(
                child: Stat(
                  label: t.totalBids,
                  value: '$totalBids',
                  align: CrossAxisAlignment.center,
                  ltrValue: true,
                ),
              ),
            ],
          ),
        ),

        if (detail.hasJoined) ...[
          const SizedBox(height: AppTheme.space4),
          MyResultCard(detail: detail, winners: winners, standings: standings),
        ],

        if (bidders.isNotEmpty) ...[
          const SizedBox(height: 22),
          Text(t.winners, style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700)),
          const SizedBox(height: AppTheme.space3),
          _Podium(top: bidders.take(3).toList(), prizeByRank: prizeByRank, currency: detail.currency),
        ],

        const SizedBox(height: 22),
        Text(t.finalStandings,
            style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 2),
        Text(t.finalStandingsSubtitle, style: theme.textTheme.bodySmall),
        const SizedBox(height: AppTheme.space3),
        SurfaceCard(
          padding: EdgeInsets.zero,
          child: Column(
            children: [
              if (bidders.isEmpty)
                Padding(
                  padding: const EdgeInsets.all(AppTheme.space6),
                  child: Text(t.noBidsInAuction, textAlign: TextAlign.center),
                ),
              for (var i = 0; i < bidders.length; i++) ...[
                if (i > 0) const Divider(height: 1),
                _StandingRow(
                  entry: bidders[i],
                  prize: prizeByRank[bidders[i].rank],
                  currency: detail.currency,
                ),
              ],
              if (silent > 0) ...[
                const Divider(height: 1),
                Padding(
                  padding: const EdgeInsets.all(AppTheme.space3),
                  child: Text(t.participantsWithoutBids(silent),
                      style: theme.textTheme.bodySmall, textAlign: TextAlign.center),
                ),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

/// The user's own result: their place out of how many, and what happens next.
class MyResultCard extends StatelessWidget {
  const MyResultCard({
    super.key,
    required this.detail,
    required this.winners,
    this.standings,
    this.onShowResults,
  });

  final AuctionDetail detail;
  final List<AuctionWinner> winners;
  final List<LeaderboardEntry>? standings;

  /// When set, the card offers the full results page.
  final VoidCallback? onShowResults;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    final myEntry = standings?.where((e) => e.isMe).firstOrNull;
    final rank = myEntry?.bidCount == 0 ? null : (myEntry?.rank ?? detail.myRank);
    final won = rank == null ? null : winners.where((w) => w.rank == rank).firstOrNull;
    final isWinner = won != null;

    final color = isWinner ? AppTheme.success : AppTheme.accent;

    return SurfaceCard(
      accent: color,
      padding: const EdgeInsets.all(AppTheme.space5),
      child: Column(
        children: [
          Container(
            width: 56,
            height: 56,
            decoration: BoxDecoration(color: color.withValues(alpha: 0.14), shape: BoxShape.circle),
            child: Icon(isWinner ? Icons.emoji_events_rounded : Icons.flag_rounded,
                color: color, size: 30),
          ),
          const SizedBox(height: AppTheme.space3),
          Text(t.yourFinalPlace, style: theme.textTheme.bodyMedium),
          const SizedBox(height: 2),
          Text(
            rank == null ? t.noBidPlaced : t.placeOutOf(rank, detail.participantCount),
            style: theme.textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w800, color: color),
          ),
          const SizedBox(height: AppTheme.space3),
          if (isWinner) ...[
            Text(t.youWonPrize(won.prizeName),
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyLarge?.copyWith(fontWeight: FontWeight.w700)),
            const SizedBox(height: 4),
            Text(t.prizeAmountDue(Fmt.money(won.prizeValue, won.currency, locale: locale)),
                textAlign: TextAlign.center, style: theme.textTheme.bodyMedium),
            if (won.claimStatus == 'PaymentRequired') ...[
              const SizedBox(height: AppTheme.space4),
              FilledButton.icon(
                onPressed: () => context.push('/prizes'),
                icon: const Icon(Icons.payments_rounded),
                label: Text(t.payForPrize),
                style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(48)),
              ),
            ],
          ] else ...[
            Text(
              t.entryFeeReturned(Fmt.money(detail.entryFee, detail.currency, locale: locale)),
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium,
            ),
            const SizedBox(height: AppTheme.space4),
            OutlinedButton.icon(
              onPressed: () => context.go('/store'),
              icon: const Icon(Icons.storefront_rounded),
              label: Text(t.shopNow),
              style: OutlinedButton.styleFrom(minimumSize: const Size.fromHeight(46)),
            ),
          ],
          if (onShowResults != null) ...[
            const SizedBox(height: AppTheme.space2),
            TextButton.icon(
              onPressed: onShowResults,
              icon: const Icon(Icons.leaderboard_rounded),
              label: Text(t.viewFullResults),
            ),
          ],
        ],
      ),
    );
  }
}

/// The first three, second and third either side of the first, standing on steps.
class _Podium extends StatelessWidget {
  const _Podium({required this.top, required this.prizeByRank, required this.currency});

  final List<LeaderboardEntry> top;
  final Map<int, AuctionWinner> prizeByRank;
  final String currency;

  static const _colors = {
    1: Color(0xFFE9B10A),
    2: Color(0xFFAEB5C0),
    3: Color(0xFFCD8A4F),
  };

  @override
  Widget build(BuildContext context) {
    LeaderboardEntry? at(int rank) => top.where((e) => e.rank == rank).firstOrNull;

    // Visual order, right to left in Arabic: second, first, third.
    final order = [2, 1, 3];

    return SurfaceCard(
      padding: const EdgeInsets.fromLTRB(AppTheme.space3, AppTheme.space5, AppTheme.space3, 0),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.end,
        children: [
          for (final rank in order)
            Expanded(
              child: at(rank) == null
                  ? const SizedBox.shrink()
                  : _Step(
                      entry: at(rank)!,
                      prize: prizeByRank[rank],
                      color: _colors[rank]!,
                      height: switch (rank) { 1 => 96.0, 2 => 70.0, _ => 54.0 },
                      currency: currency,
                    ),
            ),
        ],
      ),
    );
  }
}

class _Step extends StatelessWidget {
  const _Step({
    required this.entry,
    required this.prize,
    required this.color,
    required this.height,
    required this.currency,
  });

  final LeaderboardEntry entry;
  final AuctionWinner? prize;
  final Color color;
  final double height;
  final String currency;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final first = entry.rank == 1;

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 4),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (first) Icon(Icons.emoji_events_rounded, color: color, size: 26),
          CircleAvatar(
            radius: first ? 26 : 22,
            backgroundColor: entry.isMe ? AppTheme.accentTint : color.withValues(alpha: 0.18),
            child: Text(
              entry.displayName.trim().isEmpty ? '؟' : entry.displayName.trim().characters.first,
              style: TextStyle(
                fontWeight: FontWeight.w800,
                fontSize: first ? 18 : 15,
                color: entry.isMe ? AppTheme.accentText : theme.colorScheme.onSurface,
              ),
            ),
          ),
          const SizedBox(height: 6),
          Text(
            entry.isMe ? t.you : Fmt.name(entry.displayName),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: theme.textTheme.bodyMedium?.copyWith(
              fontWeight: FontWeight.w700,
              color: entry.isMe ? AppTheme.accentText : null,
            ),
          ),
          Text(
            Fmt.money(entry.highestPrice, currency, locale: locale),
            style: theme.textTheme.bodySmall?.copyWith(fontFeatures: AppTheme.numeric),
          ),
          const SizedBox(height: 6),
          Container(
            height: height,
            width: double.infinity,
            alignment: Alignment.topCenter,
            padding: const EdgeInsets.only(top: 8),
            decoration: BoxDecoration(
              gradient: LinearGradient(
                begin: Alignment.topCenter,
                end: Alignment.bottomCenter,
                colors: [color.withValues(alpha: 0.85), color.withValues(alpha: 0.55)],
              ),
              borderRadius: const BorderRadius.vertical(top: Radius.circular(AppTheme.radiusMd)),
            ),
            child: Text(
              '${entry.rank}',
              style: const TextStyle(
                color: Colors.white,
                fontSize: 22,
                fontWeight: FontWeight.w900,
                fontFeatures: AppTheme.numeric,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _StandingRow extends StatelessWidget {
  const _StandingRow({required this.entry, required this.prize, required this.currency});

  final LeaderboardEntry entry;
  final AuctionWinner? prize;
  final String currency;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final winner = prize != null;

    final row = Padding(
      padding: const EdgeInsets.symmetric(horizontal: AppTheme.space4, vertical: AppTheme.space3),
      child: Row(
        children: [
          SizedBox(
            width: 34,
            child: Text(
              '#${entry.rank}',
              textDirection: TextDirection.ltr,
              style: theme.textTheme.titleSmall?.copyWith(
                fontWeight: FontWeight.w800,
                color: winner ? AppTheme.gold : theme.colorScheme.onSurfaceVariant,
                fontFeatures: AppTheme.numeric,
              ),
            ),
          ),
          const SizedBox(width: AppTheme.space2),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Flexible(
                      child: Text(
                        Fmt.name(entry.displayName),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.bodyLarge?.copyWith(fontWeight: FontWeight.w700),
                      ),
                    ),
                    if (entry.isMe) ...[
                      const SizedBox(width: AppTheme.space2),
                      StatusPill(t.you, color: AppTheme.accent),
                    ],
                  ],
                ),
                const SizedBox(height: 2),
                Text(
                  winner ? '${t.bidsCount(entry.bidCount)} · ${prize!.prizeName}' : t.bidsCount(entry.bidCount),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: theme.textTheme.bodySmall,
                ),
              ],
            ),
          ),
          const SizedBox(width: AppTheme.space2),
          Text(
            Fmt.money(entry.highestPrice, currency, locale: locale),
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w800,
              fontFeatures: AppTheme.numeric,
            ),
          ),
        ],
      ),
    );

    if (!entry.isMe && !winner) return row;
    return ColoredBox(
      color: entry.isMe ? AppTheme.accentSoft : AppTheme.gold.withValues(alpha: 0.05),
      child: row,
    );
  }
}
