import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/localization/app_strings.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';
import '../data/auction_models.dart';
import 'auction_end_dialog.dart';
import 'live_auction_controller.dart';
import 'live_bidding_widgets.dart';
import 'top_bidders_panel.dart';

/// Live bidding: one auction, on its own, while it runs.
///
/// It is the auction page without the catalogue — the price, the clock, the user's place, the
/// leading bidders and the bid button — in the same calm, light design as the rest of the app. The
/// movement that is left is there to inform: the price rolls to each new value, the live badge
/// breathes, and a short notice says what just changed for the user.
///
/// It draws from the same [liveAuctionProvider] as the auction page, so nothing here decides a
/// price, a rank or a winner: the server's snapshot and its pushed events are the only source.
class BiddingArenaScreen extends ConsumerStatefulWidget {
  const BiddingArenaScreen({super.key, required this.auctionId, required this.title});

  final String auctionId;
  final String title;

  /// Opens the screen over everything else. Completes with true when the user asked to see the
  /// results of an auction that ended while it was open.
  static Future<bool?> open(BuildContext context,
      {required String auctionId, required String title}) {
    return Navigator.of(context, rootNavigator: true).push(MaterialPageRoute<bool>(
      fullscreenDialog: true,
      builder: (_) => BiddingArenaScreen(auctionId: auctionId, title: title),
    ));
  }

  @override
  ConsumerState<BiddingArenaScreen> createState() => _BiddingArenaScreenState();
}

/// What a notice is about, which sets its colour and icon.
enum _NoticeKind { good, warning, info }

class _BiddingArenaScreenState extends ConsumerState<BiddingArenaScreen> {
  Timer? _clock;
  Timer? _noticeTimer;

  String? _notice;
  _NoticeKind _noticeKind = _NoticeKind.info;

  bool _wasFinal = false;

  @override
  void initState() {
    super.initState();

    // Repaints the countdown. The time itself comes from the server's end time.
    _clock = Timer.periodic(const Duration(milliseconds: 500), (_) {
      if (mounted) setState(() {});
    });

    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      _wasFinal = ref.read(liveAuctionProvider(widget.auctionId)).snapshot?.isFinalPhase ?? false;
    });
  }

  void _say(String message, _NoticeKind kind) {
    _noticeTimer?.cancel();
    setState(() {
      _notice = message;
      _noticeKind = kind;
    });
    _noticeTimer = Timer(const Duration(seconds: 4), () {
      if (mounted) setState(() => _notice = null);
    });
  }

  /// My place, from the leaderboard when it has me (it is pushed with every change), otherwise
  /// from the last snapshot.
  int? _myRank(AuctionSnapshot s) {
    for (final e in s.leaderboard) {
      if (e.isMe) return e.rank;
    }
    return s.myRank;
  }

  void _onStateChange(LiveAuctionState? prev, LiveAuctionState next) {
    final t = AppStrings.of(context);
    final notifier = ref.read(liveAuctionProvider(widget.auctionId).notifier);

    // Anything the server refused is shown first, whatever else changed with it.
    if (next.error != null && next.error != prev?.error) {
      _say(next.error!, _NoticeKind.warning);
      HapticFeedback.mediumImpact();
      notifier.clearError();
      return;
    }

    // The controller reports the rank changes the user's own bid caused.
    if (next.rankMessage != null && next.rankMessage != prev?.rankMessage) {
      final joined = next.rankMessage == 'inTopFour';
      _say(joined ? t.inTopFour : t.lostFourthPlace,
          joined ? _NoticeKind.good : _NoticeKind.warning);
      notifier.clearRankMessage();
      return;
    }

    final before = prev?.snapshot;
    final now = next.snapshot;
    if (now == null) return;

    // The auction closed while the user was watching: say who won, and whether it was them.
    if ((before?.isLive ?? false) && !now.isLive) {
      announceAuctionEnd(context, before: before, now: now);
      return;
    }

    if (now.isFinalPhase && !_wasFinal) {
      _wasFinal = true;
      _say(t.finalPhaseStarted, _NoticeKind.warning);
      HapticFeedback.mediumImpact();
      return;
    }

    if (before == null || next.isBidding) return;

    // Someone else's bid moved me in or out of the prize places.
    final rankBefore = _myRank(before);
    final rankNow = _myRank(now);
    if (rankBefore != null && rankBefore <= 4 && rankNow != null && rankNow > 4) {
      _say(t.lostFourthPlace, _NoticeKind.warning);
      return;
    }

    final raised = now.currentPrice > before.currentPrice;
    final mine = (now.myHighestPrice ?? 0) >= now.currentPrice;
    if (raised && !mine && (now.myHighestPrice ?? 0) > 0) {
      _say(t.outbidNotice, _NoticeKind.info);
    }
  }

  @override
  void dispose() {
    _clock?.cancel();
    _noticeTimer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final live = ref.watch(liveAuctionProvider(widget.auctionId));
    ref.listen(liveAuctionProvider(widget.auctionId), _onStateChange);

    final s = live.snapshot;
    final ended = s != null && !s.isLive;

    return Scaffold(
      backgroundColor:
          (s?.isFinalPhase ?? false) ? AppTheme.urgentDeep.withValues(alpha: 0.04) : null,
      appBar: AppBar(
        leading: IconButton(
          onPressed: () => Navigator.of(context).pop(),
          tooltip: t.arenaLeave,
          icon: const Icon(Icons.close_rounded),
        ),
        title: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(widget.title, maxLines: 1, overflow: TextOverflow.ellipsis,
                style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w700)),
            Text(t.liveBidding,
                style: Theme.of(context).textTheme.labelSmall),
          ],
        ),
        centerTitle: true,
        actions: [ConnectionIndicator(status: live.hubStatus), const SizedBox(width: 8)],
      ),
      body: s == null
          ? Center(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const CircularProgressIndicator(),
                  const SizedBox(height: AppTheme.space4),
                  Text(t.arenaConnecting),
                ],
              ),
            )
          : ended
              ? _Ended(onResults: () => Navigator.of(context).pop(true))
              : ListView(
                  padding: const EdgeInsets.fromLTRB(
                      AppTheme.space4, AppTheme.space3, AppTheme.space4, AppTheme.space6),
                  children: [
                    AnimatedSwitcher(
                      duration: const Duration(milliseconds: 250),
                      transitionBuilder: (child, animation) => SizeTransition(
                        sizeFactor: animation,
                        child: FadeTransition(opacity: animation, child: child),
                      ),
                      child: _notice == null
                          ? const SizedBox(width: double.infinity)
                          : Padding(
                              key: ValueKey(_notice),
                              padding: const EdgeInsets.only(bottom: AppTheme.space3),
                              child: _Notice(message: _notice!, kind: _noticeKind),
                            ),
                    ),
                    // Compact, so the leading bidders start on the first screen rather than
                    // behind the bid bar.
                    LivePanel(state: live, snapshot: s, compact: true),
                    const SizedBox(height: AppTheme.space4),
                    TopBiddersPanel(
                      entries: s.leaderboard,
                      currency: s.currency,
                      participantCount: s.participantCount,
                      dense: true,
                    ),
                  ],
                ),
      bottomNavigationBar: s != null && !ended
          ? BidBar(state: live, snapshot: s, auctionId: widget.auctionId, compact: true)
          : null,
    );
  }
}

