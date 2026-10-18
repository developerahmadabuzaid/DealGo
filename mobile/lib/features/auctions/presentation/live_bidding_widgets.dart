import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/realtime/auction_hub_client.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';
import '../data/auction_models.dart';
import 'live_auction_controller.dart';

// The pieces of a live auction that both the auction page and the live bidding screen show, so
// the two read as one product rather than two designs of the same thing.

class ConnectionIndicator extends StatelessWidget {
  const ConnectionIndicator({super.key, required this.status});

  final HubStatus status;

  @override
  Widget build(BuildContext context) {
    final (color, icon) = switch (status) {
      HubStatus.connected => (AppTheme.success, Icons.wifi_rounded),
      HubStatus.connecting => (AppTheme.gold, Icons.wifi_tethering_rounded),
      HubStatus.disconnected => (AppTheme.urgentDeep, Icons.wifi_off_rounded),
    };

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 6),
      child: Icon(icon, color: color, size: 20),
    );
  }
}

/// The live auction. Becomes visually urgent once the final phase starts.
class LivePanel extends StatelessWidget {
  const LivePanel({
    super.key,
    required this.state,
    required this.snapshot,
    this.compact = false,
  });

  final LiveAuctionState state;
  final AuctionSnapshot snapshot;

  /// The live bidding screen's version: the same facts in about half the height, so the leading
  /// bidders below it are on screen rather than behind the bid bar.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final urgent = snapshot.isFinalPhase;

    final accent = urgent ? AppTheme.finalPhase : AppTheme.live;

    final pill = urgent
        ? StatusPill(
            t.finalPhase,
            color: AppTheme.finalPhase,
            icon: Icons.local_fire_department_rounded,
            filled: true,
            pulse: true,
          )
        : StatusPill(
            t.liveAuctions,
            color: AppTheme.live,
            icon: Icons.circle,
            filled: true,
            pulse: true,
          );

