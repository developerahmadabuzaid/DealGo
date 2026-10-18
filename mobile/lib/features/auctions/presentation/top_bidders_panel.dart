import 'package:flutter/material.dart';

import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';
import '../data/auction_models.dart';

/// The leading bidders of an auction, as the auction page shows them.
///
/// Only people who have actually bid are listed: someone who joined and has not moved yet has no
/// place to show, and a board of "no bid yet" rows reads as an empty room. The first few are shown,
/// with the rest one tap away, and the user's own row is always visible — appended under the list
/// when their place is further down — because "where am I" is the question the panel answers.
class TopBiddersPanel extends StatefulWidget {
  const TopBiddersPanel({
    super.key,
    required this.entries,
    required this.currency,
    required this.participantCount,
    this.dense = false,
  });

  final List<LeaderboardEntry> entries;
  final String currency;

  /// Everyone in the auction, including those who have not bid.
  final int participantCount;

  /// Tighter rows and no subtitle, for the live bidding screen, where the list shares a phone
  /// screen with the price panel and the bid bar.
  final bool dense;

  /// How many rows are shown before "show all".
  static const collapsedCount = 5;

  /// The places that win a prize if the auction ended now.
  static const winningPlaces = 4;

  @override
  State<TopBiddersPanel> createState() => _TopBiddersPanelState();
}

class _TopBiddersPanelState extends State<TopBiddersPanel> {
  bool _expanded = false;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    final bidders = widget.entries.where((e) => e.bidCount > 0).toList()
      ..sort((a, b) => a.rank.compareTo(b.rank));

    final shown = _expanded
        ? bidders
        : bidders.take(TopBiddersPanel.collapsedCount).toList();
    final me = bidders.where((e) => e.isMe).firstOrNull;
    final meHidden = me != null && !shown.contains(me);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    t.topBidders,
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  if (!widget.dense) ...[
                    const SizedBox(height: 2),
                    Text(
                      t.topBiddersSubtitle,
                      style: theme.textTheme.bodySmall,
                    ),
                  ],
                ],
              ),
            ),
            StatusPill(
              t.participantsCount(widget.participantCount),
              color: theme.colorScheme.onSurfaceVariant,
              icon: Icons.groups_rounded,
            ),
          ],
        ),
        SizedBox(height: widget.dense ? AppTheme.space2 : AppTheme.space3),
        SurfaceCard(
          padding: EdgeInsets.zero,
          child: bidders.isEmpty
              ? Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: AppTheme.space4,
                    vertical: AppTheme.space6,
                  ),
                  child: Column(
                    children: [
                      Icon(
                        Icons.gavel_rounded,
                        size: 28,
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                      const SizedBox(height: AppTheme.space2),
                      Text(
                        t.noBidsYet,
                        textAlign: TextAlign.center,
                        style: theme.textTheme.bodyMedium,
                      ),
                    ],
                  ),
                )
              : Column(
                  children: [
                    for (var i = 0; i < shown.length; i++) ...[
                      if (i > 0)
                        shown[i - 1].rank <= TopBiddersPanel.winningPlaces &&
                                shown[i].rank > TopBiddersPanel.winningPlaces
                            ? _WinningLine(label: t.winningZoneEnds)
                            : const Divider(
                                height: 1,
                                indent: AppTheme.space4,
                                endIndent: AppTheme.space4,
                              ),
                      _BidderRow(
                        entry: shown[i],
                        currency: widget.currency,
                        dense: widget.dense,
                      ),
                    ],
                    if (meHidden) ...[
                      const _Gap(),
                      _BidderRow(
                        entry: me,
                        currency: widget.currency,
                        dense: widget.dense,
                      ),
                    ],
                    if (bidders.length > TopBiddersPanel.collapsedCount) ...[
                      const Divider(height: 1),
                      TextButton.icon(
                        onPressed: () => setState(() => _expanded = !_expanded),
                        icon: Icon(
                          _expanded
                              ? Icons.keyboard_arrow_up_rounded
                              : Icons.keyboard_arrow_down_rounded,
                        ),
                        label: Text(
                          _expanded
                              ? t.showLess
                              : t.showAllBidders(bidders.length),
                        ),
                        style: TextButton.styleFrom(
                          minimumSize: const Size.fromHeight(
                            AppTheme.tapTarget,
                          ),
                        ),
                      ),
                    ],
                  ],
                ),
        ),
      ],
    );
  }
}

class _BidderRow extends StatelessWidget {
  const _BidderRow({
    required this.entry,
    required this.currency,
    this.dense = false,
  });

  final LeaderboardEntry entry;
  final String currency;
  final bool dense;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final winning = entry.rank <= TopBiddersPanel.winningPlaces;