/// A short line about what just changed for the user.
class _Notice extends StatelessWidget {
  const _Notice({required this.message, required this.kind});

  final String message;
  final _NoticeKind kind;

  @override
  Widget build(BuildContext context) {
    final (color, icon) = switch (kind) {
      _NoticeKind.good => (AppTheme.success, Icons.verified_rounded),
      _NoticeKind.warning => (AppTheme.urgentDeep, Icons.error_outline_rounded),
      _NoticeKind.info => (AppTheme.info, Icons.trending_up_rounded),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: AppTheme.space3, vertical: 10),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(AppTheme.radiusMd),
        border: Border.all(color: color.withValues(alpha: 0.3)),
      ),
      child: Row(
        children: [
          Icon(icon, size: 20, color: color),
          const SizedBox(width: AppTheme.space2),
          Expanded(
            child: Text(message,
                style: TextStyle(color: color, fontWeight: FontWeight.w700, fontSize: 13.5)),
          ),
        ],
      ),
    );
  }
}

/// Shown in place of the live view once the auction is over.
class _Ended extends StatelessWidget {
  const _Ended({required this.onResults});

  final VoidCallback onResults;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(AppTheme.space6),
        child: SurfaceCard(
          padding: const EdgeInsets.all(AppTheme.space6),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 64,
                height: 64,
                decoration: BoxDecoration(
                  color: AppTheme.gold.withValues(alpha: 0.15),
                  shape: BoxShape.circle,
                ),
                child: const Icon(Icons.emoji_events_rounded, size: 34, color: AppTheme.gold),
              ),
              const SizedBox(height: AppTheme.space4),
              Text(t.auctionFinished, style: theme.textTheme.titleLarge),
              const SizedBox(height: AppTheme.space2),
              Text(t.auctionEndedBody,
                  textAlign: TextAlign.center, style: theme.textTheme.bodyMedium),
              const SizedBox(height: AppTheme.space5),
              FilledButton.icon(
                onPressed: onResults,
                icon: const Icon(Icons.leaderboard_rounded),
                label: Text(t.viewResults),
                style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(50)),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