    if (compact) {
      return SurfaceCard(
        accent: accent,
        padding: const EdgeInsets.fromLTRB(
          AppTheme.space4,
          AppTheme.space3,
          AppTheme.space4,
          AppTheme.space3,
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // Live and how long is left, on one line.
            Row(
              children: [
                pill,
                const Spacer(),
                AuctionCountdown(
                  remaining: state.remaining,
                  urgent: urgent,
                  label: t.remaining,
                  compact: true,
                ),
              ],
            ),
            const SizedBox(height: AppTheme.space3),
            PriceDisplay(
              label: t.currentPrice,
              amount: Fmt.money(
                snapshot.currentPrice,
                snapshot.currency,
                locale: locale,
              ),
              color: accent,
              animate: true,
              size: 30,
            ),
            const SizedBox(height: AppTheme.space3),
            TargetProgressBar(
              progress: snapshot.progress,
              urgent: urgent,
              height: 6,
            ),
            const SizedBox(height: AppTheme.space1),
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(
                  Fmt.money(
                    snapshot.startingPrice,
                    snapshot.currency,
                    locale: locale,
                  ),
                  style: theme.textTheme.labelSmall,
                ),
                Text(
                  '${t.progressToTarget} ${Fmt.percent(snapshot.progress)}',
                  style: theme.textTheme.labelSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                    color: accent,
                  ),
                ),
                Text(
                  Fmt.money(
                    snapshot.targetPrice,
                    snapshot.currency,
                    locale: locale,
                  ),
                  style: theme.textTheme.labelSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                  ),
                ),
              ],
            ),
            const Padding(
              padding: EdgeInsets.symmetric(vertical: AppTheme.space2),
              child: Divider(height: 1),
            ),
            Row(
              children: [
                Expanded(
                  child: _InlineStat(
                    label: t.yourRank,
                    value: snapshot.myRank == null
                        ? '—'
                        : '#${snapshot.myRank}',
                    color: (snapshot.myRank ?? 99) <= 4
                        ? AppTheme.success
                        : null,
                    ltr: true,
                  ),
                ),
                Expanded(
                  child: _InlineStat(
                    label: t.yourHighest,
                    value: Fmt.money(
                      snapshot.myHighestPrice ?? 0,
                      snapshot.currency,
                      locale: locale,
                    ),
                  ),
                ),
              ],
            ),
          ],
        ),
      );
    }

    return SurfaceCard(
      accent: accent,
      padding: const EdgeInsets.all(AppTheme.space5),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // Question one the user has: is this live, and how close is the end?
          Center(child: pill),

          const SizedBox(height: AppTheme.space5),

          // Question two: what does it cost right now? This is the largest thing on the screen.
          PriceDisplay(
            label: t.currentPrice,
            amount: Fmt.money(
              snapshot.currentPrice,
              snapshot.currency,
              locale: locale,
            ),
            color: accent,
            animate: true,
            size: 40,
          ),

          const SizedBox(height: AppTheme.space5),

          // Question three: how long is left?
          AuctionCountdown(
            remaining: state.remaining,
            urgent: urgent,
            label: t.remaining,
          ),

          const SizedBox(height: AppTheme.space5),

          TargetProgressBar(
            progress: snapshot.progress,
            urgent: urgent,
            height: 10,
          ),
          const SizedBox(height: AppTheme.space2),
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Text(
                Fmt.money(
                  snapshot.startingPrice,
                  snapshot.currency,
                  locale: locale,
                ),
                style: theme.textTheme.bodySmall,
              ),
              Text(
                '${t.progressToTarget} ${Fmt.percent(snapshot.progress)}',
                style: theme.textTheme.bodySmall?.copyWith(
                  fontWeight: FontWeight.w700,
                  color: accent,
                ),
              ),
              Text(
                Fmt.money(
                  snapshot.targetPrice,
                  snapshot.currency,
                  locale: locale,
                ),
                style: theme.textTheme.bodySmall?.copyWith(
                  fontWeight: FontWeight.w700,
                ),
              ),
            ],
          ),

          const Padding(
            padding: EdgeInsets.symmetric(vertical: AppTheme.space5),
            child: Divider(height: 1),
          ),

          Row(
            children: [
              Expanded(
                child: Stat(
                  label: t.yourRank,
                  value: snapshot.myRank == null ? '—' : '#${snapshot.myRank}',
                  valueColor: (snapshot.myRank ?? 99) <= 4
                      ? AppTheme.success
                      : null,
                  align: CrossAxisAlignment.center,
                  ltrValue: true,
                ),
              ),
              const SizedBox(width: AppTheme.space2),
              Expanded(
                child: Stat(
                  label: t.yourHighest,
                  value: Fmt.money(
                    snapshot.myHighestPrice ?? 0,
                    snapshot.currency,
                    locale: locale,
                  ),
                  align: CrossAxisAlignment.center,
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

/// "Label  value" on one line, centred: the compact panel's rank and highest bid.
class _InlineStat extends StatelessWidget {
  const _InlineStat({
    required this.label,
    required this.value,
    this.color,
    this.ltr = false,
  });

  final String label;
  final String value;
  final Color? color;
  final bool ltr;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        Text(label, style: theme.textTheme.bodySmall),
        const SizedBox(width: AppTheme.space2),
        Flexible(
          child: Text(
            value,
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            textDirection: ltr ? TextDirection.ltr : null,
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w800,
              color: color,
              fontFeatures: AppTheme.numeric,
            ),
          ),
        ),
      ],
    );
  }
}

/// The countdown, split into labelled hour / minute / second blocks.
///
/// Each unit sits in its own box so the eye can read one number rather than parse a string, and
/// the separators do not drift as the digits change. Always left-to-right, even in Arabic: a
/// clock is not a sentence, and mirroring it would put the seconds where the hours belong.
class AuctionCountdown extends StatelessWidget {
  const AuctionCountdown({
    super.key,
    required this.remaining,
    required this.urgent,
    required this.label,
    this.compact = false,
  });

  final Duration remaining;
  final bool urgent;
  final String label;

  /// Small blocks with no heading, to sit beside the live badge.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final d = remaining.isNegative ? Duration.zero : remaining;

    final hours = d.inHours;
    final parts = <(String, String)>[
      if (hours > 0) (hours.toString().padLeft(2, '0'), 'H'),
      (d.inMinutes.remainder(60).toString().padLeft(2, '0'), 'M'),
      (d.inSeconds.remainder(60).toString().padLeft(2, '0'), 'S'),
    ];

