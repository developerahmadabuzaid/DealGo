import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/realtime/auction_hub_client.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/auto_refresh.dart';
import '../../../shared/app_header.dart';
import '../../../shared/widgets.dart';
import '../data/auction_models.dart';
import 'bidding_arena_screen.dart';
import 'live_auction_controller.dart';

/// One auction, in whichever of its phases it happens to be.
///
/// The screen renders only what the server said. It never computes a price, a rank, a winner or an
/// end time; the countdown is derived from the server clock offset carried in the snapshot.
class AuctionDetailScreen extends ConsumerStatefulWidget {
  const AuctionDetailScreen({super.key, required this.auctionId});

  final String auctionId;

  @override
  ConsumerState<AuctionDetailScreen> createState() => _AuctionDetailScreenState();
}

class _AuctionDetailScreenState extends ConsumerState<AuctionDetailScreen> {
  Timer? _tick;

  /// The arena opens by itself once per visit, the first time a joined auction is seen live.
  /// Closing it leaves the user on this page; the button here opens it again.
  bool _arenaShown = false;
  bool _arenaOpen = false;

  Future<void> _openArena(String title) async {
    if (_arenaOpen || !mounted) return;
    _arenaOpen = true;
    await BiddingArenaScreen.open(context, auctionId: widget.auctionId, title: title);
    _arenaOpen = false;
  }