    final row = Padding(
      padding: EdgeInsets.symmetric(
        horizontal: dense ? AppTheme.space3 : AppTheme.space4,
        vertical: dense ? AppTheme.space2 : AppTheme.space3,
      ),
      child: Row(
        children: [
          _RankBadge(rank: entry.rank),
          const SizedBox(width: AppTheme.space3),
          CircleAvatar(
            radius: dense ? 14 : 18,
            backgroundColor: entry.isMe
                ? AppTheme.accentTint
                : theme.colorScheme.surfaceContainerHighest,
            child: Text(
              _initial(entry.displayName),
              style: TextStyle(
                fontWeight: FontWeight.w800,
                color: entry.isMe
                    ? AppTheme.accentText
                    : theme.colorScheme.onSurfaceVariant,
              ),
            ),
          ),
          const SizedBox(width: AppTheme.space3),
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
                        style: theme.textTheme.bodyLarge?.copyWith(
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                    ),
                    if (entry.isMe) ...[
                      const SizedBox(width: AppTheme.space2),
                      _Tag(label: t.you, color: AppTheme.accent),
                    ],
                  ],
                ),
                const SizedBox(height: 2),
                Text(
                  t.bidsCount(entry.bidCount),
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

    if (!entry.isMe && !winning) return row;

    // The winning places carry a faint gold wash; the user's own row a faint brand one.
    return ColoredBox(
      color: entry.isMe
          ? AppTheme.accentSoft
          : AppTheme.gold.withValues(alpha: 0.05),
      child: row,
    );
  }

  static String _initial(String name) {
    final trimmed = name.trim();
    return trimmed.isEmpty ? '؟' : trimmed.characters.first.toUpperCase();
  }
}

/// The place, as a medal for the first three and a plain number after that.
class _RankBadge extends StatelessWidget {
  const _RankBadge({required this.rank});

  final int rank;

  static const _medals = {
    1: (Color(0xFFE9B10A), Color(0xFF3A2A00)),
    2: (Color(0xFFB8BEC8), Color(0xFF22262C)),
    3: (Color(0xFFCD8A4F), Color(0xFF2E1A08)),
  };

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final medal = _medals[rank];
    final winning = rank <= TopBiddersPanel.winningPlaces;

    final background =
        medal?.$1 ??
        (winning
            ? AppTheme.gold.withValues(alpha: 0.18)
            : theme.colorScheme.surfaceContainerHighest);
    final foreground =
        medal?.$2 ??
        (winning ? AppTheme.onGold : theme.colorScheme.onSurfaceVariant);

    return Container(
      width: 30,
      height: 30,
      alignment: Alignment.center,
      decoration: BoxDecoration(color: background, shape: BoxShape.circle),
      child: medal != null && rank == 1
          ? Icon(Icons.emoji_events_rounded, size: 17, color: foreground)
          : Text(
              '$rank',
              textDirection: TextDirection.ltr,
              style: TextStyle(
                color: foreground,
                fontWeight: FontWeight.w900,
                fontSize: 13,
                fontFeatures: AppTheme.numeric,
              ),
            ),
    );
  }
}

class _Tag extends StatelessWidget {
  const _Tag({required this.label, required this.color});

  final String label;
  final Color color;

  @override
  Widget build(BuildContext context) => Container(
    padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 1),
    decoration: BoxDecoration(
      color: color.withValues(alpha: 0.12),
      borderRadius: BorderRadius.circular(AppTheme.radiusPill),
    ),
    child: Text(
      label,
      style: TextStyle(color: color, fontSize: 11, fontWeight: FontWeight.w800),
    ),
  );
}

/// Where the prize places end: everyone above this line would win if the auction ended now.
class _WinningLine extends StatelessWidget {
  const _WinningLine({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    final color = AppTheme.gold;

    return Padding(
      padding: const EdgeInsets.symmetric(
        horizontal: AppTheme.space4,
        vertical: AppTheme.space1,
      ),
      child: Row(
        children: [
          Expanded(
            child: Divider(color: color.withValues(alpha: 0.5), thickness: 1),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: AppTheme.space2),
            child: Text(
              label,
              style: TextStyle(
                color: color,
                fontSize: 11,
                fontWeight: FontWeight.w800,
              ),
            ),
          ),
          Expanded(
            child: Divider(color: color.withValues(alpha: 0.5), thickness: 1),
          ),
        ],
      ),
    );
  }
}

/// Separates the user's own row when it sits below the rows shown.
class _Gap extends StatelessWidget {
  const _Gap();

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 2),
    child: Icon(
      Icons.more_horiz_rounded,
      size: 18,
      color: Theme.of(context).colorScheme.onSurfaceVariant,
    ),
  );
}