    // Under a minute the clock itself turns red, whatever phase the auction is in.
    final color = urgent || d.inSeconds <= 60
        ? AppTheme.finalPhase
        : theme.colorScheme.onSurface;

    final clock = Directionality(
      textDirection: TextDirection.ltr,
      child: Row(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          for (var i = 0; i < parts.length; i++) ...[
            if (i > 0)
              Padding(
                padding: EdgeInsets.symmetric(horizontal: compact ? 3 : 5),
                child: Text(
                  ':',
                  style:
                      (compact
                              ? theme.textTheme.titleMedium
                              : theme.textTheme.headlineMedium)
                          ?.copyWith(
                            color: color.withValues(alpha: 0.4),
                            height: 1,
                          ),
                ),
              ),
            _TimeBlock(
              value: parts[i].$1,
              unit: parts[i].$2,
              color: color,
              compact: compact,
            ),
          ],
        ],
      ),
    );

    if (compact) return clock;

    return Column(
      children: [
        Text(
          label.toUpperCase(),
          style: theme.textTheme.labelSmall?.copyWith(
            fontSize: 11,
            letterSpacing: 0.8,
          ),
        ),
        const SizedBox(height: AppTheme.space2),
        clock,
      ],
    );
  }
}

class _TimeBlock extends StatelessWidget {
  const _TimeBlock({
    required this.value,
    required this.unit,
    required this.color,
    this.compact = false,
  });

  final String value;
  final String unit;
  final Color color;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Container(
      padding: compact
          ? const EdgeInsets.symmetric(horizontal: 7, vertical: 4)
          : const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.09),
        borderRadius: BorderRadius.circular(AppTheme.radiusMd),
        border: Border.all(color: color.withValues(alpha: 0.22)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            value,
            style: theme.textTheme.headlineMedium?.copyWith(
              fontSize: compact ? 18 : 27,
              height: 1,
              color: color,
              fontFeatures: AppTheme.numeric,
            ),
          ),
          const SizedBox(height: 2),
          Text(
            unit,
            style: theme.textTheme.labelSmall?.copyWith(
              fontSize: compact ? 8 : 9.5,
              letterSpacing: 1,
              color: color.withValues(alpha: 0.75),
            ),
          ),
        ],
      ),
    );
  }
}

/// The bid button. Disabled whenever the connection cannot be trusted — bids are never queued.
class BidBar extends ConsumerWidget {
  const BidBar({
    super.key,
    required this.state,
    required this.snapshot,
    required this.auctionId,
    this.compact = false,
  });

  final LiveAuctionState state;
  final AuctionSnapshot snapshot;
  final String auctionId;

  /// The live bidding screen's version. Its panel already shows the current price, so only the
  /// next bid is repeated here, on one line, and the button is shorter.
  final bool compact;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final urgent = snapshot.isFinalPhase;

    final accent = urgent ? AppTheme.finalPhase : AppTheme.live;

    // What the price becomes if this bid lands. Shown so the button is never a leap of faith.
    final nextPrice = snapshot.currentPrice + snapshot.bidIncrement;
    final cappedNext = nextPrice > snapshot.targetPrice
        ? snapshot.targetPrice
        : nextPrice;

    return Container(
      decoration: BoxDecoration(
        color: theme.colorScheme.surface,
        border: Border(top: BorderSide(color: theme.colorScheme.outline)),
        boxShadow: [
          BoxShadow(
            color: Colors.black.withValues(
              alpha: theme.brightness == Brightness.dark ? 0.4 : 0.07,
            ),
            blurRadius: 20,
            offset: const Offset(0, -6),
          ),
        ],
      ),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: EdgeInsets.fromLTRB(
            AppTheme.space4,
            compact ? AppTheme.space2 : AppTheme.space3,
            AppTheme.space4,
            compact ? AppTheme.space2 : AppTheme.space3,
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (!state.isConnected)
                Container(
                  width: double.infinity,
                  margin: const EdgeInsets.only(bottom: AppTheme.space2),
                  padding: const EdgeInsets.symmetric(
                    horizontal: AppTheme.space3,
                    vertical: AppTheme.space2,
                  ),
                  decoration: BoxDecoration(
                    color: theme.colorScheme.errorContainer,
                    borderRadius: BorderRadius.circular(AppTheme.radiusSm),
                  ),
                  child: Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Icon(
                        Icons.wifi_off_rounded,
                        size: 14,
                        color: theme.colorScheme.onErrorContainer,
                      ),
                      const SizedBox(width: AppTheme.space2),
                      Flexible(
                        child: Text(
                          state.hubStatus == HubStatus.connecting
                              ? t.connectionLost
                              : t.bidsNeedConnection,
                          style: TextStyle(
                            fontSize: 12,
                            fontWeight: FontWeight.w600,
                            color: theme.colorScheme.onErrorContainer,
                          ),
                          textAlign: TextAlign.center,
                        ),
                      ),
                    ],
                  ),
                ),

