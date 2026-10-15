import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../core/config/app_config.dart';
import '../core/format/formatters.dart';
import '../core/localization/app_strings.dart';
import '../core/theme/app_theme.dart';

/// Small building blocks shared across screens.
///
/// Every screen draws its surfaces, badges and figures from here, so the visual language stays
/// consistent and a change lands everywhere at once.

class SectionHeader extends StatelessWidget {
  const SectionHeader(this.title, {super.key, this.action, this.subtitle});

  final String title;
  final String? subtitle;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.fromLTRB(
        AppTheme.space4,
        AppTheme.space6,
        AppTheme.space4,
        AppTheme.space3,
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(title,
                style: theme.textTheme
                    .titleLarge?.copyWith(fontSize: 18),
                ),
                if (subtitle != null) ...[
                  const SizedBox(height: 2),
                  Text(subtitle!, style: theme.textTheme.bodySmall),
                ],
              ],
            ),
          ),
          ?action,
        ],
      ),
    );
  }
}

/// A status badge.
///
/// Colour is never the only signal: each badge carries an icon and a word, so the state still
/// reads for a colour-blind user and in a screenshot printed in grey.
class StatusPill extends StatelessWidget {
  const StatusPill(this.label, {super.key, this.color, this.icon,
    this.filled = false,
    this.pulse = false,
  });

  final String label;
  final Color? color;
  final IconData? icon;

  /// Solid rather than tinted. For the one badge on screen that must win.
  final bool filled;

  /// A slow breath on the leading dot. Only a genuinely live auction should use it.
  final bool pulse;

  @override
  Widget build(BuildContext context) {
    final brightness = Theme.of(context).brightness;
    final base = color ?? Theme.of(context).colorScheme.primary;

    // The label is text, so it takes the step of this colour that can actually be read. A filled
    // pill turns that step into the fill and puts the page's own black or white on top of it.
    final strong = AppTheme.readable(base, brightness);
    final fg = filled
        ? (brightness == Brightness.dark ? AppTheme.ink : Colors.white)
        : strong;

    final dot = icon == null
        ? null
        : (pulse
              ? _PulsingIcon(icon: icon!, color: fg)
              : Icon(icon, size: 12, color: fg));

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 11, vertical: 6),
      decoration: BoxDecoration(
        color: filled ? strong : base.withValues(alpha: 0.13),
        borderRadius: BorderRadius.circular(AppTheme.radiusPill),
        border: Border.all(color: filled ? Colors.transparent : base.withValues(alpha: 0.32)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (dot != null) ...[dot, const SizedBox(width: 6)],
          Text(label,
              style: TextStyle(color: fg, fontSize: 11.5,
              height: 1.1,
              fontWeight: FontWeight.w700,
              letterSpacing: 0.1,
            )),
        ],
      ),
    );
  }
}

/// A dot that breathes, to say "this is happening now".
///
/// Honours the platform's reduce-motion setting: when animation is turned off the dot simply
/// stays put rather than disappearing, so the badge never loses its icon.
class _PulsingIcon extends StatefulWidget {
  const _PulsingIcon({required this.icon, required this.color});

  final IconData icon;
  final Color color;

  @override
  State<_PulsingIcon> createState() => _PulsingIconState();
}

class _PulsingIconState extends State<_PulsingIcon>
    with SingleTickerProviderStateMixin {
  late final AnimationController _c = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 1100),
  )..repeat(reverse: true);

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final icon = Icon(widget.icon, size: 10, color: widget.color);

    if (MediaQuery.disableAnimationsOf(context)) return icon;

    return FadeTransition(
      opacity: Tween(
        begin: 0.35,
        end: 1.0,
      ).animate(CurvedAnimation(parent: _c, curve: Curves.easeInOut)),
      child: icon,
    );
  }
}

/// Text that keeps its own left-to-right order inside a right-to-left screen.
///
/// Arabic reorders a run of text, which is right for words and wrong for a compound figure:
/// `0 / 6` participants renders as `6 / 0`, and a clock or an id reads back to front. Anything
/// whose parts have a fixed order — counts, ratios, times, product codes — belongs in here.
class LtrText extends StatelessWidget {
  const LtrText(this.text, {super.key, this.style, this.textAlign});

  final String text;
  final TextStyle? style;
  final TextAlign? textAlign;

  @override
  Widget build(BuildContext context) => Directionality(
    textDirection: TextDirection.ltr,
    child: Text(text, style: style, textAlign: textAlign),
  );
}

