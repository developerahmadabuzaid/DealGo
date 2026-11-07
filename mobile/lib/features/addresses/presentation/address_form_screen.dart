import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:latlong2/latlong.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/saudi_phone.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/app_header.dart';
import '../data/address_lookup.dart';
import '../data/address_repository.dart';
import 'location_picker_screen.dart';

/// The same limits the server applies, so the form says what is wrong before anything is sent.
/// The server checks all of it again.
class AddressRules {
  const AddressRules._();

  static const nameMax = 100;
  static const cityMax = 80;
  static const districtMax = 80;
  static const streetMax = 150;
  static const buildingMax = 30;
  static const notesMax = 500;

  static final _shortAddress = RegExp(r'^[A-Z]{4}[0-9]{4}$');

  static String? required(AppStrings t, String? value, String field, int max) {
    final v = value?.trim() ?? '';
    if (v.isEmpty) return t.fieldRequired(field);
    if (v.length > max) return t.tooLong(max);
    return null;
  }

  static String? optional(AppStrings t, String? value, int max) =>
      (value?.trim().length ?? 0) > max ? t.tooLong(max) : null;

  static String? name(AppStrings t, String? value) {
    final v = value?.trim() ?? '';
    if (v.isEmpty) return t.fieldRequired(t.recipientName);
    if (v.length < 2) return t.nameTooShort;
    if (v.length > nameMax) return t.tooLong(nameMax);
    return null;
  }

  static String? phone(AppStrings t, String? value) =>
      SaudiPhone.normalise(value ?? '') == null ? t.invalidSaudiMobile : null;

  /// "rrrd 2929", "RRRD-٢٩٢٩" and "RRRD2929" are the same code.
  static String? normaliseShortAddress(String? raw) {
    if (raw == null || raw.trim().isEmpty) return null;
    final out = StringBuffer();
    for (final r in raw.toUpperCase().runes) {
      final c = String.fromCharCode(r);
      if (c.trim().isEmpty || c == '-') continue;
      final digit = SaudiPhone.westernDigits(c);
      out.write(digit.isNotEmpty ? digit : c);
    }
    return out.toString();
  }

  static String? shortAddress(AppStrings t, String? value) {
    final code = normaliseShortAddress(value);
    if (code == null) return null;
    return _shortAddress.hasMatch(code) ? null : t.invalidShortAddress;
  }

  /// +9665XXXXXXXX back to the 05XXXXXXXX people type.
  static String nationalPhone(String stored) {
    final normalised = SaudiPhone.normalise(stored);
    return normalised == null ? stored : '0${normalised.substring(SaudiPhone.countryCode.length)}';
  }
}

/// Adds a delivery address, or edits [existing]. Pops with the saved address.
class AddressFormScreen extends ConsumerStatefulWidget {
  const AddressFormScreen({
    super.key,
    this.existing,
    this.forceDefault = false,
    this.recipientName,
    this.recipientPhone,
  });

  final ShippingAddress? existing;

  /// A new address starts with the account holder as the recipient: the usual case.
  final String? recipientName;
  final String? recipientPhone;

  /// The first address is the default whatever is ticked, so the box is not offered.
  final bool forceDefault;

  /// Opens the form above the current page and returns what was saved, if anything.
  static Future<ShippingAddress?> open(BuildContext context,
      {ShippingAddress? existing, bool forceDefault = false}) {
    final user = existing == null
        ? ProviderScope.containerOf(context, listen: false).read(authControllerProvider).user
        : null;

    return Navigator.of(context, rootNavigator: true).push<ShippingAddress>(MaterialPageRoute(
      builder: (_) => AddressFormScreen(
        existing: existing,
        forceDefault: forceDefault,
        recipientName: user?.fullName,
        recipientPhone: user?.phoneNumber,
      ),
    ));
  }

  @override
  ConsumerState<AddressFormScreen> createState() => _AddressFormScreenState();
}

class _AddressFormScreenState extends ConsumerState<AddressFormScreen> {
  final _form = GlobalKey<FormState>();
  late final TextEditingController _name;
  late final TextEditingController _phone;
  late final TextEditingController _city;
  late final TextEditingController _district;
  late final TextEditingController _street;
  late final TextEditingController _building;
  late final TextEditingController _notes;
  late final TextEditingController _shortAddress;

  LatLng? _location;
  late bool _isDefault;

  /// Looking up the address of a picked point.
  bool _finding = false;

  /// What each field was last filled with from the map. A field still holding that (or empty) is
  /// the map's to change again; one the customer typed into is never overwritten.
  final _filledFromMap = <TextEditingController, String>{};
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final a = widget.existing;

