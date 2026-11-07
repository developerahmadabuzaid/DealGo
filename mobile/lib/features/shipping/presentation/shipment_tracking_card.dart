import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';
import '../data/shipment_repository.dart';

/// Tracking for a store order, when M5azn delivers it. Shows nothing otherwise, and nothing on
/// a failed lookup either: tracking is extra information, never a reason to show an error.
class StoreOrderShipment extends ConsumerWidget {
  const StoreOrderShipment({super.key, required this.orderId});

  final String orderId;

  @override
  Widget build(BuildContext context, WidgetRef ref) => _fromAsync(ref.watch(storeOrderShipmentProvider(orderId)));
}

/// Tracking for a won prize, when M5azn delivers it.
class PrizeClaimShipment extends ConsumerWidget {
  const PrizeClaimShipment({super.key, required this.claimId});

  final String claimId;

  @override
  Widget build(BuildContext context, WidgetRef ref) => _fromAsync(ref.watch(prizeClaimShipmentProvider(claimId)));
}

Widget _fromAsync(AsyncValue<ShipmentTracking?> value) {
  final tracking = value.valueOrNull;
  if (tracking == null) return const SizedBox.shrink();
  return Padding(
    padding: const EdgeInsets.only(top: AppTheme.space3),
    child: ShipmentTrackingCard(tracking: tracking),
  );
}

class ShipmentTrackingCard extends StatelessWidget {
  const ShipmentTrackingCard({super.key, required this.tracking});

  final ShipmentTracking tracking;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final color = tracking.isCancelled
        ? theme.colorScheme.error
        : tracking.isDelivered
            ? AppTheme.success
            : theme.colorScheme.primary;

    return Container(
      padding: const EdgeInsets.all(AppTheme.space3),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.06),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: color.withValues(alpha: 0.25)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Icon(Icons.local_shipping_outlined, size: 20, color: color),
              const SizedBox(width: AppTheme.space2),
              Expanded(
                child: Text(t.shipmentTitle,
                    style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700)),
              ),
              Flexible(child: StatusPill(t.shipmentStatus(tracking.status), color: color)),
            ],
          ),
          const SizedBox(height: 4),
          Text(t.shipmentBySupplier, style: theme.textTheme.bodySmall),
          if (!tracking.isCancelled) ...[
            const SizedBox(height: AppTheme.space3),
            _Steps(step: tracking.step, color: color, labels: t.shipmentSteps),
          ],
          if (tracking.carrier != null) ...[
            const SizedBox(height: AppTheme.space2),
            _Line(label: t.carrier, value: tracking.carrier!),
          ],
          if (tracking.trackingNumber != null) ...[
            const SizedBox(height: 4),
            _Line(
              label: t.trackingNumber,
              value: tracking.trackingNumber!,
              onCopy: () async {
                await Clipboard.setData(ClipboardData(text: tracking.trackingNumber!));
                if (context.mounted) {
                  ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(t.copied)));
                }
              },
            ),
          ],
          if (tracking.trackingUri != null) ...[
            const SizedBox(height: AppTheme.space2),
            Align(
              alignment: AlignmentDirectional.centerStart,
              child: TextButton.icon(
                onPressed: () => launchUrl(tracking.trackingUri!, mode: LaunchMode.externalApplication),
                icon: const Icon(Icons.open_in_new, size: 18),
                label: Text(t.trackShipment),
              ),
            ),
          ],
          if (tracking.lastSyncedAt != null) ...[
            const SizedBox(height: 4),
            Text(t.lastUpdated(Fmt.dateTime(tracking.lastSyncedAt, locale: locale)),
                style: theme.textTheme.bodySmall),
          ],
        ],
      ),
    );
  }
}

class _Steps extends StatelessWidget {
  const _Steps({required this.step, required this.color, required this.labels});

  final int step;
  final Color color;
  final List<String> labels;

  @override
  Widget build(BuildContext context) {
    final muted = Theme.of(context).dividerColor;

    return Row(
      children: [
        for (var i = 0; i < labels.length; i++)
          Expanded(
            child: Column(
              children: [
                Container(
                  height: 4,
                  margin: const EdgeInsets.symmetric(horizontal: 2),
                  decoration: BoxDecoration(
                    color: i <= step ? color : muted,
                    borderRadius: BorderRadius.circular(2),
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  labels[i],
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  textAlign: TextAlign.center,
                  style: Theme.of(context).textTheme.labelSmall?.copyWith(
                        fontWeight: i == step ? FontWeight.w700 : FontWeight.w400,
                      ),
                ),
              ],
            ),
          ),
      ],
    );
  }
}

class _Line extends StatelessWidget {
  const _Line({required this.label, required this.value, this.onCopy});

  final String label;
  final String value;
  final VoidCallback? onCopy;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Row(
      children: [
        Text('$label: ', style: theme.textTheme.bodySmall),
        Expanded(
          child: SelectableText(value,
              maxLines: 1, style: theme.textTheme.bodyMedium?.copyWith(fontWeight: FontWeight.w600)),
        ),
        if (onCopy != null)
          IconButton(
            visualDensity: VisualDensity.compact,
            tooltip: MaterialLocalizations.of(context).copyButtonLabel,
            icon: const Icon(Icons.copy, size: 18),
            onPressed: onCopy,
          ),
      ],
    );
  }
}