/// A labelled figure. Used everywhere a server-decided number is shown.
class Stat extends StatelessWidget {
  const Stat({
    super.key,
    required this.label,
    required this.value,
    this.valueColor,
    this.big = false,
    this.align = CrossAxisAlignment.start,
    this.ltrValue = false,
  });

  final String label;
  final String value;
  final Color? valueColor;
  final bool big;
  final CrossAxisAlignment align;

  /// Set when the value's parts have a fixed order — a rank like `#4`, a ratio, a code — so
  /// Arabic does not reverse it into `4#`.
  final bool ltrValue;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final centred = align == CrossAxisAlignment.center;

    final style =
        (big ? theme.textTheme.headlineMedium : theme.textTheme.titleMedium)
              ?.copyWith(fontWeight: FontWeight.w700, color: valueColor,
              fontFeatures: AppTheme.numeric,
            );

    return Column(
      crossAxisAlignment: align,
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          label,
          textAlign: centred ? TextAlign.center : null,
          style: theme.textTheme.labelSmall?.copyWith(
            fontSize: 11.5,
            letterSpacing: 0.2,
          ),
        ),
        const SizedBox(height: 3),

        // Shrink rather than overflow. Several of these sit side by side in a narrow row, and
        // the width of an amount is not fixed: "20.00 ر.س" and "1,200.00 ر.س" differ by a lot,
        // and a currency written in words is wider again than a one-character symbol.
        FittedBox(
          fit: BoxFit.scaleDown,
          alignment: switch (align) {
            CrossAxisAlignment.center => Alignment.center,
            CrossAxisAlignment.end => AlignmentDirectional.centerEnd,
            _ => AlignmentDirectional.centerStart,
          },
          child: ltrValue
              ? LtrText(
                  value,
                  style: style,
                  textAlign: centred ? TextAlign.center : null,
                )
              : Text(
                  value,
                  textAlign: centred ? TextAlign.center : null,
                  style: style,
                ),
        ),
      ],
    );
  }
}

/// The money figure the eye should land on first.
class PriceDisplay extends StatelessWidget {
  const PriceDisplay({
    super.key,
    required this.label,
    required this.amount,
    this.color,
    this.animate = false,
    this.size = 34,
  });

  final String label;
  final String amount;
  final Color? color;

  /// Grows briefly when the amount changes, so a price moving under your eyes is felt.
  final bool animate;
  final double size;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    final text = Text(
      amount,
      key: ValueKey(amount),
      textAlign: TextAlign.center,
      style: theme.textTheme.displaySmall?.copyWith(
        fontSize: size,
        height: 1.1,
        color: color ?? theme.colorScheme.onSurface,
      ),
    );

    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          label.toUpperCase(),
          style: theme.textTheme.labelSmall?.copyWith(
            fontSize: 11,
            letterSpacing: 0.8,
          ),
        ),
        const SizedBox(height: AppTheme.space1),
        if (!animate || MediaQuery.disableAnimationsOf(context))
          text
        else
          AnimatedSwitcher(
            duration: const Duration(milliseconds: 260),
            transitionBuilder: (child, a) => ScaleTransition(
              scale: Tween(
                begin: 0.88,
                end: 1.0,
              ).animate(CurvedAnimation(parent: a, curve: Curves.easeOutBack)),
              child: FadeTransition(opacity: a, child: child),
            ),
            child: text,
          ),
      ],
    );
  }
}

/// A panel with a soft lift. The default container for a block of related information.
class SurfaceCard extends StatelessWidget {
  const SurfaceCard({
    super.key,
    required this.child,
    this.padding = const EdgeInsets.all(AppTheme.space4),
    this.accent,
    this.onTap,
  });

  final Widget child;
  final EdgeInsets padding;

  /// Tints the surface and border. Used to mark a card that belongs to a live or won auction.
  final Color? accent;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isDark = theme.brightness == Brightness.dark;

    final body = Padding(padding: padding, child: child);

    return DecoratedBox(
      decoration: BoxDecoration(
        color: accent == null
            ? theme.colorScheme.surface
            : Color.alphaBlend(
                accent!.withValues(alpha: isDark ? 0.11 : 0.06),
                theme.colorScheme.surface,
              ),
        borderRadius: BorderRadius.circular(AppTheme.radiusLg),
        border: Border.all(
          color: accent?.withValues(alpha: 0.35) ?? theme.colorScheme.outline,
        ),
        boxShadow: AppTheme.shadow(theme.brightness),
      ),
      child: onTap == null
          ? body
          : Material(
              color: Colors.transparent,
              child: InkWell(
                onTap: onTap,
                borderRadius: BorderRadius.circular(AppTheme.radiusLg),
                child: body,
              ),
            ),
    );
  }
}