  @override
  void initState() {
    super.initState();
    // Repaints the countdown once a second; the value itself comes from the server's end time.
    _tick = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() {});
    });
  }

  @override
  void dispose() {
    _tick?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final live = ref.watch(liveAuctionProvider(widget.auctionId));
    final detailAsync = ref.watch(auctionDetailProvider(widget.auctionId));

    ref.listen(liveAuctionProvider(widget.auctionId), (prev, next) {
      if (next.rankMessage != null && next.rankMessage != prev?.rankMessage) {
        // The arena says this in its own way; a snackbar would cover its bid button.
        if (_arenaOpen) {
          ref.read(liveAuctionProvider(widget.auctionId).notifier).clearRankMessage();
          return;
        }
        final message = next.rankMessage == 'inTopFour' ? t.inTopFour : t.lostFourthPlace;
        ScaffoldMessenger.of(context)
          ..hideCurrentSnackBar()
          ..showSnackBar(SnackBar(
            content: Text(message),
            backgroundColor:
                next.rankMessage == 'inTopFour' ? AppTheme.success : AppTheme.urgentDeep,
            duration: const Duration(seconds: 3),
          ));
        ref.read(liveAuctionProvider(widget.auctionId).notifier).clearRankMessage();
      }
    });

    final page = detailAsync.when(
      // A refresh that fails keeps the page on screen rather than swapping in an error.
      skipError: true,
      loading: () => Scaffold(
        appBar: const BrandAppBar(),
        body: const Center(child: CircularProgressIndicator()),
      ),
      error: (e, _) => Scaffold(
        appBar: const BrandAppBar(),
        body: ErrorView(
          message: e is ApiException ? e.displayMessage : e.toString(),
          onRetry: () => ref.invalidate(auctionDetailProvider(widget.auctionId)),
        ),
      ),
      data: (detail) {
        final snapshot = live.snapshot;
        final isLive = snapshot?.isLive ?? detail.isLive;
        final canEnterArena = isLive && snapshot != null && detail.hasJoined;

        if (canEnterArena && !_arenaShown) {
          _arenaShown = true;
          WidgetsBinding.instance.addPostFrameCallback((_) => _openArena(detail.title));
        }

        return Scaffold(
          backgroundColor: (snapshot?.isFinalPhase ?? false)
              ? AppTheme.urgentDeep.withValues(alpha: 0.05)
              : null,
          appBar: BrandAppBar(
            // The number now sits on the image, so the bar carries the name — which is what
            // tells you which auction you are looking at once the image scrolls away.
            title: Text(detail.title, maxLines: 1, overflow: TextOverflow.ellipsis),
            actions: [
              if (isLive) _ConnectionIndicator(status: live.hubStatus),
              const SizedBox(width: AppTheme.space2),
            ],
          ),
          body: RefreshIndicator(
            onRefresh: () async {
              ref.invalidate(auctionDetailProvider(widget.auctionId));
              await ref.read(liveAuctionProvider(widget.auctionId).notifier).resync();
            },
            child: ListView(
              padding: const EdgeInsets.fromLTRB(
                  AppTheme.space4, AppTheme.space4, AppTheme.space4, 140),
              children: [
                if (live.error != null)
                  Padding(
                    padding: const EdgeInsets.only(bottom: AppTheme.space3),
                    child: _Banner(
                      text: live.error!,
                      color: Theme.of(context).colorScheme.errorContainer,
                    ),
                  ),

                // The product, with its number set on the image rather than stacked above the
                // title, so the eye reaches the name without stepping over an id first.
                Stack(
                  children: [
                    // The auction's own picture; the first prize's picture when it has none.
                    RemoteImage(detail.imageUrl,
                        height: 210, width: double.infinity, radius: AppTheme.radiusLg),
                    Positioned.fill(
                      child: DecoratedBox(
                        decoration: BoxDecoration(
                          borderRadius: BorderRadius.circular(AppTheme.radiusLg),
                          gradient: const LinearGradient(
                            begin: Alignment.bottomCenter,
                            end: Alignment.topCenter,
                            colors: [Color(0x73000000), Color(0x00000000)],
                            stops: [0, 0.5],
                          ),
                        ),
                      ),
                    ),
                    PositionedDirectional(
                      bottom: AppTheme.space3,
                      start: AppTheme.space3,
                      child: Container(
                        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
                        decoration: BoxDecoration(
                          color: Colors.black.withValues(alpha: 0.55),
                          borderRadius: BorderRadius.circular(AppTheme.radiusPill),
                        ),
                        child: LtrText(
                          detail.auctionNumber,
                          style: const TextStyle(
                            color: Colors.white,
                            fontSize: 11.5,
                            fontWeight: FontWeight.w700,
                            letterSpacing: 0.4,
                          ),
                        ),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: AppTheme.space4),

                Text(detail.title, style: Theme.of(context).textTheme.headlineSmall),
                const SizedBox(height: 3),
                Text(detail.mainProductName,
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: Theme.of(context).colorScheme.onSurfaceVariant,
                        )),

                const SizedBox(height: AppTheme.space5),

                if (canEnterArena) ...[
                  FilledButton.icon(
                    onPressed: () => _openArena(detail.title),
                    icon: const Icon(Icons.bolt_rounded),
                    label: Text(t.enterArena),
                    style: FilledButton.styleFrom(
                      backgroundColor:
                          snapshot.isFinalPhase ? AppTheme.finalPhase : AppTheme.accent,
                      minimumSize: const Size.fromHeight(54),
                    ),
                  ),
                  const SizedBox(height: AppTheme.space4),
                ],

                if (detail.acceptsRegistration)
                  _RegistrationPanel(detail: detail)
                else if (detail.isWaitingToStart)
                  _WaitingPanel(detail: detail)
                else if (isLive && snapshot != null)
                  _LivePanel(state: live, snapshot: snapshot, detail: detail)
                else if (detail.isComplete)
                  _ResultsPanel(detail: detail),

                const SizedBox(height: 22),
                _PrizeList(detail: detail, livePrice: snapshot?.currentPrice),

                if (snapshot != null && snapshot.leaderboard.isNotEmpty) ...[
                  const SizedBox(height: 22),
                  _Leaderboard(entries: snapshot.leaderboard, currency: snapshot.currency),
                ],
              ],
            ),
          ),
          bottomNavigationBar: isLive && snapshot != null && detail.hasJoined
              ? _BidBar(state: live, snapshot: snapshot, auctionId: widget.auctionId)
              : null,
        );
      },
    );

    // The page refreshes itself, so a change of state — enough people joined, the start time set,
    // the auction started or ended — shows up without the user pulling down. Bids still arrive
    // instantly over the live connection.
    return AutoRefresh(
      onRefresh: () => ref.invalidate(auctionDetailProvider(widget.auctionId)),
      child: page,
    );
  }
}

class _ConnectionIndicator extends StatelessWidget {
  const _ConnectionIndicator({required this.status});

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

class _Banner extends StatelessWidget {
  const _Banner({required this.text, required this.color});

  final String text;
  final Color color;

  @override
  Widget build(BuildContext context) => Container(
        width: double.infinity,
        padding: const EdgeInsets.all(12),
        decoration: BoxDecoration(color: color, borderRadius: BorderRadius.circular(12)),
        child: Text(text, style: const TextStyle(fontSize: 13)),
      );
}

/// Before the participant threshold: the number that matters is how many people are still needed.
class _RegistrationPanel extends ConsumerStatefulWidget {
  const _RegistrationPanel({required this.detail});

  final AuctionDetail detail;

  @override
  ConsumerState<_RegistrationPanel> createState() => _RegistrationPanelState();
}

class _RegistrationPanelState extends ConsumerState<_RegistrationPanel> {
  bool _busy = false;

  Future<void> _join() async {
    final t = AppStrings.of(context);
    setState(() => _busy = true);

    try {
      final key = 'join-${widget.detail.id}-${DateTime.now().microsecondsSinceEpoch}';
      await ref.read(auctionRepositoryProvider).join(widget.detail.id, key);

      ref.invalidate(auctionDetailProvider(widget.detail.id));
      ref.invalidate(walletsProvider);

      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(t.joined)));
      }
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
    final d = widget.detail;
    final locale = Localizations.localeOf(context).languageCode;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // How full the auction is, as a bar only: the app does not show participant numbers.
            Text(t.participantsFilling,
                style: Theme.of(context).textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700)),
            const SizedBox(height: 10),
            TargetProgressBar(
                height: 10,
                progress: d.requiredParticipants == 0
                    ? 0
                    : d.participantCount / d.requiredParticipants),
            const SizedBox(height: 18),

            Row(
              children: [
                Expanded(
                    child: Stat(
                        label: t.originalPrice,
                        value: Fmt.money(d.originalPrice, d.currency, locale: locale))),
                const SizedBox(width: AppTheme.space2),
                Expanded(
                    child: Stat(
                        label: t.targetPrice,
                        value: Fmt.money(d.targetPrice, d.currency, locale: locale))),
                const SizedBox(width: AppTheme.space2),
                Expanded(
                    child: Stat(
                        label: t.entryFee,
                        value: Fmt.money(d.entryFee, d.currency, locale: locale))),
              ],
            ),
            const SizedBox(height: 18),

            if (d.hasJoined)
              StatusPill(t.joined, color: AppTheme.success, icon: Icons.check_rounded)
            else
              FilledButton.icon(
                onPressed: _busy ? null : _join,
                icon: _busy
                    ? const SizedBox(
                        height: 18, width: 18, child: CircularProgressIndicator(strokeWidth: 2))
                    : const Icon(Icons.how_to_reg_rounded),
                label: Text('${t.joinAuction} · ${Fmt.money(d.entryFee, d.currency, locale: locale)}'),
              ),
          ],
        ),
      ),
    );
  }
}

