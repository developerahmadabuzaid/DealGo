import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/app_header.dart';
import '../../../shared/widgets.dart';
import '../data/address_repository.dart';
import 'address_form_screen.dart';

/// "My addresses": every place the customer has asked to have things delivered.
class AddressesScreen extends ConsumerWidget {
  const AddressesScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final addresses = ref.watch(myAddressesProvider);
    final isEmpty = addresses.valueOrNull?.isEmpty ?? false;

    return Scaffold(
      appBar: BrandAppBar(title: Text(t.myAddresses)),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () => AddressFormScreen.open(context, forceDefault: isEmpty),
        icon: const Icon(Icons.add_location_alt_outlined),
        label: Text(t.addAddress),
      ),
      body: addresses.when(
        skipError: true,
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.displayMessage : t.somethingWentWrong,
          onRetry: () => ref.invalidate(myAddressesProvider),
        ),
        data: (list) => list.isEmpty
            ? EmptyState(message: t.noAddressesYet, icon: Icons.location_off_outlined)
            : RefreshIndicator(
                onRefresh: () async => ref.invalidate(myAddressesProvider),
                child: ListView.separated(
                  padding: const EdgeInsets.fromLTRB(16, 16, 16, 96),
                  itemCount: list.length,
                  separatorBuilder: (_, _) => const SizedBox(height: 10),
                  itemBuilder: (context, i) => _AddressCard(address: list[i]),
                ),
              ),
      ),
    );
  }
}

class _AddressCard extends ConsumerWidget {
  const _AddressCard({required this.address});

  final ShippingAddress address;

  Future<void> _run(BuildContext context, WidgetRef ref, Future<void> Function() action) async {
    try {
      await action();
      ref.invalidate(myAddressesProvider);
    } on ApiException catch (e) {
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(e.displayMessage)));
      }
    }
  }

  Future<void> _delete(BuildContext context, WidgetRef ref) async {
    final t = AppStrings.of(context);
    final sure = await showDialog<bool>(
      context: context,
      builder: (dialog) => AlertDialog(
        title: Text(t.deleteAddress),
        content: Text(t.deleteAddressConfirm),
        actions: [
          TextButton(onPressed: () => Navigator.pop(dialog, false), child: Text(t.cancel)),
          TextButton(
            onPressed: () => Navigator.pop(dialog, true),
            style: TextButton.styleFrom(foregroundColor: Theme.of(dialog).colorScheme.error),
            child: Text(t.delete),
          ),
        ],
      ),
    );

    if (sure == true && context.mounted) {
      await _run(context, ref, () => ref.read(addressRepositoryProvider).delete(address.id));
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);

    return SurfaceCard(
      accent: address.isDefault ? AppTheme.brand : null,
      padding: const EdgeInsets.fromLTRB(14, 12, 4, 12),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(child: AddressSummary(address: address)),
          PopupMenuButton<String>(
            onSelected: (choice) => switch (choice) {
              'edit' => AddressFormScreen.open(context, existing: address),
              'default' => _run(context, ref,
                  () => ref.read(addressRepositoryProvider).setDefault(address.id)),
              'delete' => _delete(context, ref),
              _ => null,
            },
            itemBuilder: (_) => [
              PopupMenuItem(value: 'edit', child: Text(t.edit)),
              if (!address.isDefault)
                PopupMenuItem(value: 'default', child: Text(t.setAsDefault)),
              PopupMenuItem(value: 'delete', child: Text(t.delete)),
            ],
          ),
        ],
      ),
    );
  }
}

/// Recipient, phone and where: how an address reads everywhere it is shown.
class AddressSummary extends StatelessWidget {
  const AddressSummary({super.key, required this.address, this.compact = false});