              // Current price and what it becomes, side by side, directly above the button.
              if (compact)
                Padding(
                  padding: const EdgeInsets.only(bottom: AppTheme.space2),
                  child: Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Text(t.yourNextBid, style: theme.textTheme.bodySmall),
                      const SizedBox(width: AppTheme.space2),
                      Text(
                        Fmt.money(
                          cappedNext,
                          snapshot.currency,
                          locale: locale,
                        ),
                        style: theme.textTheme.titleSmall?.copyWith(
                          fontWeight: FontWeight.w800,
                          color: accent,
                          fontFeatures: AppTheme.numeric,
                        ),
                      ),
                    ],
                  ),
                )
              else
                Padding(
                  padding: const EdgeInsets.only(bottom: AppTheme.space3),
                  child: Row(
                    children: [
                      Expanded(
                        child: Stat(
                          label: t.currentPrice,
                          value: Fmt.money(
                            snapshot.currentPrice,
                            snapshot.currency,
                            locale: locale,
                          ),
                        ),
                      ),
                      const SizedBox(width: AppTheme.space2),
                      Icon(
                        Icons.arrow_forward_rounded,
                        size: 17,
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                      const SizedBox(width: AppTheme.space2),
                      Expanded(
                        child: Stat(
                          label: t.yourNextBid,
                          value: Fmt.money(
                            cappedNext,
                            snapshot.currency,
                            locale: locale,
                          ),
                          valueColor: accent,
                          align: CrossAxisAlignment.end,
                        ),
                      ),
                    ],
                  ),
                ),

              FilledButton(
                onPressed: state.canBid
                    ? () => ref
                          .read(liveAuctionProvider(auctionId).notifier)
                          .placeBid()
                    : null,
                style: FilledButton.styleFrom(
                  backgroundColor: accent,
                  foregroundColor: Colors.white,
                  minimumSize: Size.fromHeight(compact ? 50 : 58),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(AppTheme.radiusMd),
                  ),
                ),
                child: state.isBidding
                    ? const SizedBox(
                        height: 22,
                        width: 22,
                        child: CircularProgressIndicator(
                          strokeWidth: 2.4,
                          color: Colors.white,
                        ),
                      )
                    : Row(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          const Icon(Icons.gavel_rounded, size: 19),
                          const SizedBox(width: AppTheme.space2),
                          Text(
                            t.bidButton(
                              Fmt.money(
                                snapshot.bidIncrement,
                                snapshot.currency,
                                locale: locale,
                              ),
                            ),
                            style: const TextStyle(
                              fontSize: 16.5,
                              fontWeight: FontWeight.w800,
                            ),
                          ),
                        ],
                      ),
              ),
              const SizedBox(height: AppTheme.space2),

              if (state.waitingForOtherBidder) ...[
                Text(
                  t.waitForOtherBidder,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: accent,
                    fontWeight: FontWeight.w700,
                  ),
                  textAlign: TextAlign.center,
                ),
                const SizedBox(height: AppTheme.space1),
              ],

              // The increment moves the price; it is not a charge. Saying so prevents the most
              // common misunderstanding of this auction format.
              Text(
                t.bidIncrementNote,
                style: theme.textTheme.labelSmall?.copyWith(fontSize: 11),
                textAlign: TextAlign.center,
              ),
            ],
          ),
        ),
      ),
    );
  }
}
