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
import '../../../shared/widgets.dart';
import '../data/auction_models.dart';
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

    return detailAsync.when(
      loading: () => Scaffold(
        appBar: AppBar(),
        body: const Center(child: CircularProgressIndicator()),
      ),
      error: (e, _) => Scaffold(
        appBar: AppBar(),
        body: ErrorView(
          message: e is ApiException ? e.message : e.toString(),
          onRetry: () => ref.invalidate(auctionDetailProvider(widget.auctionId)),
        ),
      ),
      data: (detail) {
        final snapshot = live.snapshot;
        final isLive = snapshot?.isLive ?? detail.isLive;

        return Scaffold(
          backgroundColor: (snapshot?.isFinalPhase ?? false)
              ? AppTheme.urgentDeep.withValues(alpha: 0.05)
              : null,
          appBar: AppBar(
            title: Text(detail.auctionNumber),
            actions: [
              if (isLive) _ConnectionIndicator(status: live.hubStatus),
              const SizedBox(width: 8),
            ],
          ),
          body: RefreshIndicator(
            onRefresh: () async {
              ref.invalidate(auctionDetailProvider(widget.auctionId));
              await ref.read(liveAuctionProvider(widget.auctionId).notifier).resync();
            },
            child: ListView(
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 120),
              children: [
                if (live.error != null)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 12),
                    child: _Banner(
                      text: live.error!,
                      color: Theme.of(context).colorScheme.errorContainer,
                    ),
                  ),

                RemoteImage(detail.mainImageUrl, height: 190, width: double.infinity),
                const SizedBox(height: 14),

                Text(detail.title,
                    style: Theme.of(context)
                        .textTheme
                        .headlineSmall
                        ?.copyWith(fontWeight: FontWeight.bold)),
                const SizedBox(height: 4),
                Text(detail.mainProductName,
                    style: TextStyle(color: Theme.of(context).colorScheme.onSurfaceVariant)),

                const SizedBox(height: 18),

                if (detail.acceptsRegistration)
                  _RegistrationPanel(detail: detail)
                else if (detail.isWaitingToStart)
                  _WaitingPanel(detail: detail)
                else if (isLive && snapshot != null)
                  _LivePanel(state: live, snapshot: snapshot, detail: detail)
                else if (detail.isComplete)
                  _ResultsPanel(detail: detail),

                const SizedBox(height: 22),
                _PrizeList(detail: detail),

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
          SnackBar(content: Text(e.message), backgroundColor: AppTheme.urgentDeep),
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
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text('${d.participantCount} / ${d.requiredParticipants}',
                    style: Theme.of(context)
                        .textTheme
                        .headlineSmall
                        ?.copyWith(fontWeight: FontWeight.bold)),
                Text(t.participantsRemaining(d.participantsRemaining)),
              ],
            ),
            const SizedBox(height: 10),
            TargetProgressBar(
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
                Expanded(
                    child: Stat(
                        label: t.targetPrice,
                        value: Fmt.money(d.targetPrice, d.currency, locale: locale))),
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
            Text(t.countdownComplete(detail.participantCount, detail.requiredParticipants),
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

    return Card(
      color: urgent ? AppTheme.urgentDeep.withValues(alpha: 0.08) : null,
      child: Padding(
        padding: const EdgeInsets.all(18),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (urgent) ...[
              Center(
                child: Text('🔥 ${t.finalPhase}',
                    style: theme.textTheme.titleMedium?.copyWith(
                        color: AppTheme.urgentDeep, fontWeight: FontWeight.bold)),
              ),
              const SizedBox(height: 14),
            ],

            // The countdown is always visible during the final phase.
            Center(
              child: Column(
                children: [
                  Text(t.remaining, style: theme.textTheme.bodySmall),
                  AnimatedDefaultTextStyle(
                    duration: const Duration(milliseconds: 250),
                    style: theme.textTheme.displaySmall!.copyWith(
                      fontWeight: FontWeight.bold,
                      color: urgent ? AppTheme.urgentDeep : theme.colorScheme.onSurface,
                      fontFeatures: const [FontFeature.tabularFigures()],
                    ),
                    child: Text(Fmt.duration(state.remaining)),
                  ),
                ],
              ),
            ),

            const SizedBox(height: 20),

            Center(
              child: Column(
                children: [
                  Text(t.currentPrice, style: theme.textTheme.bodySmall),
                  AnimatedSwitcher(
                    duration: const Duration(milliseconds: 260),
                    transitionBuilder: (child, animation) =>
                        ScaleTransition(scale: animation, child: child),
                    child: Text(
                      Fmt.money(snapshot.currentPrice, snapshot.currency, locale: locale),
                      key: ValueKey(snapshot.currentPrice),
                      style: theme.textTheme.displaySmall?.copyWith(
                        fontWeight: FontWeight.bold,
                        color: urgent ? AppTheme.urgent : theme.colorScheme.primary,
                      ),
                    ),
                  ),
                ],
              ),
            ),

            const SizedBox(height: 16),
            TargetProgressBar(progress: snapshot.progress, urgent: urgent),
            const SizedBox(height: 6),
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                Text(Fmt.money(snapshot.startingPrice, snapshot.currency, locale: locale),
                    style: theme.textTheme.bodySmall),
                Text('${t.progressToTarget} ${Fmt.percent(snapshot.progress)}',
                    style: theme.textTheme.bodySmall),
                Text(Fmt.money(snapshot.targetPrice, snapshot.currency, locale: locale),
                    style: theme.textTheme.bodySmall?.copyWith(fontWeight: FontWeight.w700)),
              ],
            ),

            const Divider(height: 30),

            Row(
              children: [
                Expanded(
                  child: Stat(
                    label: t.yourRank,
                    value: snapshot.myRank == null ? '—' : '#${snapshot.myRank}',
                    valueColor: (snapshot.myRank ?? 99) <= 4 ? AppTheme.success : null,
                    align: CrossAxisAlignment.center,
                  ),
                ),
                Expanded(
                  child: Stat(
                    label: t.yourHighest,
                    value: Fmt.money(snapshot.myHighestPrice ?? 0, snapshot.currency, locale: locale),
                    align: CrossAxisAlignment.center,
                  ),
                ),
                Expanded(
                  child: Stat(
                    label: t.participants,
                    value: Fmt.number(snapshot.participantCount),
                    align: CrossAxisAlignment.center,
                  ),
                ),
              ],
            ),
          ],
        ),
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

    return SafeArea(
      child: Container(
        padding: const EdgeInsets.fromLTRB(16, 12, 16, 12),
        decoration: BoxDecoration(
          color: theme.colorScheme.surface,
          border: Border(top: BorderSide(color: theme.dividerColor)),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            if (!state.isConnected)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Text(
                  state.hubStatus == HubStatus.connecting ? t.connectionLost : t.bidsNeedConnection,
                  style: TextStyle(fontSize: 12, color: theme.colorScheme.error),
                  textAlign: TextAlign.center,
                ),
              ),

            FilledButton(
              onPressed: state.canBid
                  ? () => ref.read(liveAuctionProvider(auctionId).notifier).placeBid()
                  : null,
              style: FilledButton.styleFrom(
                backgroundColor: urgent ? AppTheme.urgentDeep : null,
                minimumSize: const Size.fromHeight(56),
              ),
              child: state.isBidding
                  ? const SizedBox(
                      height: 22, width: 22,
                      child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                  : Text(
                      t.bidButton(Fmt.money(snapshot.bidIncrement, snapshot.currency, locale: locale)),
                      style: const TextStyle(fontSize: 17, fontWeight: FontWeight.bold),
                    ),
            ),
            const SizedBox(height: 6),
            // The increment moves the price; it is not a charge. Saying so prevents the most
            // common misunderstanding of this auction format.
            Text(t.bidIncrementNote,
                style: theme.textTheme.bodySmall, textAlign: TextAlign.center),
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
                    leading: CircleAvatar(child: Text('#${w.rank}')),
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
  const _PrizeList({required this.detail});

  final AuctionDetail detail;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text('${t.prizes} (${detail.prizes.length})',
            style: Theme.of(context).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700)),
        const SizedBox(height: 10),
        ...detail.prizes.map((p) => Card(
              margin: const EdgeInsets.only(bottom: 8),
              child: ListTile(
                leading: RemoteImage(p.imageUrl, height: 46, width: 46, radius: 8),
                title: Text(p.productName),
                subtitle: Text('#${p.rank}'),
                trailing: Text(Fmt.money(p.originalPrice, p.currency, locale: locale),
                    style: const TextStyle(fontWeight: FontWeight.w600)),
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