/// The threshold is met and the start is scheduled.
class _WaitingPanel extends StatelessWidget {
  const _WaitingPanel({required this.detail});

  final AuctionDetail detail;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    // The countdown is measured against the server's scheduled start, corrected by the offset
    // captured when the detail was fetched.
    final offset = detail.serverTime.difference(DateTime.now().toUtc());
    final until = detail.scheduledStartAt == null
        ? Duration.zero
        : detail.scheduledStartAt!.difference(DateTime.now().toUtc().add(offset));

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Column(
          children: [
            Text(t.participantsComplete,
                style: const TextStyle(fontWeight: FontWeight.w600, color: AppTheme.success)),
            const SizedBox(height: 18),
            Text(t.auctionStartsIn,
                style: TextStyle(color: Theme.of(context).colorScheme.onSurfaceVariant)),
            const SizedBox(height: 6),
            Text(
              Fmt.duration(until.isNegative ? Duration.zero : until),
              style: Theme.of(context).textTheme.displaySmall?.copyWith(
                    fontWeight: FontWeight.bold,
                    fontFeatures: const [FontFeature.tabularFigures()],
                  ),
            ),
            const SizedBox(height: 14),
            Text(t.willNotifyOnStart,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodySmall),
          ],
        ),
      ),
    );
  }
}

