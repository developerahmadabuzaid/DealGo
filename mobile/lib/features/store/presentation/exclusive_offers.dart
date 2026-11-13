import 'dart:async';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';
import '../data/store_repository.dart';

/// "1ي 05:12:09", "05:12:09" or "12:09": how long is left, as a timer reads.
String offerClock(Duration left, AppStrings t) {
  if (left.isNegative) left = Duration.zero;
  String two(int n) => n.toString().padLeft(2, '0');
  final h = left.inHours % 24;
  final m = left.inMinutes % 60;
  final s = left.inSeconds % 60;
  final clock = left.inHours > 0 ? '${two(h)}:${two(m)}:${two(s)}' : '${two(m)}:${two(s)}';
  return left.inDays > 0 ? '${t.days(left.inDays)} $clock' : clock;
}

/// Repaints itself once a second while the offer runs; the times come from the server.
class OfferCountdown extends StatefulWidget {
  const OfferCountdown({super.key, required this.offer, this.style, this.compact = false});

  final ExclusiveOffer offer;
  final TextStyle? style;
  final bool compact;

  @override
  State<OfferCountdown> createState() => _OfferCountdownState();
}

class _OfferCountdownState extends State<OfferCountdown> {
  Timer? _tick;

  @override
  void initState() {
    super.initState();
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
    final now = DateTime.now().toUtc();
    final o = widget.offer;

    final String text;
    if (now.isBefore(o.startsAt)) {
      text = widget.compact
          ? offerClock(o.startsAt.difference(now), t)
          : t.offerStartsIn(offerClock(o.startsAt.difference(now), t));
    } else if (now.isBefore(o.endsAt)) {
      text = widget.compact
          ? offerClock(o.endsAt.difference(now), t)
          : t.offerEndsIn(offerClock(o.endsAt.difference(now), t));
    } else {
      text = t.offerEnded;
    }

    return Text(
      text,
      maxLines: 1,
      style: (widget.style ?? const TextStyle()).copyWith(fontFeatures: AppTheme.numeric),
    );
  }
}

/// On an offer's own page: that it is exclusive, how long is left, and the limit per customer.
class ExclusiveOfferBanner extends StatelessWidget {
  const ExclusiveOfferBanner({super.key, required this.offer});

  final ExclusiveOffer offer;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      decoration: BoxDecoration(
        gradient: const LinearGradient(colors: [Color(0xFFE53935), Color(0xFFF26A1B)]),
        borderRadius: BorderRadius.circular(AppTheme.radiusMd),
        boxShadow: [
          BoxShadow(color: const Color(0xFFE53935).withValues(alpha: 0.25), blurRadius: 12, offset: const Offset(0, 4)),
        ],
      ),
      child: Row(
        children: [
          const Icon(Icons.local_fire_department_rounded, color: Colors.white, size: 26),
          const SizedBox(width: 10),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  t.exclusiveOffer,
                  style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 15),
                ),
                const SizedBox(height: 2),
                Text(
                  t.offerLimitPerCustomer(offer.maxPerUser),
                  style: TextStyle(color: Colors.white.withValues(alpha: 0.92), fontSize: 12.5),
                ),
              ],
            ),
          ),
          Container(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
            decoration: BoxDecoration(
              color: Colors.black.withValues(alpha: 0.18),
              borderRadius: BorderRadius.circular(10),
            ),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                const Icon(Icons.timer_outlined, color: Colors.white, size: 16),
                const SizedBox(width: 5),
                OfferCountdown(
                  offer: offer,
                  compact: true,
                  style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w800, fontSize: 13.5),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// The hot row on the home page: every exclusive offer running now, each with its own clock.
class ExclusiveOffersSection extends StatelessWidget {
  const ExclusiveOffersSection({super.key, required this.products});

  final List<StoreProduct> products;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final scale = MediaQuery.textScalerOf(context).scale(1).clamp(1.0, 1.35);

    return Container(
      margin: const EdgeInsets.only(top: AppTheme.space2),
      padding: const EdgeInsets.only(bottom: AppTheme.space3),
      decoration: BoxDecoration(
        gradient: LinearGradient(
          begin: Alignment.topCenter,
          end: Alignment.bottomCenter,
          colors: [
            const Color(0xFFE53935).withValues(alpha: theme.brightness == Brightness.dark ? 0.22 : 0.10),
            theme.colorScheme.surface,
          ],
        ),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsetsDirectional.fromSTEB(AppTheme.space3, 12, AppTheme.space3, 8),
            child: Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(5),
                  decoration: const BoxDecoration(
                    gradient: LinearGradient(colors: [Color(0xFFE53935), Color(0xFFF26A1B)]),
                    shape: BoxShape.circle,
                  ),
                  child: const Icon(Icons.local_fire_department_rounded, color: Colors.white, size: 18),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        t.exclusiveOffers,
                        style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w900),
                      ),
                      Text(t.exclusiveOffersHint, style: theme.textTheme.bodySmall),
                    ],
                  ),
                ),
              ],
            ),
          ),
          SizedBox(
            height: 248 * scale,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: AppTheme.space3),
              itemCount: products.length,
              separatorBuilder: (_, _) => const SizedBox(width: AppTheme.space2),
              itemBuilder: (_, i) => _ExclusiveOfferCard(product: products[i]),
            ),
          ),
        ],
      ),
    );
  }
}