/// One wallet, shown as a card.
///
/// Both balances use this, so they look like members of one family — but each keeps its own
/// colour, icon and rule badge, because the whole point is that the two are *not*
/// interchangeable. There is deliberately no way to render a combined figure here: adding cash
/// to store credit would suggest the whole amount can be withdrawn, which it cannot.
class WalletCard extends StatelessWidget {
  const WalletCard({
    super.key,
    required this.title,
    required this.amount,
    required this.accent,
    required this.icon,
    required this.ruleLabel,
    required this.ruleIcon,
    this.note,
    this.actions = const [],
  });

  final String title;
  final String amount;
  final Color accent;
  final IconData icon;

  /// What this balance may be used for. Always present — the rule is part of the balance.
  final String ruleLabel;
  final IconData ruleIcon;

  final String? note;
  final List<Widget> actions;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return SurfaceCard(
      accent: accent,
      padding: const EdgeInsets.all(AppTheme.space5),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Container(
                height: 38,
                width: 38,
                decoration: BoxDecoration(
                  color: accent.withValues(alpha: 0.16),
                  borderRadius: BorderRadius.circular(AppTheme.radiusMd),
                ),
                child: Icon(icon, color: accent, size: 20),
              ),
              const SizedBox(width: AppTheme.space3),
              Expanded(child: Text(title, style: theme.textTheme.titleMedium)),
            ],
          ),
          const SizedBox(height: AppTheme.space4),
          FittedBox(
            fit: BoxFit.scaleDown,
            alignment: AlignmentDirectional.centerStart,
            child: Text(
              amount,
              style: theme.textTheme.displaySmall?.copyWith(
                fontSize: 34,
                height: 1.1,
                fontFeatures: AppTheme.numeric,
              ),
            ),
          ),
          const SizedBox(height: AppTheme.space3),
          StatusPill(ruleLabel, color: accent, icon: ruleIcon),
          if (note != null) ...[
            const SizedBox(height: AppTheme.space3),
            Text(note!, style: theme.textTheme.bodySmall),
          ],
          if (actions.isNotEmpty) ...[
            const SizedBox(height: AppTheme.space5),
            Row(
              children: [
                for (var i = 0; i < actions.length; i++) ...[
                  if (i > 0) const SizedBox(width: AppTheme.space3),
                  Expanded(child: actions[i]),
                ],
              ],
            ),
          ],
        ],
      ),
    );
  }
}

/// A grey block standing in for content that has not arrived.
///
/// A skeleton in the shape of the real thing beats a spinner: the layout does not jump when the
/// data lands, and the wait reads as loading rather than as breakage.
class Skeleton extends StatefulWidget {
  const Skeleton({
    super.key,
    this.height = 14,
    this.width,
    this.radius = AppTheme.radiusSm,
  });

  final double height;
  final double? width;
  final double radius;

  @override
  State<Skeleton> createState() => _SkeletonState();
}

class _SkeletonState extends State<Skeleton>
    with SingleTickerProviderStateMixin {
  late final AnimationController _c = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 1200),
  )..repeat(reverse: true);

  @override
  void dispose() {
    _c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final base = Theme.of(context).colorScheme.surfaceContainerHighest;

    final box = Container(
      height: widget.height,
      width: widget.width,
      decoration: BoxDecoration(
        color: base,
        borderRadius: BorderRadius.circular(widget.radius),
      ),
    );

    if (MediaQuery.disableAnimationsOf(context)) return box;

    return FadeTransition(
      opacity: Tween(
        begin: 0.45,
        end: 1.0,
      ).animate(CurvedAnimation(parent: _c, curve: Curves.easeInOut)),
      child: box,
    );
  }
}

/// A card-shaped skeleton, for lists that are still loading.
class SkeletonCard extends StatelessWidget {
  const SkeletonCard({super.key, this.hasImage = true});

  final bool hasImage;