/// The live auction. Becomes visually urgent once the final phase starts.
class _LivePanel extends StatelessWidget {
  const _LivePanel({required this.state, required this.snapshot, required this.detail});

  final LiveAuctionState state;
  final AuctionSnapshot snapshot;
  final AuctionDetail detail;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final urgent = snapshot.isFinalPhase;

    final accent = urgent ? AppTheme.finalPhase : AppTheme.live;

    return SurfaceCard(
      accent: accent,
      padding: const EdgeInsets.all(AppTheme.space5),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // Question one the user has: is this live, and how close is the end?
          Center(
            child: urgent
                ? StatusPill(t.finalPhase,
                    color: AppTheme.finalPhase,
                    icon: Icons.local_fire_department_rounded,
                    filled: true,
                    pulse: true)
                : StatusPill(t.liveAuctions,
                    color: AppTheme.live, icon: Icons.circle, filled: true, pulse: true),
          ),

          const SizedBox(height: AppTheme.space5),

          // Question two: what does it cost right now? This is the largest thing on the screen.
          PriceDisplay(
            label: t.currentPrice,
            amount: Fmt.money(snapshot.currentPrice, snapshot.currency, locale: locale),
            color: accent,
            animate: true,
            size: 40,
          ),

          const SizedBox(height: AppTheme.space5),

          // Question three: how long is left?
          _Countdown(remaining: state.remaining, urgent: urgent, label: t.remaining),

          const SizedBox(height: AppTheme.space5),

          TargetProgressBar(progress: snapshot.progress, urgent: urgent, height: 10),
          const SizedBox(height: AppTheme.space2),
          Row(
            mainAxisAlignment: MainAxisAlignment.spaceBetween,
            children: [
              Text(Fmt.money(snapshot.startingPrice, snapshot.currency, locale: locale),
                  style: theme.textTheme.bodySmall),
              Text('${t.progressToTarget} ${Fmt.percent(snapshot.progress)}',
                  style: theme.textTheme.bodySmall
                      ?.copyWith(fontWeight: FontWeight.w700, color: accent)),
              Text(Fmt.money(snapshot.targetPrice, snapshot.currency, locale: locale),
                  style: theme.textTheme.bodySmall?.copyWith(fontWeight: FontWeight.w700)),
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
                  valueColor: (snapshot.myRank ?? 99) <= 4 ? AppTheme.success : null,
                  align: CrossAxisAlignment.center,
                  ltrValue: true,
                ),
              ),
              const SizedBox(width: AppTheme.space2),
              Expanded(
                child: Stat(
                  label: t.yourHighest,
                  value: Fmt.money(snapshot.myHighestPrice ?? 0, snapshot.currency, locale: locale),
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

/// The countdown, split into labelled hour / minute / second blocks.
///
/// Each unit sits in its own box so the eye can read one number rather than parse a string, and
/// the separators do not drift as the digits change. Always left-to-right, even in Arabic: a
/// clock is not a sentence, and mirroring it would put the seconds where the hours belong.
class _Countdown extends StatelessWidget {
  const _Countdown({required this.remaining, required this.urgent, required this.label});

  final Duration remaining;
  final bool urgent;
  final String label;

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

    return Column(
      children: [
        Text(
          label.toUpperCase(),
          style: theme.textTheme.labelSmall?.copyWith(fontSize: 11, letterSpacing: 0.8),
        ),
        const SizedBox(height: AppTheme.space2),
        Directionality(
          textDirection: TextDirection.ltr,
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              for (var i = 0; i < parts.length; i++) ...[
                if (i > 0)
                  Padding(
                    padding: const EdgeInsets.symmetric(horizontal: 5),
                    child: Text(':',
                        style: theme.textTheme.headlineMedium
                            ?.copyWith(color: color.withValues(alpha: 0.4), height: 1)),
                  ),
                _TimeBlock(value: parts[i].$1, unit: parts[i].$2, color: color),
              ],
            ],
          ),
        ),
      ],
    );
  }
}

class _TimeBlock extends StatelessWidget {
  const _TimeBlock({required this.value, required this.unit, required this.color});

