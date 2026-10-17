import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/arena_sounds.dart';
import '../data/auction_models.dart';
import 'live_auction_controller.dart';

/// The bidding arena: a full-screen view of one live auction and nothing else.
///
/// Everything the player needs is on one dark stage — the price, the clock, their place and one big
/// bid button — with moving light behind it and a line of text that keeps urging them on. When the
/// final phase starts the stage turns red, beats like a heart and shakes once, the way a game marks
/// its last round.
///
/// It draws from the same [liveAuctionProvider] as the auction page, so nothing here decides a
/// price, a rank or a winner: the server's snapshot and its pushed events are the only source.
class BiddingArenaScreen extends ConsumerStatefulWidget {
  const BiddingArenaScreen({super.key, required this.auctionId, required this.title});

  final String auctionId;
  final String title;

  /// Opens the arena over everything else, with a fade-and-zoom entrance.
  static Future<void> open(BuildContext context,
      {required String auctionId, required String title}) {
    return Navigator.of(context, rootNavigator: true).push(PageRouteBuilder<void>(
      opaque: true,
      fullscreenDialog: true,
      transitionDuration: const Duration(milliseconds: 420),
      reverseTransitionDuration: const Duration(milliseconds: 260),
      pageBuilder: (_, _, _) => BiddingArenaScreen(auctionId: auctionId, title: title),
      transitionsBuilder: (_, animation, _, child) {
        final curved = CurvedAnimation(parent: animation, curve: Curves.easeOutCubic);
        return FadeTransition(
          opacity: curved,
          child: ScaleTransition(scale: Tween(begin: 1.08, end: 1.0).animate(curved), child: child),
        );
      },
    ));
  }

  @override
  ConsumerState<BiddingArenaScreen> createState() => _BiddingArenaScreenState();
}