    _name = TextEditingController(text: a?.fullName ?? widget.recipientName ?? '');
    _phone = TextEditingController(
        text: AddressRules.nationalPhone(a?.phone ?? widget.recipientPhone ?? ''));
    _city = TextEditingController(text: a?.city ?? '');
    _district = TextEditingController(text: a?.district ?? '');
    _street = TextEditingController(text: a?.street ?? '');
    _building = TextEditingController(text: a?.buildingNumber ?? '');
    _notes = TextEditingController(text: a?.notes ?? '');
    _shortAddress = TextEditingController(text: a?.shortAddress ?? '');
    _location = a != null && a.hasLocation ? LatLng(a.latitude!, a.longitude!) : null;
    _isDefault = a?.isDefault ?? widget.forceDefault;
  }

  @override
  void dispose() {
    for (final c in [_name, _phone, _city, _district, _street, _building, _notes, _shortAddress]) {
      c.dispose();
    }
    super.dispose();
  }

  Future<void> _pickLocation({bool locate = false}) async {
    final picked = await ref.read(locationPickerProvider)(context, initial: _location, locate: locate);
    if (picked == null || !mounted) return;

    setState(() {
      _location = picked;
      _finding = true;
    });

    // City, district, street and building from the point, so the customer only checks them.
    final found = await ref.read(addressLookupProvider).at(picked);
    if (!mounted) return;
    setState(() => _finding = false);

    final t = AppStrings.of(context);
    final messenger = ScaffoldMessenger.of(context)..hideCurrentSnackBar();
    if (found == null) {
      messenger.showSnackBar(SnackBar(content: Text(t.addressNotFoundFromMap)));
      return;
    }

    setState(() {
      _fill(_city, found.city);
      _fill(_district, found.district);
      _fill(_street, found.street);
      _fill(_building, found.buildingNumber);
    });
    _form.currentState?.validate();
    messenger.showSnackBar(SnackBar(content: Text(t.addressFilledFromMap)));
  }

  void _fill(TextEditingController field, String? value) {
    if (value == null) return;
    final current = field.text.trim();
    if (current.isNotEmpty && current != _filledFromMap[field]) return;
    field.text = value;
    _filledFromMap[field] = value;
  }

  static String? _orNull(String text) => text.trim().isEmpty ? null : text.trim();

  Future<void> _save() async {
    if (!(_form.currentState?.validate() ?? false)) return;

    final input = AddressInput(
      fullName: _name.text.trim(),
      phone: SaudiPhone.normalise(_phone.text)!,
      city: _city.text.trim(),
      district: _district.text.trim(),
      street: _street.text.trim(),
      buildingNumber: _orNull(_building.text),
      notes: _orNull(_notes.text),
      shortAddress: AddressRules.normaliseShortAddress(_shortAddress.text),
      latitude: _location?.latitude,
      longitude: _location?.longitude,
      isDefault: _isDefault,
    );

    setState(() {
      _saving = true;
      _error = null;
    });

    try {
      final repo = ref.read(addressRepositoryProvider);
      final saved = widget.existing == null
          ? await repo.create(input)
          : await repo.update(widget.existing!.id, input);

      ref.invalidate(myAddressesProvider);
      if (mounted) Navigator.of(context).pop(saved);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.displayMessage);
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    InputDecoration label(String text, {String? hint, Widget? suffix}) =>
        InputDecoration(labelText: text, hintText: hint, suffixIcon: suffix);

    const gap = SizedBox(height: AppTheme.space3);

    return Scaffold(
      appBar: BrandAppBar(title: Text(widget.existing == null ? t.addAddress : t.editAddress)),
      body: Form(
        key: _form,
        autovalidateMode: AutovalidateMode.onUserInteraction,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
          children: [
            _LocationCard(
              location: _location,
              finding: _finding,
              onPick: () => _pickLocation(),
              onLocate: () => _pickLocation(locate: true),
            ),
            const SizedBox(height: AppTheme.space4),
            TextFormField(
              key: const ValueKey('address-name'),
              controller: _name,
              decoration: label(t.recipientName),
              textInputAction: TextInputAction.next,
              validator: (v) => AddressRules.name(t, v),
            ),
            gap,
            TextFormField(
              key: const ValueKey('address-phone'),
              controller: _phone,
              decoration: label(t.recipientPhone, hint: '05XXXXXXXX'),
              keyboardType: TextInputType.phone,
              inputFormatters: const [SaudiPhoneInputFormatter()],
              textDirection: TextDirection.ltr,
              textInputAction: TextInputAction.next,
              validator: (v) => AddressRules.phone(t, v),
            ),
            gap,
            TextFormField(
              key: const ValueKey('address-city'),
              controller: _city,
              decoration: label(t.city),
              textInputAction: TextInputAction.next,
              validator: (v) => AddressRules.required(t, v, t.city, AddressRules.cityMax),
            ),
            gap,
            TextFormField(
              key: const ValueKey('address-district'),
              controller: _district,
              decoration: label(t.district),
              textInputAction: TextInputAction.next,
              validator: (v) =>
                  AddressRules.required(t, v, t.district, AddressRules.districtMax),
            ),
            gap,
            TextFormField(
              key: const ValueKey('address-street'),
              controller: _street,
              decoration: label(t.street),
              textInputAction: TextInputAction.next,
              validator: (v) => AddressRules.required(t, v, t.street, AddressRules.streetMax),
            ),
            gap,
            TextFormField(
              key: const ValueKey('address-building'),
              controller: _building,
              decoration: label(t.buildingNumber, hint: t.optional),
              textInputAction: TextInputAction.next,
              validator: (v) => AddressRules.optional(t, v, AddressRules.buildingMax),
            ),
            gap,
            TextFormField(
              key: const ValueKey('address-short'),
              controller: _shortAddress,
              decoration: label(t.shortAddress, hint: t.shortAddressHint),
              textCapitalization: TextCapitalization.characters,
              textDirection: TextDirection.ltr,
              inputFormatters: [LengthLimitingTextInputFormatter(12)],
              textInputAction: TextInputAction.next,
              validator: (v) => AddressRules.shortAddress(t, v),
            ),
            gap,
            TextFormField(
              key: const ValueKey('address-notes'),
              controller: _notes,
              decoration: label(t.addressNotes, hint: t.addressNotesHint),
              minLines: 2,
              maxLines: 4,
              validator: (v) => AddressRules.optional(t, v, AddressRules.notesMax),
            ),
            if (!widget.forceDefault && !(widget.existing?.isDefault ?? false)) ...[
              gap,
              SwitchListTile(
                contentPadding: EdgeInsets.zero,
                title: Text(t.makeDefaultAddress),
                value: _isDefault,
                onChanged: (v) => setState(() => _isDefault = v),
              ),
            ],
            if (_error != null) ...[
              gap,
              Text(_error!, style: TextStyle(color: theme.colorScheme.error)),
            ],
          ],
        ),
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: FilledButton(
            key: const ValueKey('address-save'),
            onPressed: _saving ? null : _save,
            child: _saving
                ? const SizedBox(
                    height: 20,
                    width: 20,
                    child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                : Text(t.save),
          ),
        ),
      ),
    );
  }
}