  final String value;
  final String unit;
  final Color color;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
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
              fontSize: 27,
              height: 1,
              color: color,
              fontFeatures: AppTheme.numeric,
            ),
          ),
          const SizedBox(height: 2),
          Text(
            unit,
            style: theme.textTheme.labelSmall?.copyWith(
              fontSize: 9.5,
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
class _BidBar extends ConsumerWidget {
  const _BidBar({required this.state, required this.snapshot, required this.auctionId});

  final LiveAuctionState state;
  final AuctionSnapshot snapshot;
  final String auctionId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final urgent = snapshot.isFinalPhase;

    final accent = urgent ? AppTheme.finalPhase : AppTheme.live;

    // What the price becomes if this bid lands. Shown so the button is never a leap of faith.
    final nextPrice = snapshot.currentPrice + snapshot.bidIncrement;
    final cappedNext = nextPrice > snapshot.targetPrice ? snapshot.targetPrice : nextPrice;

    return Container(
      decoration: BoxDecoration(
        color: theme.colorScheme.surface,
        border: Border(top: BorderSide(color: theme.colorScheme.outline)),
        boxShadow: [
          BoxShadow(
            color: Colors.black.withValues(alpha: theme.brightness == Brightness.dark ? 0.4 : 0.07),
            blurRadius: 20,
            offset: const Offset(0, -6),
          ),
        ],
      ),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
              AppTheme.space4, AppTheme.space3, AppTheme.space4, AppTheme.space3),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (!state.isConnected)
                Container(
                  width: double.infinity,
                  margin: const EdgeInsets.only(bottom: AppTheme.space2),
                  padding: const EdgeInsets.symmetric(
                      horizontal: AppTheme.space3, vertical: AppTheme.space2),
                  decoration: BoxDecoration(
                    color: theme.colorScheme.errorContainer,
                    borderRadius: BorderRadius.circular(AppTheme.radiusSm),
                  ),
                  child: Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Icon(Icons.wifi_off_rounded,
                          size: 14, color: theme.colorScheme.onErrorContainer),
                      const SizedBox(width: AppTheme.space2),
                      Flexible(
                        child: Text(
                          state.hubStatus == HubStatus.connecting
                              ? t.connectionLost
                              : t.bidsNeedConnection,
                          style: TextStyle(
                              fontSize: 12,
                              fontWeight: FontWeight.w600,
                              color: theme.colorScheme.onErrorContainer),
                          textAlign: TextAlign.center,
                        ),
                      ),
                    ],
                  ),
                ),

              // Current price and what it becomes, side by side, directly above the button.
              Padding(
                padding: const EdgeInsets.only(bottom: AppTheme.space3),
                child: Row(
                  children: [
                    Expanded(
                      child: Stat(
                        label: t.currentPrice,
                        value: Fmt.money(snapshot.currentPrice, snapshot.currency, locale: locale),
                      ),
                    ),
                    const SizedBox(width: AppTheme.space2),
                    Icon(Icons.arrow_forward_rounded,
                        size: 17, color: theme.colorScheme.onSurfaceVariant),
                    const SizedBox(width: AppTheme.space2),
                    Expanded(
                      child: Stat(
                        label: t.yourNextBid,
                        value: Fmt.money(cappedNext, snapshot.currency, locale: locale),
                        valueColor: accent,
                        align: CrossAxisAlignment.end,
                      ),
                    ),
                  ],
                ),
              ),

              FilledButton(
                onPressed: state.canBid
                    ? () => ref.read(liveAuctionProvider(auctionId).notifier).placeBid()
                    : null,
                style: FilledButton.styleFrom(
                  backgroundColor: accent,
                  foregroundColor: Colors.white,
                  minimumSize: const Size.fromHeight(58),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(AppTheme.radiusMd),
                  ),
                ),
                child: state.isBidding
                    ? const SizedBox(
                        height: 22,
                        width: 22,
                        child: CircularProgressIndicator(strokeWidth: 2.4, color: Colors.white),
                      )
                    : Row(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          const Icon(Icons.gavel_rounded, size: 19),
                          const SizedBox(width: AppTheme.space2),
                          Text(
                            t.bidButton(Fmt.money(
                                snapshot.bidIncrement, snapshot.currency, locale: locale)),
                            style: const TextStyle(fontSize: 16.5, fontWeight: FontWeight.w800),
                          ),
                        ],
                      ),
              ),
              const SizedBox(height: AppTheme.space2),

              if (state.waitingForOtherBidder) ...[
                Text(t.waitForOtherBidder,
                    style: theme.textTheme.bodySmall?.copyWith(
                        color: accent, fontWeight: FontWeight.w700),
                    textAlign: TextAlign.center),
                const SizedBox(height: AppTheme.space1),
              ],

              // The increment moves the price; it is not a charge. Saying so prevents the most
              // common misunderstanding of this auction format.
              Text(t.bidIncrementNote,
                  style: theme.textTheme.labelSmall?.copyWith(fontSize: 11),
                  textAlign: TextAlign.center),
            ],
          ),
        ),
      ),
    );
  }
}

