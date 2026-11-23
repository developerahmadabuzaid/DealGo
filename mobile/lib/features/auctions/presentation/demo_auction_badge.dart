import 'package:flutter/material.dart';

import '../../../core/localization/app_strings.dart';
import '../../../core/theme/app_theme.dart';

/// The "مزاد تجريبي" chip a demo auction carries wherever it is shown: its card, its page and
/// the live screen. Demo auctions are free and open to everyone, so the label has to be plain
/// enough that nobody mistakes one for a paid auction with a real prize.
class DemoAuctionBadge extends StatelessWidget {
  const DemoAuctionBadge({super.key, this.dense = false});

  /// Smaller, for list cards.
  final bool dense;

  static const Color color = Color(0xFF7C4DFF);

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Container(
      padding: EdgeInsets.symmetric(
        horizontal: dense ? 8 : 10,
        vertical: dense ? 2 : 4,
      ),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.14),
        borderRadius: BorderRadius.circular(AppTheme.radiusMd),
        border: Border.all(color: color.withValues(alpha: 0.45)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.science_outlined, size: dense ? 12 : 14, color: color),
          const SizedBox(width: 4),
          Text(
            t.demoAuction,
            style: (dense ? theme.textTheme.labelSmall : theme.textTheme.labelMedium)
                ?.copyWith(color: color, fontWeight: FontWeight.w800),
          ),
        ],
      ),
    );
  }
}