class _ExclusiveOfferCard extends StatelessWidget {
  const _ExclusiveOfferCard({required this.product});

  final StoreProduct product;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final p = product;
    final offer = p.offer;

    return SizedBox(
      width: 158,
      child: Material(
        color: theme.colorScheme.surface,
        elevation: 1.5,
        shadowColor: const Color(0x33E53935),
        borderRadius: BorderRadius.circular(AppTheme.radiusMd),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: () => context.push('/store/products/${p.id}'),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Expanded(
                child: Stack(
                  fit: StackFit.expand,
                  children: [
                    ColoredBox(
                      color: theme.colorScheme.surfaceContainerHighest,
                      child: RemoteImage(p.imageUrl, radius: 0),
                    ),
                    PositionedDirectional(
                      top: 6,
                      start: 6,
                      child: _Tag(
                        label: p.discountPercent > 0 ? t.discountOff(p.discountPercent) : t.exclusiveBadge,
                        color: const Color(0xFFE53935),
                      ),
                    ),
                    if (offer != null)
                      PositionedDirectional(
                        bottom: 6,
                        start: 6,
                        end: 6,
                        child: Container(
                          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
                          decoration: BoxDecoration(
                            color: Colors.black.withValues(alpha: 0.62),
                            borderRadius: BorderRadius.circular(8),
                          ),
                          child: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              const Icon(Icons.timer_outlined, color: Colors.white, size: 13),
                              const SizedBox(width: 4),
                              Flexible(
                                child: OfferCountdown(
                                  offer: offer,
                                  compact: true,
                                  style: const TextStyle(
                                    color: Colors.white,
                                    fontSize: 12,
                                    fontWeight: FontWeight.w800,
                                  ),
                                ),
                              ),
                            ],
                          ),
                        ),
                      ),
                    if (!p.inStock)
                      Positioned.fill(
                        child: ColoredBox(
                          color: Colors.black.withValues(alpha: 0.5),
                          child: Center(child: _Tag(label: t.offerSoldOut, color: AppTheme.ink)),
                        ),
                      ),
                  ],
                ),
              ),
              Padding(
                padding: const EdgeInsets.fromLTRB(8, 7, 8, 8),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      p.name,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600),
                    ),
                    const SizedBox(height: 2),
                    FittedBox(
                      fit: BoxFit.scaleDown,
                      alignment: AlignmentDirectional.centerStart,
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        textBaseline: TextBaseline.alphabetic,
                        crossAxisAlignment: CrossAxisAlignment.baseline,
                        children: [
                          Text(
                            Fmt.money(p.price, p.currency, locale: locale),
                            style: const TextStyle(
                              color: Color(0xFFE53935),
                              fontSize: 15,
                              fontWeight: FontWeight.w900,
                              fontFeatures: AppTheme.numeric,
                            ),
                          ),
                          if (p.isOnOffer) ...[
                            const SizedBox(width: 5),
                            Text(
                              Fmt.money(p.compareAtPrice, p.currency, locale: locale),
                              style: TextStyle(
                                color: theme.colorScheme.outline,
                                fontSize: 11.5,
                                decoration: TextDecoration.lineThrough,
                                fontFeatures: AppTheme.numeric,
                              ),
                            ),
                          ],
                        ],
                      ),
                    ),
                    if (offer != null) ...[
                      const SizedBox(height: 2),
                      Text(
                        t.offerLimitPerCustomer(offer.maxPerUser),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: theme.textTheme.labelSmall?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                      ),
                    ],
                  ],
                ),
              ),
            ],
          ),
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
        padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
        decoration: BoxDecoration(color: color, borderRadius: BorderRadius.circular(6)),
        child: Text(
          label,
          style: const TextStyle(color: Colors.white, fontSize: 11, fontWeight: FontWeight.w800),
        ),
      );
}