  final ShippingAddress address;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      mainAxisSize: MainAxisSize.min,
      children: [
        Row(
          children: [
            Flexible(
              child: Text(address.fullName,
                  overflow: TextOverflow.ellipsis,
                  style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700)),
            ),
            if (address.isDefault) ...[
              const SizedBox(width: 8),
              StatusPill(t.defaultAddress, color: AppTheme.brand),
            ],
            if (address.hasLocation) ...[
              const SizedBox(width: 6),
              const Icon(Icons.where_to_vote_rounded, size: 16, color: AppTheme.success),
            ],
          ],
        ),
        const SizedBox(height: 4),
        Text(address.summary, style: theme.textTheme.bodyMedium),
        if (!compact) ...[
          const SizedBox(height: 2),
          Text(
            [
              AddressRules.nationalPhone(address.phone),
              if (address.shortAddress != null) address.shortAddress!,
            ].join(' · '),
            textDirection: TextDirection.ltr,
            style: theme.textTheme.bodySmall,
          ),
          if (address.notes != null && address.notes!.isNotEmpty)
            Text(address.notes!, style: theme.textTheme.bodySmall),
        ],
      ],
    );
  }
}

/// Lets the customer choose where something goes, or add a new address on the spot.
/// Returns the chosen address, or null when they close the sheet.
Future<ShippingAddress?> pickDeliveryAddress(BuildContext context, {String? selectedId}) =>
    showModalBottomSheet<ShippingAddress>(
      context: context,
      useRootNavigator: true,
      showDragHandle: true,
      isScrollControlled: true,
      constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * 0.8),
      builder: (_) => _AddressPickerSheet(selectedId: selectedId),
    );

class _AddressPickerSheet extends ConsumerWidget {
  const _AddressPickerSheet({this.selectedId});

  final String? selectedId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final addresses = ref.watch(myAddressesProvider);

    Future<void> addNew(bool first) async {
      final saved = await AddressFormScreen.open(context, forceDefault: first);
      if (saved != null && context.mounted) Navigator.of(context).pop(saved);
    }

    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
        child: addresses.when(
          loading: () => const Padding(
              padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
          error: (e, _) => ErrorView(
            message: e is ApiException ? e.displayMessage : t.somethingWentWrong,
            onRetry: () => ref.invalidate(myAddressesProvider),
          ),
          data: (list) => ListView(
            shrinkWrap: true,
            children: [
              Text(t.chooseDeliveryAddress, style: Theme.of(context).textTheme.titleMedium),
              const SizedBox(height: AppTheme.space3),
              if (list.isEmpty)
                Padding(
                  padding: const EdgeInsets.only(bottom: AppTheme.space3),
                  child: Text(t.noAddressesYet),
                ),
              for (final a in list)
                Padding(
                  padding: const EdgeInsets.only(bottom: 8),
                  child: SurfaceCard(
                    accent: a.id == selectedId ? AppTheme.brand : null,
                    padding: const EdgeInsets.all(12),
                    onTap: () => Navigator.of(context).pop(a),
                    child: Row(
                      children: [
                        Icon(
                          a.id == selectedId
                              ? Icons.radio_button_checked_rounded
                              : Icons.radio_button_unchecked_rounded,
                          size: 20,
                          color: a.id == selectedId
                              ? AppTheme.brand
                              : Theme.of(context).colorScheme.outline,
                        ),
                        const SizedBox(width: 10),
                        Expanded(child: AddressSummary(address: a, compact: true)),
                      ],
                    ),
                  ),
                ),
              const SizedBox(height: 4),
              OutlinedButton.icon(
                onPressed: () => addNew(list.isEmpty),
                icon: const Icon(Icons.add_location_alt_outlined),
                label: Text(t.addAddress),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// The address an order or prize will go to, with a way to change it. With nothing chosen yet
/// it is a prompt to choose or add one.
class DeliveryAddressCard extends StatelessWidget {
  const DeliveryAddressCard({super.key, required this.address, required this.onChange});

  final ShippingAddress? address;
  final VoidCallback onChange;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return SurfaceCard(
      padding: const EdgeInsets.fromLTRB(14, 12, 8, 12),
      onTap: onChange,
      accent: address == null ? theme.colorScheme.error : null,
      child: Row(
        children: [
          Icon(Icons.local_shipping_outlined,
              color: address == null ? theme.colorScheme.error : AppTheme.brand),
          const SizedBox(width: 10),
          Expanded(
            child: address == null
                ? Text(t.chooseDeliveryAddress,
                    style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700))
                : AddressSummary(address: address!, compact: true),
          ),
          TextButton(onPressed: onChange, child: Text(address == null ? t.addAddress : t.changeAddress)),
        ],
      ),
    );
  }
}