class _ResultsPanel extends ConsumerWidget {
  const _ResultsPanel({required this.detail});

  final AuctionDetail detail;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final winnersAsync = ref.watch(auctionWinnersProvider(detail.id));
    final myUserId = ref.watch(authControllerProvider).user?.id;

    return winnersAsync.when(
      loading: () => const Card(
        child: Padding(padding: EdgeInsets.all(24), child: Center(child: CircularProgressIndicator())),
      ),
      error: (_, _) => Card(
        child: Padding(
          padding: const EdgeInsets.all(20),
          child: Center(child: Text(t.auctionFinished)),
        ),
      ),
      data: (winners) {
        final me = winners.where((w) => w.userId == myUserId).firstOrNull;

        return Column(
          children: [
            Card(
              color: me != null
                  ? AppTheme.success.withValues(alpha: 0.1)
                  : Theme.of(context).colorScheme.surfaceContainerHighest,
              child: Padding(
                padding: const EdgeInsets.all(20),
                child: me != null
                    ? Column(
                        children: [
                          Text(t.congratulations,
                              style: Theme.of(context).textTheme.titleLarge),
                          const SizedBox(height: 6),
                          Text(t.youFinished(me.rank),
                              style: const TextStyle(fontWeight: FontWeight.w600)),
                          const SizedBox(height: 10),
                          Text('${me.prizeName} · ${Fmt.money(me.prizeValue, me.currency, locale: locale)}'),
                          if (me.claimStatus == 'PaymentRequired') ...[
                            const SizedBox(height: 16),
                            FilledButton(
                              onPressed: () => context.push('/prizes'),
                              child: Text(t.payForPrize),
                            ),
                          ],
                        ],
                      )
                    : Column(
                        children: [
                          Text(t.auctionFinished,
                              style: Theme.of(context).textTheme.titleMedium),
                          const SizedBox(height: 8),
                          if (detail.hasJoined) ...[
                            Text(t.didNotPlace, textAlign: TextAlign.center),
                            const SizedBox(height: 6),
                            Text(
                              t.entryFeeReturned(
                                  Fmt.money(detail.entryFee, detail.currency, locale: locale)),
                              textAlign: TextAlign.center,
                              style: const TextStyle(fontWeight: FontWeight.w600),
                            ),
                            const SizedBox(height: 16),
                            FilledButton.icon(
                              onPressed: () => context.go('/store'),
                              icon: const Icon(Icons.storefront_rounded),
                              label: Text(t.shopNow),
                            ),
                          ],
                        ],
                      ),
              ),
            ),
            const SizedBox(height: 18),
            SectionHeader(t.winners),
            ...winners.map((w) => Card(
                  margin: const EdgeInsets.only(bottom: 8),
                  child: ListTile(
                    leading: CircleAvatar(child: LtrText('#${w.rank}')),
                    title: Text(w.prizeName),
                    subtitle: Text(
                        '${w.displayName} · ${Fmt.money(w.highestPrice, w.currency, locale: locale)}'),
                    trailing: Text(Fmt.money(w.prizeValue, w.currency, locale: locale),
                        style: const TextStyle(fontWeight: FontWeight.w600)),
                  ),
                )),
          ],
        );
      },
    );
  }
}

