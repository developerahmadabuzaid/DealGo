import 'package:flutter/material.dart';

import '../../../core/localization/app_strings.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/art_fallback.dart';
import '../data/auction_models.dart';

/// The devices an auction awards, in a row under its card: a small picture each, its place in
/// the corner and its name beneath, so what is at stake is seen without opening the auction.
class AuctionDevicesStrip extends StatelessWidget {
  const AuctionDevicesStrip({super.key, required this.prizes});

  final List<AuctionListPrize> prizes;

  static const _medals = {
    1: Color(0xFFE9B10A),
    2: Color(0xFFAEB5C0),
    3: Color(0xFFCD8A4F),
  };

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final shown = prizes.take(4).toList();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(t.prizes, style: theme.textTheme.labelSmall),
        const SizedBox(height: AppTheme.space2),
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            for (var i = 0; i < 4; i++) ...[
              if (i > 0) const SizedBox(width: AppTheme.space2),
              Expanded(
                child: i < shown.length
                    ? _Tile(prize: shown[i])
                    : const SizedBox.shrink(),
              ),
            ],
          ],
        ),
      ],
    );
  }
}

class _Tile extends StatelessWidget {
  const _Tile({required this.prize});

  final AuctionListPrize prize;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final medal = AuctionDevicesStrip._medals[prize.rank] ?? AppTheme.gold;

    return Column(
      children: [
        AspectRatio(
          aspectRatio: 1,
          child: ClipRRect(
            borderRadius: BorderRadius.circular(AppTheme.radiusSm),
            child: ColoredBox(
              color: const Color(0xFF0B0C0E),
              child: Stack(
                fit: StackFit.expand,
                children: [
                  ArtOrPicture(
                    imageUrl: prize.imageUrl,
                    seed: 'device-${prize.rank}-${prize.name}',
                  ),
                  PositionedDirectional(
                    top: 3,
                    start: 3,
                    child: Container(
                      width: 18,
                      height: 18,
                      alignment: Alignment.center,
                      decoration: BoxDecoration(
                        color: medal,
                        shape: BoxShape.circle,
                      ),
                      child: Text(
                        '${prize.rank}',
                        textDirection: TextDirection.ltr,
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: 10,
                          fontWeight: FontWeight.w900,
                          fontFeatures: AppTheme.numeric,
                        ),
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
        const SizedBox(height: 4),
        Text(
          prize.name,
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
          textAlign: TextAlign.center,
          style: theme.textTheme.labelSmall?.copyWith(height: 1.2),
        ),
      ],
    );
  }
}