  @override
  Widget build(BuildContext context) {
    return SurfaceCard(
      padding: EdgeInsets.zero,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (hasImage)
            const ClipRRect(
              borderRadius: BorderRadius.vertical(
                top: Radius.circular(AppTheme.radiusLg),
              ),
              child: Skeleton(height: 150, width: double.infinity, radius: 0),
            ),
          const Padding(
            padding: EdgeInsets.all(AppTheme.space4),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Skeleton(height: 17, width: 190),
                SizedBox(height: AppTheme.space2),
                Skeleton(height: 12, width: 120),
                SizedBox(height: AppTheme.space5),
                Row(
                  children: [
                    Expanded(child: Skeleton(height: 30)),
                    SizedBox(width: AppTheme.space3),
                    Expanded(child: Skeleton(height: 30)),
                    SizedBox(width: AppTheme.space3),
                    Expanded(child: Skeleton(height: 30)),
                  ],
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class EmptyState extends StatelessWidget {
  const EmptyState({super.key, this.message, this.icon = Icons.inbox_rounded, this.action,
    this.title,
  });

  final String? title;

  final String? message;
  final IconData icon;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(AppTheme.space8),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              height: 76,
              width: 76,
              decoration: BoxDecoration(
                color: theme.colorScheme.surfaceContainerHighest,
                shape: BoxShape.circle,
              ),
              child: Icon(icon, size: 34, color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: AppTheme.space4),
            Text(
              title ?? AppStrings.of(context).nothingHere,
                textAlign: TextAlign.center,
                style: theme.textTheme.titleMedium,
            ),
            if (message != null) ...[
              const SizedBox(height: AppTheme.space2),
              Text(
                message!,
                textAlign: TextAlign.center,
                style: theme.textTheme.bodySmall,
              ),
            ],
            if (action != null) ...[const SizedBox(height: AppTheme.space5), action!],
          ],
        ),
      ),
    );
  }
}

class ErrorView extends StatelessWidget {
  const ErrorView({super.key, required this.message, this.onRetry});

  final String message;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(AppTheme.space8),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Container(
              height: 76,
              width: 76,
              decoration: BoxDecoration(
                color: theme.colorScheme.errorContainer,
                shape: BoxShape.circle,
              ),
              child: Icon(Icons.cloud_off_rounded, size: 34, color: theme.colorScheme.onErrorContainer,
              ),
            ),
            const SizedBox(height: AppTheme.space4),
            Text(
              AppStrings.of(context).somethingWentWrong,
              textAlign: TextAlign.center,
              style: theme.textTheme.titleMedium,
            ),
            const SizedBox(height: AppTheme.space2),
            Text(message, textAlign: TextAlign.center,
              style: theme.textTheme.bodySmall,
            ),
            if (onRetry != null) ...[
              const SizedBox(height: AppTheme.space5),
              OutlinedButton.icon(onPressed: onRetry,
                icon: const Icon(Icons.refresh_rounded, size: 18),
                label: Text(AppStrings.of(context).retry)),
            ],
          ],
        ),
      ),
    );
  }
}

/// A product or prize image with a graceful placeholder, since seed images come from the network.
class RemoteImage extends StatelessWidget {
  const RemoteImage(this.url, {super.key, this.height, this.width, this.radius = AppTheme.radiusMd, this.fit = BoxFit.cover});

  final String? url;
  final double? height;
  final double? width;
  final double radius;
  final BoxFit fit;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    // Resolved here rather than at every call site, so no screen can forget to do it.
    final src = AppConfig.mediaUrl(url);

