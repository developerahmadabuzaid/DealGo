import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/auto_refresh.dart';
import '../../../shared/app_header.dart';
import '../../../shared/widgets.dart';
import '../data/auction_models.dart';
import 'auction_end_dialog.dart';
import 'auction_results_screen.dart';
import 'bidding_arena_screen.dart';
import 'live_auction_controller.dart';
import 'live_bidding_widgets.dart';
import 'top_bidders_panel.dart';

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
    final showResults =
        await BiddingArenaScreen.open(context, auctionId: widget.auctionId, title: title);
    _arenaOpen = false;
    // The auction ended while the screen was open and the user asked for the results.
    if (showResults == true && mounted) context.push('/auctions/${widget.auctionId}/results');
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
      // The live bidding screen announces the end itself when it is the one open.
      if (!_arenaOpen) {
        announceAuctionEnd(context, before: prev?.snapshot, now: next.snapshot);
      }
      if (next.rankMessage != null && next.rankMessage != prev?.rankMessage) {
        // The live bidding screen reports this itself, and clears it once shown.
        if (_arenaOpen) return;
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
              if (isLive) ConnectionIndicator(status: live.hubStatus),
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
                  LivePanel(state: live, snapshot: snapshot)
                else if (detail.isComplete)
                  _ResultsPanel(detail: detail),

                // Who is ahead, right under the live state and above the prizes, so it is seen
                // without scrolling past four large pictures first.
                if (snapshot != null && isLive) ...[
                  const SizedBox(height: 22),
                  TopBiddersPanel(
                    entries: snapshot.leaderboard,
                    currency: snapshot.currency,
                    participantCount: snapshot.participantCount,
                  ),
                ],

                const SizedBox(height: 22),
                _PrizeList(detail: detail, livePrice: snapshot?.currentPrice),
              ],
            ),
          ),
          bottomNavigationBar: isLive && snapshot != null && detail.hasJoined
              ? BidBar(state: live, snapshot: snapshot, auctionId: widget.auctionId)
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
class _WaitingPanel extends StatefulWidget {
  const _WaitingPanel({required this.detail});

  final AuctionDetail detail;

  @override
  State<_WaitingPanel> createState() => _WaitingPanelState();
}

class _WaitingPanelState extends State<_WaitingPanel> {
  Timer? _tick;

  /// serverTime − deviceTime, taken once when a detail arrives. Working it out again on every
  /// build cancelled "now" out of the sum, and the clock stood still between refreshes.
  late Duration _offset;

  @override
  void initState() {
    super.initState();
    _measureOffset();
    _tick = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() {});
    });
  }

  @override
  void didUpdateWidget(_WaitingPanel old) {
    super.didUpdateWidget(old);
    // A refresh brings a fresh server time; measure against that one.
    if (old.detail.serverTime != widget.detail.serverTime) _measureOffset();
  }

  void _measureOffset() =>
      _offset = widget.detail.serverTime.difference(DateTime.now().toUtc());

  @override
  void dispose() {
    _tick?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final detail = widget.detail;

    // The countdown is measured against the server's scheduled start, read on the server's clock.
    final until = detail.scheduledStartAt == null
        ? Duration.zero
        : detail.scheduledStartAt!.difference(DateTime.now().toUtc().add(_offset));

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





class _ResultsPanel extends ConsumerWidget {
  const _ResultsPanel({required this.detail});

  final AuctionDetail detail;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final winners = ref.watch(auctionWinnersProvider(detail.id)).valueOrNull ?? const [];
    void openResults() => context.push('/auctions/${detail.id}/results');

    if (detail.hasJoined) {
      return MyResultCard(detail: detail, winners: winners, onShowResults: openResults);
    }

    // Someone who only watched still gets the outcome, one tap away.
    return SurfaceCard(
      padding: const EdgeInsets.all(AppTheme.space5),
      child: Column(
        children: [
          const Icon(Icons.emoji_events_rounded, color: AppTheme.gold, size: 32),
          const SizedBox(height: AppTheme.space2),
          Text(t.auctionFinished, style: theme.textTheme.titleMedium),
          const SizedBox(height: AppTheme.space4),
          FilledButton.icon(
            onPressed: openResults,
            icon: const Icon(Icons.leaderboard_rounded),
            label: Text(t.viewResults),
            style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(48)),
          ),
        ],
      ),
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