class _BiddingArenaScreenState extends ConsumerState<BiddingArenaScreen>
    with TickerProviderStateMixin {
  /// Drives the drifting glows. Runs for as long as the screen is open.
  late final AnimationController _ambient =
      AnimationController(vsync: this, duration: const Duration(seconds: 9))..repeat();

  /// The heartbeat: slow while live, fast once the final phase starts.
  late final AnimationController _pulse =
      AnimationController(vsync: this, duration: const Duration(milliseconds: 1400))..repeat();

  /// One shake of the whole stage, played when the final phase starts.
  late final AnimationController _shake =
      AnimationController(vsync: this, duration: const Duration(milliseconds: 650));

  /// A banner that flashes across the stage: "the auction is live", "final phase".
  late final AnimationController _banner =
      AnimationController(vsync: this, duration: const Duration(milliseconds: 2200));
  String? _bannerTitle;
  String? _bannerSub;

  Timer? _clock;
  Timer? _phraseTimer;
  int _phraseIndex = 0;

  /// A line that answers something that just happened. Shown in place of the rotating phrases for
  /// a few seconds, then they resume.
  String? _eventPhrase;

  /// What the server last refused, and the timer that takes it away again.
  String? _error;
  Timer? _errorTimer;

  /// Repeats the alarm through the final phase, so the pressure does not fade after one blast.
  Timer? _alarmTimer;
  Timer? _eventTimer;

  bool _wasFinal = false;

  @override
  void initState() {
    super.initState();

    // Nothing but the auction: the system bars get out of the way until the arena closes.
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.immersiveSticky);

    // Repaints the countdown. The time itself comes from the server's end time.
    _clock = Timer.periodic(const Duration(milliseconds: 250), (_) {
      if (mounted) setState(() {});
    });
    _schedulePhrases(const Duration(milliseconds: 3200));

    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final snapshot = ref.read(liveAuctionProvider(widget.auctionId)).snapshot;
      _wasFinal = snapshot?.isFinalPhase ?? false;
      final t = AppStrings.of(context);
      if (_wasFinal) {
        // Joining a final phase already in progress: the alarm belongs to the moment it began,
        // so the screen shakes and sounds once on arrival rather than pretending it just started.
        _enterFinalPhase(t, playShake: false);
        ArenaSounds.instance.play(ArenaSound.finalPhase, volume: 0.85);
      } else {
        _flash(t.arenaStarted, t.arenaLivePhrases.first);
        ArenaSounds.instance.play(ArenaSound.start);
      }
    });
  }

  void _schedulePhrases(Duration every) {
    _phraseTimer?.cancel();
    _phraseTimer = Timer.periodic(every, (_) {
      if (mounted && _eventPhrase == null) setState(() => _phraseIndex++);
    });
  }

  /// Shows what went wrong, on top of everything.
  ///
  /// The arena covers the whole screen, so the page underneath — where a failed bid used to be
  /// reported — cannot be seen. Without this, "not enough balance" or "the auction has ended"
  /// happened in silence and the button simply did nothing.
  void _showError(String message) {
    _errorTimer?.cancel();
    setState(() => _error = message);
    HapticFeedback.heavyImpact();

    _errorTimer = Timer(const Duration(seconds: 5), () {
      if (mounted) setState(() => _error = null);
    });
  }

  void _say(String phrase) {
    _eventTimer?.cancel();
    setState(() => _eventPhrase = phrase);
    _eventTimer = Timer(const Duration(milliseconds: 3200), () {
      if (mounted) setState(() => _eventPhrase = null);
    });
  }

  void _flash(String title, String? sub) {
    setState(() {
      _bannerTitle = title;
      _bannerSub = sub;
    });
    _banner.forward(from: 0);
  }

  void _enterFinalPhase(AppStrings t, {bool playShake = true}) {
    _pulse.duration = const Duration(milliseconds: 620);
    _pulse.repeat();
    _schedulePhrases(const Duration(milliseconds: 2000));
    _flash(t.arenaFinalPhase, t.arenaFinalPhaseSub);
    if (playShake) {
      _shake.forward(from: 0);
      HapticFeedback.heavyImpact();
      ArenaSounds.instance.play(ArenaSound.finalPhase);
    }

    // Every ten seconds, quieter than the first one: present, not deafening.
    _alarmTimer?.cancel();
    _alarmTimer = Timer.periodic(const Duration(seconds: 10), (_) {
      if (!mounted) return;
      final live = ref.read(liveAuctionProvider(widget.auctionId)).snapshot;
      if (live == null || !live.isFinalPhase || !live.isLive) {
        _alarmTimer?.cancel();
        return;
      }
      ArenaSounds.instance.play(ArenaSound.finalPhase, volume: 0.55);
      HapticFeedback.mediumImpact();
    });
  }

  /// My place, from the leaderboard when it has me (it is pushed with every change), otherwise from
  /// the last snapshot.
  int? _myRank(AuctionSnapshot s) {
    for (final e in s.leaderboard) {
      if (e.isMe) return e.rank;
    }
    return s.myRank;
  }

  void _onStateChange(LiveAuctionState? prev, LiveAuctionState next) {
    // Anything the server refused is shown first, whatever else changed with it.
    if (next.error != null && next.error != prev?.error) {
      _showError(next.error!);
      ref.read(liveAuctionProvider(widget.auctionId).notifier).clearError();
    }

    final before = prev?.snapshot;
    final now = next.snapshot;
    if (now == null) return;
    final t = AppStrings.of(context);

    if (now.isFinalPhase && !_wasFinal) {
      _wasFinal = true;
      _enterFinalPhase(t);
      return;
    }

    if (before == null) return;

    final rankBefore = _myRank(before);
    final rankNow = _myRank(now);
    if (rankNow != null && rankNow <= 4 && (rankBefore == null || rankBefore > 4)) {
      _say(t.arenaJoinedWinners);
      HapticFeedback.mediumImpact();
      return;
    }
    if (rankBefore != null && rankBefore <= 4 && (rankNow == null || rankNow > 4)) {
      _say(t.arenaDroppedOut);
      HapticFeedback.heavyImpact();
      return;
    }

    // Someone else moved the price above my best.
    final raised = now.currentPrice > before.currentPrice;
    final mine = (now.myHighestPrice ?? 0) >= now.currentPrice;
    if (raised && !mine && !next.isBidding) {
      _say(t.arenaOutbid);
    }
  }

  Future<void> _bid() async {
    HapticFeedback.heavyImpact();
    ArenaSounds.instance.play(ArenaSound.bid, volume: 0.7);
    await ref.read(liveAuctionProvider(widget.auctionId).notifier).placeBid();
  }

  @override
  void dispose() {
    _clock?.cancel();
    _phraseTimer?.cancel();
    _eventTimer?.cancel();
    _errorTimer?.cancel();
    _alarmTimer?.cancel();
    // The alarm belongs to the arena: leaving it stops the noise at once.
    ArenaSounds.instance.stopAll();
    _ambient.dispose();
    _pulse.dispose();
    _shake.dispose();
    _banner.dispose();
    SystemChrome.setEnabledSystemUIMode(SystemUiMode.edgeToEdge);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final live = ref.watch(liveAuctionProvider(widget.auctionId));
    ref.listen(liveAuctionProvider(widget.auctionId), _onStateChange);

    final s = live.snapshot;
    final finalPhase = s?.isFinalPhase ?? false;
    final ended = s != null && !s.isLive;
    final phrases = finalPhase ? t.arenaFinalPhrases : t.arenaLivePhrases;
    final phrase = _eventPhrase ?? phrases[_phraseIndex % phrases.length];

    final hot = finalPhase ? const Color(0xFFFF2D3F) : AppTheme.brand;
    final deep = finalPhase ? const Color(0xFF8E0A18) : const Color(0xFFB8420A);

    return AnnotatedRegion<SystemUiOverlayStyle>(
      value: SystemUiOverlayStyle.light,
      child: Scaffold(
        backgroundColor: const Color(0xFF07080B),
        body: AnimatedBuilder(
          animation: Listenable.merge([_ambient, _pulse, _shake]),
          builder: (context, child) {
            // A decaying side-to-side shake when the final phase lands.
            final shake = _shake.isAnimating
                ? math.sin(_shake.value * math.pi * 10) * 12 * (1 - _shake.value)
                : 0.0;
            return Transform.translate(offset: Offset(shake, 0), child: child);
          },
          child: Stack(
            fit: StackFit.expand,
            children: [
              _Backdrop(
                  ambient: _ambient, pulse: _pulse, hot: hot, deep: deep, finalPhase: finalPhase),
              SafeArea(
                child: s == null
                    ? _Connecting(label: t.arenaConnecting, color: hot)
                    : _Stage(
                        title: widget.title,
                        live: live,
                        snapshot: s,
                        rank: _myRank(s),
                        phrase: phrase,
                        hot: hot,
                        deep: deep,
                        pulse: _pulse,
                        locale: locale,
                        onBid: _bid,
                        onClose: () => Navigator.of(context).pop(),
                      ),
              ),
              _Banner(animation: _banner, title: _bannerTitle, sub: _bannerSub, color: hot),
              if (_error case final message?)
                _ErrorNote(message: message, onClose: () => setState(() => _error = null)),
              if (ended)
                _EndedOverlay(
                  title: t.arenaEnded,
                  action: t.arenaSeeResults,
                  onTap: () => Navigator.of(context).pop(),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

/// What the server refused, across the top of the arena: loud enough to be read at a glance,
/// short enough not to cover the button that caused it.
class _ErrorNote extends StatelessWidget {
  const _ErrorNote({required this.message, required this.onClose});

  final String message;
  final VoidCallback onClose;

  @override
  Widget build(BuildContext context) {
    return SafeArea(
      child: Align(
        alignment: Alignment.topCenter,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(AppTheme.space3, 6, AppTheme.space3, 0),
          child: Material(
            color: const Color(0xFFB3120F),
            borderRadius: BorderRadius.circular(12),
            elevation: 8,
            child: InkWell(
              borderRadius: BorderRadius.circular(12),
              onTap: onClose,
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                child: Row(
                  children: [
                    const Icon(Icons.error_outline_rounded, color: Colors.white, size: 20),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        message,
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: 13.5,
                          height: 1.25,
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                    ),
                    const SizedBox(width: 6),
                    const Icon(Icons.close_rounded, color: Colors.white70, size: 18),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Dark stage, two drifting glows and, in the final phase, a red vignette that beats.
class _Backdrop extends StatelessWidget {
  const _Backdrop({
    required this.ambient,
    required this.pulse,
    required this.hot,
    required this.deep,
    required this.finalPhase,
  });

  final Animation<double> ambient;
  final Animation<double> pulse;
  final Color hot;
  final Color deep;
  final bool finalPhase;

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: Listenable.merge([ambient, pulse]),
      builder: (context, _) {
        final a = ambient.value * 2 * math.pi;
        // A heartbeat: a quick double swell, then rest.
        final beat = math.pow(math.sin(pulse.value * math.pi), 6).toDouble();

        return Stack(
          fit: StackFit.expand,
          children: [
            AnimatedContainer(
              duration: const Duration(milliseconds: 700),
              decoration: BoxDecoration(
                gradient: RadialGradient(
                  center: Alignment(math.sin(a) * 0.6, -0.55 + math.cos(a) * 0.2),
                  radius: 1.1,
                  colors: [hot.withValues(alpha: 0.42 + beat * 0.18), Colors.transparent],
                ),
              ),
            ),
            DecoratedBox(
              decoration: BoxDecoration(
                gradient: RadialGradient(
                  center: Alignment(-math.cos(a * 0.7) * 0.7, 0.75),
                  radius: 1.0,
                  colors: [deep.withValues(alpha: 0.55), Colors.transparent],
                ),
              ),
            ),
            if (finalPhase)
              // The edges of the screen glow red in time with the beat.
              DecoratedBox(
                decoration: BoxDecoration(
                  gradient: RadialGradient(
                    radius: 1.05,
                    colors: [
                      Colors.transparent,
                      Colors.transparent,
                      const Color(0xFFFF0A22).withValues(alpha: 0.25 + beat * 0.45),
                    ],
                    stops: const [0, 0.62, 1],
                  ),
                ),
              ),
          ],
        );
      },
    );
  }
}

class _Stage extends StatelessWidget {
  const _Stage({
    required this.title,
    required this.live,
    required this.snapshot,
    required this.rank,
    required this.phrase,
    required this.hot,
    required this.deep,
    required this.pulse,
    required this.locale,
    required this.onBid,
    required this.onClose,
  });

  final String title;
  final LiveAuctionState live;
  final AuctionSnapshot snapshot;
  final int? rank;
  final String phrase;
  final Color hot;
  final Color deep;
  final Animation<double> pulse;
  final String locale;
  final VoidCallback onBid;
  final VoidCallback onClose;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final s = snapshot;
    final finalPhase = s.isFinalPhase;
    final next = math.min(s.currentPrice + s.bidIncrement, s.targetPrice);

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 20),
      child: Column(
        children: [
          // Top bar: leave, what this is, and whether the connection can be trusted.
          Row(
            children: [
              IconButton(
                onPressed: onClose,
                tooltip: t.arenaLeave,
                icon: const Icon(Icons.close_rounded, color: Colors.white70),
              ),
              Expanded(
                child: Text(
                  title,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  textAlign: TextAlign.center,
                  style: const TextStyle(color: Colors.white70, fontWeight: FontWeight.w600),
                ),
              ),
              Padding(
                padding: const EdgeInsets.all(12),
                child: Icon(
                  live.isConnected ? Icons.wifi_rounded : Icons.wifi_off_rounded,
                  size: 20,
                  color: live.isConnected ? const Color(0xFF3DDC84) : const Color(0xFFFF5A67),
                ),
              ),
            ],
          ),

          _PhaseChip(finalPhase: finalPhase, color: hot, pulse: pulse),
          const SizedBox(height: 14),
          _ArenaClock(remaining: live.remaining, hot: hot, urgent: finalPhase),

          const Spacer(),

          // The price: the biggest thing on the stage, rolling up to each new value.
          Text(t.currentPrice,
              style: TextStyle(color: Colors.white.withValues(alpha: 0.6), fontSize: 14)),
          const SizedBox(height: 4),
          TweenAnimationBuilder<double>(
            tween: Tween(end: s.currentPrice),
            duration: const Duration(milliseconds: 450),
            curve: Curves.easeOutCubic,
            builder: (_, value, _) => FittedBox(
              fit: BoxFit.scaleDown,
              child: Text(
                Fmt.money(value, s.currency, locale: locale),
                style: TextStyle(
                  color: Colors.white,
                  fontSize: 54,
                  fontWeight: FontWeight.w900,
                  fontFeatures: AppTheme.numeric,
                  shadows: [Shadow(color: hot.withValues(alpha: 0.9), blurRadius: 28)],
                ),
              ),
            ),
          ),
          const SizedBox(height: 12),
          _GlowBar(progress: s.progress, color: hot),
          const SizedBox(height: 14),
          _RankChip(rank: rank, t: t),
          const SizedBox(height: 10),

          // Who else is in the room. Without it the arena was a price and a button: nobody could
          // see the people they were bidding against, or what it would take to pass them.
          _LiveBoard(
            entries: s.leaderboard,
            participantCount: s.participantCount,
            currency: s.currency,
            locale: locale,
            hot: hot,
          ),

          const Spacer(),

          // The line that keeps the pace up. Slides in fresh every time it changes.
          SizedBox(
            height: 58,
            child: AnimatedSwitcher(
              duration: const Duration(milliseconds: 380),
              transitionBuilder: (child, animation) => FadeTransition(
                opacity: animation,
                child: SlideTransition(
                  position: Tween(begin: const Offset(0, 0.4), end: Offset.zero).animate(animation),
                  child: ScaleTransition(
                    scale: Tween(begin: 0.9, end: 1.0).animate(animation),
                    child: child,
                  ),
                ),
              ),
              child: Text(
                phrase,
                key: ValueKey(phrase),
                textAlign: TextAlign.center,
                maxLines: 2,
                style: TextStyle(
                  color: Colors.white,
                  fontSize: finalPhase ? 21 : 19,
                  fontWeight: FontWeight.w800,
                  height: 1.3,
                  shadows: [Shadow(color: hot, blurRadius: 18)],
                ),
              ),
            ),
          ),
          const SizedBox(height: 14),

          _BidButton(
            enabled: live.canBid,
            busy: live.isBidding,
            label: t.arenaBid,
            increment: '+${Fmt.money(s.bidIncrement, s.currency, locale: locale)}',
            hot: hot,
            deep: deep,
            pulse: pulse,
            onTap: onBid,
          ),
          const SizedBox(height: 14),
          Text(
            !live.isConnected
                ? t.arenaConnecting
                : s.targetReached
                    ? t.arenaTargetReached
                    : live.waitingForOtherBidder
                        ? t.waitForOtherBidder
                        : t.arenaNextPrice(Fmt.money(next, s.currency, locale: locale)),
            textAlign: TextAlign.center,
            style: TextStyle(color: Colors.white.withValues(alpha: 0.7), fontSize: 13),
          ),
        ],
      ),
    );
  }
}

class _PhaseChip extends StatelessWidget {
  const _PhaseChip({required this.finalPhase, required this.color, required this.pulse});

  final bool finalPhase;
  final Color color;
  final Animation<double> pulse;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    return AnimatedBuilder(
      animation: pulse,
      builder: (_, _) {
        final glow = math.sin(pulse.value * math.pi);
        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 7),
          decoration: BoxDecoration(
            color: color.withValues(alpha: 0.18 + glow * 0.12),
            borderRadius: BorderRadius.circular(AppTheme.radiusPill),
            border: Border.all(color: color.withValues(alpha: 0.8)),
            boxShadow: [BoxShadow(color: color.withValues(alpha: 0.35 * glow), blurRadius: 18)],
          ),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(finalPhase ? Icons.local_fire_department_rounded : Icons.circle,
                  size: finalPhase ? 18 : 10, color: Colors.white),
              const SizedBox(width: 8),
              Text(
                finalPhase ? t.arenaFinalPhase : t.arenaLive,
                style: const TextStyle(
                  color: Colors.white,
                  fontWeight: FontWeight.w800,
                  letterSpacing: 0.3,
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}

/// The countdown, as glowing blocks. Left to right in any language: a clock is not a sentence.
class _ArenaClock extends StatelessWidget {
  const _ArenaClock({required this.remaining, required this.hot, required this.urgent});

  final Duration remaining;
  final Color hot;
  final bool urgent;

  @override
  Widget build(BuildContext context) {
    final d = remaining.isNegative ? Duration.zero : remaining;
    final red = urgent || d.inSeconds <= 30;
    final color = red ? const Color(0xFFFF4757) : Colors.white;
    final parts = [
      if (d.inHours > 0) d.inHours.toString().padLeft(2, '0'),
      d.inMinutes.remainder(60).toString().padLeft(2, '0'),
      d.inSeconds.remainder(60).toString().padLeft(2, '0'),
    ];

    return Directionality(
      textDirection: TextDirection.ltr,
      child: Row(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          for (var i = 0; i < parts.length; i++) ...[
            if (i > 0)
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 6),
                child:
                    Text(':', style: TextStyle(color: color.withValues(alpha: 0.5), fontSize: 30)),
              ),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
              decoration: BoxDecoration(
                color: Colors.white.withValues(alpha: 0.06),
                borderRadius: BorderRadius.circular(AppTheme.radiusMd),
                border: Border.all(color: (red ? color : hot).withValues(alpha: 0.5)),
                boxShadow: [
                  BoxShadow(color: (red ? color : hot).withValues(alpha: 0.25), blurRadius: 16),
                ],
              ),
              child: Text(
                parts[i],
                style: TextStyle(
                  color: color,
                  fontSize: 34,
                  fontWeight: FontWeight.w900,
                  fontFeatures: AppTheme.numeric,
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _GlowBar extends StatelessWidget {
  const _GlowBar({required this.progress, required this.color});

  final double progress;
  final Color color;

  @override
  Widget build(BuildContext context) {
    final value = progress.clamp(0.0, 1.0);

    return Container(
      height: 10,
      decoration: BoxDecoration(
        color: Colors.white.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(AppTheme.radiusPill),
      ),
      alignment: AlignmentDirectional.centerStart,
      child: LayoutBuilder(
        builder: (_, c) => AnimatedContainer(
          duration: const Duration(milliseconds: 450),
          curve: Curves.easeOutCubic,
          width: c.maxWidth * value,
          decoration: BoxDecoration(
            gradient: LinearGradient(colors: [color.withValues(alpha: 0.7), color]),
            borderRadius: BorderRadius.circular(AppTheme.radiusPill),
            boxShadow: [BoxShadow(color: color.withValues(alpha: 0.7), blurRadius: 14)],
          ),
        ),
      ),
    );
  }
}

/// The top of the board, live, inside the arena.
///
/// Five rows at most: enough to see the four who are winning and the one about to take a place,
/// and no more than fits above the bidding button on a small phone.
class _LiveBoard extends StatelessWidget {
  const _LiveBoard({
    required this.entries,
    required this.participantCount,
    required this.currency,
    required this.locale,
    required this.hot,
  });

  final List<LeaderboardEntry> entries;

  /// Everyone in the auction. The board itself is capped, so it cannot be counted from [entries].
  final int participantCount;
  final String currency;
  final String locale;
  final Color hot;

  static const _shownRows = 5;
  static const _winningPlaces = 4;
  static const _win = Color(0xFF3DDC84);

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    if (entries.isEmpty) return const SizedBox.shrink();

    final shown = entries.take(_shownRows).toList();
    // My own place stays on the board even when it is below the rows shown.
    final me = entries.where((e) => e.isMe).firstOrNull;
    final meHidden = me != null && !shown.contains(me);

    final rows = <Widget>[];
    for (var i = 0; i < shown.length; i++) {
      final e = shown[i];
      if (i > 0 && shown[i - 1].rank <= _winningPlaces && e.rank > _winningPlaces) {
        rows.add(_winningLine());
      }
      rows.add(_row(e, t));
    }
    if (meHidden) {
      rows.add(Padding(
        padding: const EdgeInsets.symmetric(vertical: 1),
        child: Icon(Icons.more_horiz_rounded, size: 14, color: Colors.white.withValues(alpha: 0.4)),
      ));
      rows.add(_row(me, t));
    }

    return Container(
      padding: const EdgeInsets.fromLTRB(10, 8, 10, 8),
      decoration: BoxDecoration(
        color: Colors.white.withValues(alpha: 0.06),
        borderRadius: BorderRadius.circular(14),
        border: Border.all(color: Colors.white.withValues(alpha: 0.10)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Row(
            children: [
              Icon(Icons.leaderboard_rounded, size: 14, color: hot),
              const SizedBox(width: 6),
              Text(
                t.leaderboard,
                style: TextStyle(
                  color: Colors.white.withValues(alpha: 0.75),
                  fontSize: 11.5,
                  fontWeight: FontWeight.w700,
                ),
              ),
              const Spacer(),
              Icon(Icons.groups_rounded, size: 13, color: Colors.white.withValues(alpha: 0.55)),
              const SizedBox(width: 4),
              Text(
                t.participantsCount(participantCount),
                style: TextStyle(
                  color: Colors.white.withValues(alpha: 0.55),
                  fontSize: 10.5,
                  fontWeight: FontWeight.w600,
                ),
              ),
            ],
          ),
          const SizedBox(height: 6),
          ...rows,
        ],
      ),
    );
  }

  /// Everyone above this line would win a prize if the auction ended now.
  Widget _winningLine() => Padding(
        padding: const EdgeInsets.symmetric(vertical: 3),
        child: Row(
          children: [
            Expanded(child: Container(height: 1, color: _win.withValues(alpha: 0.35))),
            const SizedBox(width: 6),
            Icon(Icons.emoji_events_rounded, size: 11, color: _win.withValues(alpha: 0.8)),
            const SizedBox(width: 6),
            Expanded(child: Container(height: 1, color: _win.withValues(alpha: 0.35))),
          ],
        ),
      );

  Widget _row(LeaderboardEntry e, AppStrings t) {
    // The four who would win if it ended now are marked; my own row is marked whatever my place.
    final winning = e.rank <= _winningPlaces;
    final colour = e.isMe ? hot : (winning ? _win : Colors.white70);
    final bid = e.bidCount > 0;

    return Container(
      margin: const EdgeInsets.symmetric(vertical: 1),
      padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 3),
      decoration: e.isMe
          ? BoxDecoration(
              color: hot.withValues(alpha: 0.14),
              borderRadius: BorderRadius.circular(8),
            )
          : null,
      child: Row(
        children: [
          SizedBox(
            width: 22,
            child: Text(
              '${e.rank}',
              textDirection: TextDirection.ltr,
              textAlign: TextAlign.center,
              style: TextStyle(
                color: colour,
                fontSize: 12.5,
                fontWeight: FontWeight.w900,
                fontFeatures: AppTheme.numeric,
              ),
            ),
          ),
          const SizedBox(width: 6),
          Expanded(
            child: Text(
              e.isMe ? t.you : Fmt.name(e.displayName),
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                color: e.isMe ? Colors.white : Colors.white.withValues(alpha: 0.82),
                fontSize: 12,
                fontWeight: e.isMe ? FontWeight.w800 : FontWeight.w600,
              ),
            ),
          ),
          if (bid) ...[
            Text(
              t.bidsCount(e.bidCount),
              style: TextStyle(
                color: Colors.white.withValues(alpha: 0.45),
                fontSize: 10.5,
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(width: 10),
          ],
          Text(
            // Somebody who has not bid yet has no price to show, so the row says so.
            bid ? Fmt.money(e.highestPrice, currency, locale: locale) : t.noBidYet,
            style: TextStyle(
              color: bid ? colour : Colors.white38,
              fontSize: 12,
              fontWeight: FontWeight.w800,
              fontFeatures: AppTheme.numeric,
            ),
          ),
        ],
      ),
    );
  }
}

class _RankChip extends StatelessWidget {
  const _RankChip({required this.rank, required this.t});

  final int? rank;
  final AppStrings t;

  @override
  Widget build(BuildContext context) {
    final winning = rank != null && rank! <= 4;
    final color = rank == null
        ? Colors.white54
        : winning
            ? const Color(0xFF3DDC84)
            : const Color(0xFFFF5A67);

    return AnimatedContainer(
      duration: const Duration(milliseconds: 300),
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(AppTheme.radiusPill),
        border: Border.all(color: color.withValues(alpha: 0.6)),
      ),
      child: Text(
        rank == null
            ? t.arenaNoRank
            : '${t.arenaYourRank(rank!)} · ${winning ? t.arenaInWinners : t.arenaOutOfWinners}',
        style: TextStyle(color: color, fontWeight: FontWeight.w800),
      ),
    );
  }
}

/// The big round button, ringed by pulses of light. It shrinks under the finger and springs back.
class _BidButton extends StatefulWidget {
  const _BidButton({
    required this.enabled,
    required this.busy,
    required this.label,
    required this.increment,
    required this.hot,
    required this.deep,
    required this.pulse,
    required this.onTap,
  });

  final bool enabled;
  final bool busy;
  final String label;
  final String increment;
  final Color hot;
  final Color deep;
  final Animation<double> pulse;
  final VoidCallback onTap;

  @override
  State<_BidButton> createState() => _BidButtonState();
}

class _BidButtonState extends State<_BidButton> {
  bool _down = false;

  @override
  Widget build(BuildContext context) {
    const size = 176.0;
    final on = widget.enabled;
    final hot = on ? widget.hot : const Color(0xFF4A4F57);
    final deep = on ? widget.deep : const Color(0xFF2A2E34);

    return AnimatedBuilder(
      animation: widget.pulse,
      builder: (context, _) {
        final p = widget.pulse.value;

        return SizedBox(
          width: size + 90,
          height: size + 90,
          child: Stack(
            alignment: Alignment.center,
            children: [
              // Three rings of light moving outward, one after another.
              if (on)
                for (var i = 0; i < 3; i++)
                  Builder(builder: (_) {
                    final v = (p + i / 3) % 1.0;
                    return Container(
                      width: size + v * 86,
                      height: size + v * 86,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        border: Border.all(
                          color: hot.withValues(alpha: (1 - v) * 0.55),
                          width: 3,
                        ),
                      ),
                    );
                  }),
              GestureDetector(
                onTapDown: on ? (_) => setState(() => _down = true) : null,
                onTapCancel: () => setState(() => _down = false),
                onTapUp: on
                    ? (_) {
                        setState(() => _down = false);
                        widget.onTap();
                      }
                    : null,
                child: AnimatedScale(
                  scale: _down ? 0.9 : 1.0,
                  duration: const Duration(milliseconds: 110),
                  child: Container(
                    width: size,
                    height: size,
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      gradient: RadialGradient(
                        center: const Alignment(-0.3, -0.35),
                        colors: [Color.lerp(hot, Colors.white, 0.25)!, hot, deep],
                        stops: const [0, 0.55, 1],
                      ),
                      boxShadow: [
                        BoxShadow(
                          color: hot.withValues(alpha: on ? 0.55 + 0.3 * math.sin(p * math.pi) : 0),
                          blurRadius: 46,
                          spreadRadius: 4,
                        ),
                      ],
                      border: Border.all(color: Colors.white.withValues(alpha: 0.35), width: 2),
                    ),
                    child: widget.busy
                        ? const Center(
                            child: SizedBox(
                              width: 42,
                              height: 42,
                              child: CircularProgressIndicator(color: Colors.white, strokeWidth: 4),
                            ),
                          )
                        : Column(
                            mainAxisAlignment: MainAxisAlignment.center,
                            children: [
                              const Icon(Icons.gavel_rounded, color: Colors.white, size: 34),
                              Text(
                                widget.label,
                                style: const TextStyle(
                                  color: Colors.white,
                                  fontSize: 38,
                                  fontWeight: FontWeight.w900,
                                  height: 1.1,
                                ),
                              ),
                              Text(
                                widget.increment,
                                style: TextStyle(
                                  color: Colors.white.withValues(alpha: 0.85),
                                  fontWeight: FontWeight.w700,
                                ),
                              ),
                            ],
                          ),
                  ),
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}

/// A title that zooms in across the stage, holds, and fades: "the auction is live", "final phase".
class _Banner extends StatelessWidget {
  const _Banner(
      {required this.animation, required this.title, required this.sub, required this.color});

  final Animation<double> animation;
  final String? title;
  final String? sub;
  final Color color;

  @override
  Widget build(BuildContext context) {
    if (title == null) return const SizedBox.shrink();

    return IgnorePointer(
      child: AnimatedBuilder(
        animation: animation,
        builder: (_, _) {
          final v = animation.value;
          if (v == 0 || v == 1) return const SizedBox.shrink();

          // In fast, hold, out slow.
          final opacity = v < 0.15 ? v / 0.15 : (v > 0.7 ? (1 - v) / 0.3 : 1.0);
          final scale =
              v < 0.15 ? 1.6 - 0.6 * Curves.easeOut.transform(v / 0.15) : 1.0 + (v - 0.15) * 0.08;

          return ColoredBox(
            color: Colors.black.withValues(alpha: 0.45 * opacity),
            child: Center(
              child: Opacity(
                opacity: opacity.clamp(0.0, 1.0),
                child: Transform.scale(
                  scale: scale,
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(
                        title!,
                        textAlign: TextAlign.center,
                        style: TextStyle(
                          color: Colors.white,
                          fontSize: 46,
                          fontWeight: FontWeight.w900,
                          shadows: [
                            Shadow(color: color, blurRadius: 30),
                            Shadow(color: color, blurRadius: 60),
                          ],
                        ),
                      ),
                      if (sub != null)
                        Padding(
                          padding: const EdgeInsets.only(top: 8),
                          child: Text(
                            sub!,
                            textAlign: TextAlign.center,
                            style: const TextStyle(
                              color: Colors.white,
                              fontSize: 18,
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
              ),
            ),
          );
        },
      ),
    );
  }
}

class _Connecting extends StatelessWidget {
  const _Connecting({required this.label, required this.color});

  final String label;
  final Color color;

  @override
  Widget build(BuildContext context) => Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            CircularProgressIndicator(color: color),
            const SizedBox(height: 16),
            Text(label, style: const TextStyle(color: Colors.white70)),
          ],
        ),
      );
}

class _EndedOverlay extends StatelessWidget {
  const _EndedOverlay({required this.title, required this.action, required this.onTap});

  final String title;
  final String action;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => ColoredBox(
        color: Colors.black.withValues(alpha: 0.78),
        child: Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(Icons.flag_rounded, color: Colors.white, size: 56),
              const SizedBox(height: 12),
              Text(
                title,
                style:
                    const TextStyle(color: Colors.white, fontSize: 32, fontWeight: FontWeight.w900),
              ),
              const SizedBox(height: 22),
              FilledButton(
                onPressed: onTap,
                style: FilledButton.styleFrom(
                  backgroundColor: AppTheme.brand,
                  foregroundColor: Colors.white,
                  minimumSize: const Size(220, 52),
                ),
                child: Text(action, style: const TextStyle(fontWeight: FontWeight.w800)),
              ),
            ],
          ),
        ),
      );
}