class _PrizeList extends StatelessWidget {
  const _PrizeList({required this.detail, this.livePrice});

  final AuctionDetail detail;

  /// The price pushed over the live connection, newer than the one in [detail].
  final double? livePrice;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    // Once the auction is over its closing price sets the discount exactly. Before that, the most a
    // winner can pay is at the target price, where the auction stops.
    final exact = detail.isComplete;
    final auctionPrice = exact ? (livePrice ?? detail.currentPrice) : detail.targetPrice;
    final discount = discountAt(auctionPrice, detail.originalPrice);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('${t.prizes} (${detail.prizes.length})',
            style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 4),
        Text(t.discountAppliesToAll, style: theme.textTheme.bodySmall),
        const SizedBox(height: 10),
        for (final p in detail.prizes)
          Padding(
              padding: const EdgeInsets.only(bottom: AppTheme.space3),
              child: SurfaceCard(
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
                            p.imageUrl,
                            height: 170,
                            width: double.infinity,
                            radius: 0,
                          ),
                          PositionedDirectional(
                            top: 12,
                            start: 12,
                            child: StatusPill(
                              t.prizeRank(p.rank),
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
                              p.productName,
                              style: theme.textTheme.titleMedium?.copyWith(
                                fontWeight: FontWeight.w700,
                              ),
                            ),
                            if (p.description != null && p.description!.trim().isNotEmpty) ...[
                              const SizedBox(height: AppTheme.space2),
                              Text(
                                p.description!,
                                style: theme.textTheme.bodyMedium,
                              ),
                            ],
                            const SizedBox(height: AppTheme.space3),
                            PrizePriceBox(
                              fullPrice: p.originalPrice,
                              price: prizePriceAt(
                                p.originalPrice,
                                auctionPrice,
                                detail.originalPrice,
                              ),
                              discount: discount,
                              currency: p.currency,
                              exact: exact,
                            ),
                          ],
                        )),
                  ],
                ),
            )),
      ],
    );
  }
}

class _Leaderboard extends StatelessWidget {
  const _Leaderboard({required this.entries, required this.currency});

  final List<LeaderboardEntry> entries;
  final String currency;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(t.leaderboard,
            style: Theme.of(context).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 10),
        Card(
          child: Column(
            children: [
              for (final e in entries)
                ListTile(
                  dense: true,
                  leading: CircleAvatar(
                    radius: 15,
                    backgroundColor: e.rank <= 4
                        ? AppTheme.gold.withValues(alpha: 0.25)
                        : Theme.of(context).colorScheme.surfaceContainerHighest,
                    child: Text('${e.rank}', style: const TextStyle(fontSize: 12)),
                  ),
                  title: Text(e.isMe ? '${e.displayName} (you)' : e.displayName,
                      style: TextStyle(
                          fontWeight: e.isMe ? FontWeight.bold : FontWeight.normal)),
                  trailing: Text(Fmt.money(e.highestPrice, currency, locale: locale)),
                ),
            ],
          ),
        ),
      ],
    );
  }
}
