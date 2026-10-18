import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:go_router/go_router.dart';

import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/theme/app_theme.dart';
import '../data/auction_models.dart';

/// The statuses an auction is in once bidding has closed and someone has won it.
const _finishedStatuses = {'Finished', 'WinnersDeclared', 'PrizeClaim', 'Completed'};

/// The places that take a prize.
const _prizePlaces = 4;

/// Tells the user how a running auction ended, the moment it ends: a congratulation for a prize
/// winner, and for everyone else who took first place and a word of luck for the next one.
///
/// Called with each change of the live state. It only acts on the change from running to finished,
/// so an auction already over when the screen opened does not announce itself again.
Future<void> announceAuctionEnd(
  BuildContext context, {
  required AuctionSnapshot? before,
  required AuctionSnapshot? now,
}) async {
  if (before == null || now == null) return;
  if (!before.isLive || !_finishedStatuses.contains(now.status)) return;

  final bidders = now.leaderboard.where((e) => e.bidCount > 0).toList()
    ..sort((a, b) => a.rank.compareTo(b.rank));
  final first = bidders.firstOrNull;

  final mine = now.leaderboard.where((e) => e.isMe).firstOrNull;
  final myRank = mine != null ? (mine.bidCount > 0 ? mine.rank : null) : now.myRank;

  final result = myRank != null && myRank <= _prizePlaces && first != null
      ? _Outcome.won(myRank)
      : _Outcome.lost(first?.displayName);

  HapticFeedback.heavyImpact();
  await showGeneralDialog<void>(
    context: context,
    barrierDismissible: false,
    barrierColor: Colors.black.withValues(alpha: 0.55),
    transitionDuration: const Duration(milliseconds: 320),
    pageBuilder: (_, _, _) => _AuctionEndDialog(
      outcome: result,
      price: Fmt.money(now.currentPrice, now.currency,
          locale: Localizations.localeOf(context).languageCode),
    ),
    transitionBuilder: (_, animation, _, child) {
      final curved = CurvedAnimation(parent: animation, curve: Curves.easeOutBack);
      return FadeTransition(
        opacity: animation,
        child: ScaleTransition(scale: Tween(begin: 0.85, end: 1.0).animate(curved), child: child),
      );
    },
  );
}

class _Outcome {
  const _Outcome._({required this.won, this.rank, this.winnerName});

  factory _Outcome.won(int rank) => _Outcome._(won: true, rank: rank);
  factory _Outcome.lost(String? winnerName) => _Outcome._(won: false, winnerName: winnerName);

  final bool won;
  final int? rank;
  final String? winnerName;
}

class _AuctionEndDialog extends StatelessWidget {
  const _AuctionEndDialog({required this.outcome, required this.price});

  final _Outcome outcome;
  final String price;

  static const _gold = Color(0xFFE9B10A);

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final won = outcome.won;
    final accent = won ? _gold : AppTheme.brand;

    final title = won
        ? (outcome.rank == 1 ? t.youWonAuction : t.youWonPlace(outcome.rank!))
        : t.auctionFinished;
    final subtitle = won
        ? t.congratsWinner
        : outcome.winnerName == null
            ? null
            : t.congratsTo(Fmt.name(outcome.winnerName!));
    final body = won ? t.wonAuctionBody(price) : t.betterLuckNextTime;

    return Center(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: AppTheme.space6),
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 380),
          child: Material(
            color: theme.colorScheme.surface,
            borderRadius: BorderRadius.circular(AppTheme.radiusXl),
            clipBehavior: Clip.antiAlias,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(
                  AppTheme.space6, AppTheme.space8, AppTheme.space6, AppTheme.space6),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  _Badge(won: won, color: accent),
                  const SizedBox(height: AppTheme.space5),
                  Text(
                    title,
                    textAlign: TextAlign.center,
                    style: theme.textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w900),
                  ),
                  if (subtitle != null) ...[
                    const SizedBox(height: AppTheme.space1),
                    Text(
                      subtitle,
                      textAlign: TextAlign.center,
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w800,
                        color: won ? AppTheme.warning : null,
                      ),
                    ),
                  ],
                  const SizedBox(height: AppTheme.space4),
                  Text(
                    body,
                    textAlign: TextAlign.center,
                    style: theme.textTheme.bodyLarge?.copyWith(height: 1.5),
                  ),
                  const SizedBox(height: AppTheme.space6),
                  _GradientButton(
                    label: t.confirm,
                    colors: won
                        ? const [Color(0xFFF2C23A), Color(0xFFC98A12)]
                        : const [AppTheme.brandSoft, AppTheme.accent],
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                  if (won) ...[
                    const SizedBox(height: AppTheme.space2),
                    TextButton.icon(
                      onPressed: () {
                        Navigator.of(context).pop();
                        context.push('/prizes');
                      },
                      icon: const Icon(Icons.card_giftcard_rounded),
                      label: Text(t.myPrizes),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// The mark at the top: an "i" for the end of the auction, a ringed trophy for a win.
class _Badge extends StatelessWidget {
  const _Badge({required this.won, required this.color});

  final bool won;
  final Color color;

  @override
  Widget build(BuildContext context) {
    // The end of an auction is news, not a warning: the plain "i" in its ring.
    if (!won) return Icon(Icons.info_outline_rounded, size: 92, color: color);

    final ring = Container(
      width: 84,
      height: 84,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        color: color.withValues(alpha: 0.1),
        border: Border.all(color: color, width: 5),
      ),
      child: Icon(Icons.emoji_events_rounded, size: 46, color: color),
    );

    if (MediaQuery.disableAnimationsOf(context)) return ring;

    // A winner's trophy settles in with one small bounce.
    return TweenAnimationBuilder<double>(
      tween: Tween(begin: 0.4, end: 1),
      duration: const Duration(milliseconds: 700),
      curve: Curves.elasticOut,
      builder: (_, scale, child) => Transform.scale(scale: scale, child: child),
      child: ring,
    );
  }
}

class _GradientButton extends StatelessWidget {
  const _GradientButton({required this.label, required this.colors, required this.onPressed});

  final String label;
  final List<Color> colors;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    return DecoratedBox(
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(AppTheme.radiusLg),
        gradient: LinearGradient(colors: colors),
        boxShadow: [
          BoxShadow(color: colors.last.withValues(alpha: 0.35), blurRadius: 12, offset: const Offset(0, 5)),
        ],
      ),
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          onTap: onPressed,
          borderRadius: BorderRadius.circular(AppTheme.radiusLg),
          child: SizedBox(
            height: 52,
            width: double.infinity,
            child: Center(
              child: Text(
                label,
                style: const TextStyle(
                    color: Colors.white, fontSize: 16, fontWeight: FontWeight.w800),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