/// The pin, or the two ways to drop one. No map is drawn here: the picker has the real one.
class _LocationCard extends StatelessWidget {
  const _LocationCard({
    required this.location,
    required this.onPick,
    required this.onLocate,
    this.finding = false,
  });

  final LatLng? location;
  final bool finding;
  final VoidCallback onPick;
  final VoidCallback onLocate;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final picked = location != null;

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Icon(picked ? Icons.where_to_vote_rounded : Icons.location_on_outlined,
                    color: picked ? AppTheme.success : AppTheme.brand),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(picked ? t.locationPicked : t.locationOnMap,
                      style: theme.textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700)),
                ),
                if (picked)
                  Text(
                    '${location!.latitude.toStringAsFixed(5)}, ${location!.longitude.toStringAsFixed(5)}',
                    textDirection: TextDirection.ltr,
                    style: theme.textTheme.labelSmall,
                  ),
              ],
            ),
            if (finding) ...[
              const SizedBox(height: 8),
              Row(
                key: const ValueKey('address-finding'),
                children: [
                  const SizedBox(width: 14, height: 14, child: CircularProgressIndicator(strokeWidth: 2)),
                  const SizedBox(width: 8),
                  Text(t.findingAddress, style: theme.textTheme.bodySmall),
                ],
              ),
            ],
            const SizedBox(height: 10),
            Row(
              children: [
                Expanded(
                  child: OutlinedButton.icon(
                    key: const ValueKey('address-locate'),
                    onPressed: onLocate,
                    icon: const Icon(Icons.my_location_rounded, size: 18),
                    label: Text(t.useMyLocation),
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: OutlinedButton.icon(
                    key: const ValueKey('address-map'),
                    onPressed: onPick,
                    icon: const Icon(Icons.map_outlined, size: 18),
                    label: Text(picked ? t.changeLocation : t.pickOnMap),
                  ),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
