import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart' show DateFormat;

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/art_fallback.dart';
import '../../../shared/widgets.dart';
import '../data/auction_models.dart';

/// The red band across the corner of anything that has closed.
class CornerRibbon extends StatelessWidget {
  const CornerRibbon({
    super.key,
    required this.label,
    this.size = 64,
    this.color = _red,
  });

  static const _red = Color(0xFFE5303E);

  final String label;
  final double size;
  final Color color;

  @override
  Widget build(BuildContext context) {
    // It sits on the start corner, so it crosses the right-hand corner in Arabic.
    final rtl = Directionality.of(context) == TextDirection.rtl;
    const band = 17.0;
    final length = size * 1.5;
    final cx = rtl ? size * 0.64 : size * 0.36;
    final cy = size * 0.36;

    return IgnorePointer(
      child: SizedBox.square(
        dimension: size,
        child: ClipRect(
          child: Stack(
            children: [
              Positioned(
                left: cx - length / 2,
                top: cy - band / 2,
                width: length,
                height: band,
                child: Transform.rotate(
                  angle: rtl ? math.pi / 4 : -math.pi / 4,
                  child: DecoratedBox(
                    decoration: BoxDecoration(
                      color: color,
                      boxShadow: const [
                        BoxShadow(
                          color: Color(0x33000000),
                          blurRadius: 3,
                          offset: Offset(0, 1),
                        ),
                      ],
                    ),
                    child: Center(
                      child: Text(
                        label,
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: 9.5,
                          height: 1,
                          fontWeight: FontWeight.w800,
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// One finished auction in a list: the picture beside it, what it closed at and who took it.
class EndedAuctionCard extends StatelessWidget {
  const EndedAuctionCard({super.key, required this.auction});

  final AuctionSummary auction;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final winner = auction.winnerName;

    return Stack(
      children: [
        SurfaceCard(
          padding: const EdgeInsets.all(AppTheme.space3),
          onTap: () => context.push('/auctions/${auction.id}/ended'),
          child: IntrinsicHeight(
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                // The picture, on a dark ground so a product shot on black or white both sit well.
                ClipRRect(
                  borderRadius: BorderRadius.circular(AppTheme.radiusMd),
                  child: Container(
                    width: 96,
                    constraints: const BoxConstraints(minHeight: 96),
                    color: const Color(0xFF0B0C0E),
                    child: ArtOrPicture(
                      imageUrl: auction.imageUrl,
                      seed: auction.auctionNumber,
                    ),
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
                        style: theme.textTheme.titleSmall?.copyWith(
                          fontWeight: FontWeight.w800,
                        ),
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
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(
                                  t.finalPrice,
                                  style: theme.textTheme.labelSmall,
                                ),
                                const SizedBox(height: 2),
                                FittedBox(
                                  fit: BoxFit.scaleDown,
                                  alignment: AlignmentDirectional.centerStart,
                                  child: Text(
                                    Fmt.money(
                                      auction.currentPrice,
                                      auction.currency,
                                      locale: locale,
                                    ),
                                    style: theme.textTheme.titleSmall?.copyWith(
                                      color: AppTheme.brand,
                                      fontWeight: FontWeight.w800,
                                      fontFeatures: AppTheme.numeric,
                                    ),
                                  ),
                                ),
                              ],
                            ),
                          ),
                          const SizedBox(width: AppTheme.space2),
                          Flexible(
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.end,
                              children: [
                                Row(
                                  mainAxisSize: MainAxisSize.min,
                                  children: [
                                    const Icon(
                                      Icons.workspace_premium_rounded,
                                      size: 15,
                                      color: Color(0xFFE9B10A),
                                    ),
                                    const SizedBox(width: 3),
                                    Text(
                                      t.winner,
                                      style: theme.textTheme.labelSmall,
                                    ),
                                  ],
                                ),
                                const SizedBox(height: 2),
                                Text(
                                  winner == null
                                      ? t.noWinner
                                      : Fmt.name(winner),
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: theme.textTheme.bodySmall?.copyWith(
                                    fontWeight: FontWeight.w800,
                                    color: theme.colorScheme.onSurface,
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ],
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
            child: CornerRibbon(label: t.ended, size: 58),
          ),
        ),
      ],
    );
  }
}

/// How a bid's moment reads under a name: the time, then the day.
String _when(DateTime? at, String locale) {
  if (at == null) return '';
  final local = at.toLocal();
  return '${DateFormat('hh:mm a', locale).format(local)}  ·  ${DateFormat('dd/MM/yyyy', 'en_US').format(local)}';
}

String _initial(String name) {
  final trimmed = name.trim();
  return trimmed.isEmpty ? '؟' : trimmed.characters.first.toUpperCase();
}

/// A finished auction: the product large on a dark stage, the winner in gold, and every bid that
/// led there, newest first.
class EndedAuctionScreen extends ConsumerWidget {
  const EndedAuctionScreen({super.key, required this.auctionId});

  final String auctionId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final detailAsync = ref.watch(auctionDetailProvider(auctionId));
    final bidsAsync = ref.watch(auctionBidsProvider(auctionId));

    Future<void> refresh() async {
      ref.invalidate(auctionDetailProvider(auctionId));
      ref.invalidate(auctionBidsProvider(auctionId));
    }

    // The bid log is the extra, not the page. When it cannot be had — a server that predates it
    // answered 405, and the whole page used to turn into "server error" — the auction is still
    // shown, just without its log.
    final bidsSettled =
        bidsAsync.hasValue || (bidsAsync.hasError && !bidsAsync.isLoading);
    final error = detailAsync.error;

    Widget body;
    if (detailAsync.hasValue && bidsSettled) {
      body = RefreshIndicator(
        onRefresh: refresh,
        child: _EndedBody(
          detail: detailAsync.requireValue,
          bids: bidsAsync.valueOrNull ?? const <BidHistoryEntry>[],
        ),
      );
    } else if (error != null && !detailAsync.isLoading) {
      body = SafeArea(
        child: ErrorView(
          message: error is ApiException
              ? error.displayMessage
              : error.toString(),
          onRetry: refresh,
        ),
      );
    } else {
      body = const Center(child: CircularProgressIndicator());
    }

    return Scaffold(
      body: Stack(
        children: [
          body,
          // Back, over the dark stage.
          PositionedDirectional(
            top: MediaQuery.paddingOf(context).top + AppTheme.space2,
            start: AppTheme.space3,
            child: Material(
              color: Colors.black.withValues(alpha: 0.45),
              shape: const CircleBorder(),
              child: IconButton(
                tooltip: MaterialLocalizations.of(context).backButtonTooltip,
                onPressed: () =>
                    context.canPop() ? context.pop() : context.go('/auctions'),
                icon: const BackButtonIcon(),
                color: Colors.white,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _EndedBody extends StatelessWidget {
  const _EndedBody({required this.detail, required this.bids});

  final AuctionDetail detail;
  final List<BidHistoryEntry> bids;

  static const _stage = 300.0;
  static const _overlap = 70.0;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final top = MediaQuery.paddingOf(context).top;
    final winner = bids.firstOrNull;
    final rest = bids.skip(1).toList();

    return CustomScrollView(
      physics: const AlwaysScrollableScrollPhysics(),
      slivers: [
        SliverToBoxAdapter(
          child: Stack(
            children: [
              // The stage: the product, alone, on black.
              Container(
                height: _stage + top,
                padding: EdgeInsets.fromLTRB(
                  AppTheme.space8,
                  top + AppTheme.space4,
                  AppTheme.space8,
                  _overlap + AppTheme.space2,
                ),
                decoration: const BoxDecoration(
                  color: Color(0xFF0B0C0E),
                  borderRadius: BorderRadius.vertical(
                    bottom: Radius.circular(AppTheme.radiusXl),
                  ),
                ),
                child: Center(
                  child: _StageImage(
                    url: detail.imageUrl,
                    seed: detail.auctionNumber,
                  ),
                ),
              ),
              Padding(
                padding: EdgeInsets.fromLTRB(
                  AppTheme.space4,
                  _stage + top - _overlap,
                  AppTheme.space4,
                  0,
                ),
                child: _SummaryCard(detail: detail, winner: winner),
              ),
            ],
          ),
        ),

        if (winner != null) ...[
          SliverPadding(
            padding: const EdgeInsets.fromLTRB(
              AppTheme.space4,
              AppTheme.space5,
              AppTheme.space4,
              0,
            ),
            sliver: SliverToBoxAdapter(
              child: _WinnerBanner(bid: winner, currency: detail.currency),
            ),
          ),
          SliverPadding(
            padding: const EdgeInsets.fromLTRB(
              AppTheme.space4,
              AppTheme.space5,
              AppTheme.space4,
              AppTheme.space2,
            ),
            sliver: SliverToBoxAdapter(
              child: Row(
                children: [
                  const Icon(
                    Icons.history_rounded,
                    size: 18,
                    color: AppTheme.brand,
                  ),
                  const SizedBox(width: 6),
                  Expanded(
                    child: Text(
                      t.bidHistory,
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w800,
                      ),
                    ),
                  ),
                  Text(
                    t.bidsLogged(bids.length),
                    style: theme.textTheme.bodySmall,
                  ),
                ],
              ),
            ),
          ),
          SliverPadding(
            padding: const EdgeInsets.symmetric(horizontal: AppTheme.space4),
            sliver: SliverList.separated(
              itemCount: rest.length,
              separatorBuilder: (_, _) =>
                  const SizedBox(height: AppTheme.space2),
              itemBuilder: (_, i) =>
                  _BidRow(bid: rest[i], currency: detail.currency),
            ),
          ),
        ] else
          SliverPadding(
            padding: const EdgeInsets.all(AppTheme.space6),
            sliver: SliverToBoxAdapter(
              child: Text(
                t.noBidsInAuction,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium,
              ),
            ),
          ),

        SliverPadding(
          padding: const EdgeInsets.fromLTRB(
            AppTheme.space4,
            AppTheme.space4,
            AppTheme.space4,
            40,
          ),
          sliver: SliverToBoxAdapter(
            child: OutlinedButton.icon(
              onPressed: () => context.push('/auctions/${detail.id}/results'),
              icon: const Icon(Icons.leaderboard_rounded),
              label: Text(t.viewFullResults),
              style: OutlinedButton.styleFrom(
                minimumSize: const Size.fromHeight(46),
              ),
            ),
          ),
        ),
      ],
    );
  }
}

class _StageImage extends StatelessWidget {
  const _StageImage({required this.url, required this.seed});

  final String? url;
  final Object seed;

  @override
  Widget build(BuildContext context) {
    final u = url;
    if (u != null && u.isNotEmpty) {
      return RemoteImage(u, radius: AppTheme.radiusMd, fit: BoxFit.contain);
    }
    return ClipRRect(
      borderRadius: BorderRadius.circular(AppTheme.radiusMd),
      child: ArtOrPicture(imageUrl: null, seed: seed),
    );
  }
}

/// The card that rides up over the stage: the title, who won it and what it closed at.
class _SummaryCard extends StatelessWidget {
  const _SummaryCard({required this.detail, required this.winner});

  final AuctionDetail detail;
  final BidHistoryEntry? winner;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    return Stack(
      children: [
        Container(
          width: double.infinity,
          padding: const EdgeInsets.fromLTRB(
            AppTheme.space5,
            AppTheme.space5,
            AppTheme.space5,
            AppTheme.space4,
          ),
          decoration: BoxDecoration(
            color: theme.colorScheme.surface,
            borderRadius: BorderRadius.circular(AppTheme.radiusLg),
            border: Border.all(color: theme.colorScheme.outline),
            boxShadow: const [
              BoxShadow(
                color: Color(0x26000000),
                blurRadius: 18,
                offset: Offset(0, 6),
              ),
            ],
          ),
          child: Column(
            children: [
              Text(
                detail.title,
                textAlign: TextAlign.center,
                style: theme.textTheme.titleMedium?.copyWith(
                  fontWeight: FontWeight.w800,
                ),
              ),
              if (detail.mainProductName.isNotEmpty &&
                  detail.mainProductName != detail.title) ...[
                const SizedBox(height: 2),
                Text(
                  detail.mainProductName,
                  textAlign: TextAlign.center,
                  style: theme.textTheme.bodySmall,
                ),
              ],
              const SizedBox(height: AppTheme.space4),
              Wrap(
                alignment: WrapAlignment.center,
                spacing: AppTheme.space2,
                runSpacing: AppTheme.space2,
                children: [
                  _Chip(
                    color: AppTheme.brand,
                    icon: Icons.workspace_premium_rounded,
                    iconColor: const Color(0xFFE9B10A),
                    label: winner == null
                        ? t.noWinner
                        : Fmt.name(winner!.displayName),
                  ),
                  _Chip(
                    color: AppTheme.success,
                    icon: Icons.sell_rounded,
                    label: Fmt.money(
                      detail.currentPrice,
                      detail.currency,
                      locale: locale,
                    ),
                    numeric: true,
                  ),
                ],
              ),
              if (detail.finishedAt != null) ...[
                const SizedBox(height: AppTheme.space3),
                Text(
                  '${t.auctionFinished} · ${_when(detail.finishedAt, locale)}',
                  textAlign: TextAlign.center,
                  style: theme.textTheme.labelSmall,
                ),
              ],
            ],
          ),
        ),
        PositionedDirectional(
          top: 0,
          start: 0,
          child: ClipRRect(
            borderRadius: const BorderRadiusDirectional.only(
              topStart: Radius.circular(AppTheme.radiusLg),
            ).resolve(Directionality.of(context)),
            child: CornerRibbon(label: t.ended, size: 62),
          ),
        ),
      ],
    );
  }
}

class _Chip extends StatelessWidget {
  const _Chip({
    required this.color,
    required this.icon,
    required this.label,
    this.iconColor,
    this.numeric = false,
  });

  final Color color;
  final IconData icon;
  final Color? iconColor;
  final String label;
  final bool numeric;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(
        horizontal: AppTheme.space3,
        vertical: 7,
      ),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(AppTheme.radiusSm),
        border: Border.all(color: color.withValues(alpha: 0.7)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 16, color: iconColor ?? color),
          const SizedBox(width: 6),
          Flexible(
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                color: color,
                fontWeight: FontWeight.w800,
                fontSize: 13.5,
                fontFeatures: numeric ? AppTheme.numeric : null,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The closing bid: the winner, in gold.
class _WinnerBanner extends StatelessWidget {
  const _WinnerBanner({required this.bid, required this.currency});

  final BidHistoryEntry bid;
  final String currency;

  static const _gold = Color(0xFFE9B10A);

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final rtl = Directionality.of(context) == TextDirection.rtl;

    return Container(
      padding: const EdgeInsets.fromLTRB(
        AppTheme.space3,
        AppTheme.space3,
        AppTheme.space4,
        AppTheme.space3,
      ),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(AppTheme.radiusLg),
        gradient: LinearGradient(
          begin: rtl ? Alignment.centerRight : Alignment.centerLeft,
          end: rtl ? Alignment.centerLeft : Alignment.centerRight,
          colors: const [
            Color(0xFFC98A12),
            Color(0xFF8A6A2C),
            Color(0xFF3B4250),
          ],
          stops: const [0, 0.45, 1],
        ),
        boxShadow: const [
          BoxShadow(
            color: Color(0x40C98A12),
            blurRadius: 16,
            offset: Offset(0, 6),
          ),
        ],
      ),
      child: Row(
        children: [
          // The winner's mark: their initial in a gold ring, a medal pinned to it.
          SizedBox(
            width: 58,
            height: 58,
            child: Stack(
              clipBehavior: Clip.none,
              children: [
                Container(
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    gradient: const LinearGradient(
                      colors: [Color(0xFFFFE08A), _gold, Color(0xFFB07A00)],
                      begin: Alignment.topLeft,
                      end: Alignment.bottomRight,
                    ),
                    boxShadow: const [
                      BoxShadow(color: Color(0x55000000), blurRadius: 6),
                    ],
                  ),
                  padding: const EdgeInsets.all(3),
                  child: CircleAvatar(
                    backgroundColor: const Color(0xFF1F5FAF),
                    child: Text(
                      _initial(bid.displayName),
                      style: const TextStyle(
                        color: Colors.white,
                        fontWeight: FontWeight.w900,
                        fontSize: 20,
                      ),
                    ),
                  ),
                ),
                const PositionedDirectional(
                  bottom: -4,
                  end: -4,
                  child: CircleAvatar(
                    radius: 12,
                    backgroundColor: Colors.white,
                    child: Icon(
                      Icons.emoji_events_rounded,
                      size: 16,
                      color: _gold,
                    ),
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: AppTheme.space3),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  t.auctionWinner,
                  style: const TextStyle(
                    color: Color(0xFFFFE7A8),
                    fontSize: 11,
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  bid.isMe ? t.you : Fmt.name(bid.displayName),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    color: Colors.white,
                    fontSize: 16,
                    fontWeight: FontWeight.w900,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  _when(bid.placedAt, locale),
                  style: const TextStyle(
                    color: Color(0xE6FFFFFF),
                    fontSize: 11.5,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(width: AppTheme.space2),
          Text(
            Fmt.money(bid.price, currency, locale: locale),
            style: const TextStyle(
              color: Colors.white,
              fontSize: 19,
              fontWeight: FontWeight.w900,
              fontFeatures: AppTheme.numeric,
            ),
          ),
        ],
      ),
    );
  }
}

/// Any bid before the closing one.
class _BidRow extends StatelessWidget {
  const _BidRow({required this.bid, required this.currency});

  final BidHistoryEntry bid;
  final String currency;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final mine = bid.isMe;

    return Container(
      padding: const EdgeInsets.symmetric(
        horizontal: AppTheme.space3,
        vertical: 10,
      ),
      decoration: BoxDecoration(
        color: mine
            ? AppTheme.accentSoft
            : Color.alphaBlend(
                theme.colorScheme.onSurface.withValues(alpha: 0.04),
                theme.colorScheme.surface,
              ),
        borderRadius: BorderRadius.circular(AppTheme.radiusMd),
        border: Border.all(
          color: mine
              ? AppTheme.accent.withValues(alpha: 0.4)
              : theme.colorScheme.outline,
        ),
      ),
      child: Row(
        children: [
          CircleAvatar(
            radius: 19,
            backgroundColor: mine
                ? AppTheme.accentTint
                : theme.colorScheme.onSurfaceVariant.withValues(alpha: 0.55),
            child: Text(
              _initial(bid.displayName),
              style: TextStyle(
                color: mine ? AppTheme.accentText : Colors.white,
                fontWeight: FontWeight.w800,
                fontSize: 15,
              ),
            ),
          ),
          const SizedBox(width: AppTheme.space3),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  mine ? t.you : Fmt.name(bid.displayName),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: theme.textTheme.bodyMedium?.copyWith(
                    fontWeight: FontWeight.w800,
                    color: mine ? AppTheme.accentText : null,
                  ),
                ),
                const SizedBox(height: 1),
                Text(
                  _when(bid.placedAt, locale),
                  style: theme.textTheme.bodySmall,
                ),
              ],
            ),
          ),
          const SizedBox(width: AppTheme.space2),
          Text(
            Fmt.money(bid.price, currency, locale: locale),
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w900,
              fontFeatures: AppTheme.numeric,
            ),
          ),
        ],
      ),
    );
  }
}