    // A soft two-tone wash rather than a flat grey, so a missing image still looks intentional.
    final placeholder = Container(
      height: height,
      width: width,
      decoration: BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
          colors: [
            scheme.surfaceContainerHighest,
            Color.alphaBlend(
              scheme.primary.withValues(alpha: 0.07),
              scheme.surfaceContainerHighest,
            ),
          ],
        ),
      ),
      child: Icon(Icons.image_outlined,
          color: scheme.outline, size: 30),
    );

    if (src == null) {
      return ClipRRect(borderRadius: BorderRadius.circular(radius), child: placeholder);
    }

    return ClipRRect(
      borderRadius: BorderRadius.circular(radius),
      // The size this is drawn at is what decides how much of the picture is worth decoding, and
      // that is only known once the parent has said how much room there is.
      child: LayoutBuilder(
        builder: (context, constraints) => Image.network(
          src,
          height: height,
          width: width,
          fit: fit,
          /*
           * Decode to the size it is shown at, not the size it was uploaded at.
           *
           * Without this, a photograph straight from a phone camera is held in memory at its full
           * resolution however small it is drawn: 4000 x 3000 is 48 MB for one thumbnail, and a
           * grid of twenty products asks for most of a gigabyte. Cheap Android phones are killed
           * by it. Decoding to the drawn size costs a fraction of that and looks identical.
           */
          cacheWidth: _decodeWidth(context, constraints),
          errorBuilder: (_, _, _) => placeholder,
          loadingBuilder: (context, child, progress) =>
              progress == null ? child : placeholder,
        ),
      ),
    );
  }

  /// How wide to decode, in real device pixels, or null to leave the picture alone.
  int? _decodeWidth(BuildContext context, BoxConstraints constraints) {
    final drawnWidth = _finite(width) ?? _finite(constraints.maxWidth);
    final drawnHeight = _finite(height) ?? _finite(constraints.maxHeight);

    // Nothing bounded it, so there is no size to decode to and guessing would risk a picture
    // blurrier than the one asked for.
    if (drawnWidth == null && drawnHeight == null) return null;

    /*
     * The larger of the two sides, because the fit is `cover` by default.
     *
     * `cover` fills the box and crops the rest, so the side that needs the most resolution
     * depends on the picture's own shape, which is not known until it loads. Taking the larger
     * side means a wide picture in a square slot — a 3000 x 400 banner in a product card, say —
     * is still decoded wide enough to fill the height sharply instead of being blown up from a
     * few dozen pixels.
     */
    final side = math.max(drawnWidth ?? 0, drawnHeight ?? 0);

    // Device pixels, not logical ones: a 124 wide card on a 3x phone really is 372 pixels.
    final pixels = side * MediaQuery.devicePixelRatioOf(context);

    // Full screen on the densest phone. Past this the saving is gone and the ceiling is only
    // there to stop an absurd constraint asking for an absurd bitmap.
    const ceiling = 2048;

    return pixels < 1 ? null : math.min(pixels.round(), ceiling);
  }

  /// A usable measurement, or null for the infinite and the nonsensical.
  static double? _finite(double? value) =>
      value == null || !value.isFinite || value <= 0 ? null : value;
}

/// Progress from the starting price toward the target.
class TargetProgressBar extends StatelessWidget {
  const TargetProgressBar({super.key, required this.progress, this.urgent = false,
    this.height = 8,
    this.color,
  });

  final double progress;
  final bool urgent;
  final double height;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final bar =
        color ?? (urgent ? AppTheme.finalPhase : theme.colorScheme.primary);
    final value = progress.clamp(0.0, 1.0);

    return ClipRRect(
      borderRadius: BorderRadius.circular(AppTheme.radiusPill),
      child: SizedBox(
        height: height,
        child: Stack(
          children: [
            Container(color: theme.colorScheme.surfaceContainerHighest),
            // Animated so a price change slides the bar rather than teleporting it.
            LayoutBuilder(
              builder: (context, c) => AnimatedContainer(
                duration: const Duration(milliseconds: 420),
                curve: Curves.easeOutCubic,
                width: c.maxWidth * value,
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    colors: [bar.withValues(alpha: 0.75), bar],
                  ),
                  borderRadius: BorderRadius.circular(AppTheme.radiusPill),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// What a prize is worth against what its winner pays for it:
/// "Device price X — you get it for Y after a Z% discount".
///
/// [exact] is false before an auction ends, when the price shown is the most the winner can pay
/// (the auction stops at its target, and ends lower if time runs out first).
class PrizePriceBox extends StatelessWidget {
  const PrizePriceBox({
    super.key,
    required this.fullPrice,
    required this.price,
    required this.discount,
    required this.currency,
    this.exact = true,
  });

  final double? fullPrice;
  final double price;
  final double discount;
  final String currency;
  final bool exact;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final green = AppTheme.readable(AppTheme.success, theme.brightness);
    final amount = Fmt.money(price, currency, locale: locale);

    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(AppTheme.space3),
      decoration: BoxDecoration(
        color: AppTheme.success.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(AppTheme.radiusMd),
        border: Border.all(color: AppTheme.success.withValues(alpha: 0.25)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (fullPrice != null)
            Text(
              t.devicePrice(Fmt.money(fullPrice, currency, locale: locale)),
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
                fontFeatures: AppTheme.numeric,
              ),
            ),
          const SizedBox(height: 2),
          Text(
            exact ? t.youGetItFor(amount) : t.youGetItForAtMost(amount),
            style: theme.textTheme.titleMedium?.copyWith(
              fontWeight: FontWeight.w800,
              color: green,
              fontFeatures: AppTheme.numeric,
            ),
          ),
          if (discount > 0) ...[
            const SizedBox(height: AppTheme.space2),
            StatusPill(
              t.afterDiscount(Fmt.percent(discount)),
              color: AppTheme.success,
              icon: Icons.local_offer_rounded,
            ),
          ],
        ],
      ),
    );
  }
}
