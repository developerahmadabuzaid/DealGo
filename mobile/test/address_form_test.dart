import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:latlong2/latlong.dart';
import 'package:mobile/features/addresses/data/address_lookup.dart';
import 'package:mobile/features/addresses/data/address_repository.dart';
import 'package:mobile/features/addresses/presentation/address_form_screen.dart';
import 'package:mobile/features/addresses/presentation/location_picker_screen.dart';

/// Records what the form would have sent.
class _FakeAddresses implements AddressRepository {
  AddressInput? created;
  String? updatedId;
  AddressInput? updated;

  ShippingAddress _saved(String id, AddressInput i) => ShippingAddress(
        id: id,
        fullName: i.fullName,
        phone: i.phone,
        city: i.city,
        district: i.district,
        street: i.street,
        buildingNumber: i.buildingNumber,
        notes: i.notes,
        shortAddress: i.shortAddress,
        latitude: i.latitude,
        longitude: i.longitude,
        isDefault: i.isDefault,
      );

  @override
  Future<ShippingAddress> create(AddressInput input) async {
    created = input;
    return _saved('new', input);
  }

  @override
  Future<ShippingAddress> update(String id, AddressInput input) async {
    updatedId = id;
    updated = input;
    return _saved(id, input);
  }

  @override
  Future<List<ShippingAddress>> list() async => const [];

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

/// The phone's geocoder, answering what each test says.
class _FakeLookup implements AddressLookup {
  _FakeLookup(this.answers);

  final List<FoundAddress?> answers;
  final asked = <LatLng>[];

  @override
  Future<FoundAddress?> at(LatLng point, {Locale locale = const Locale('ar', 'SA')}) async {
    asked.add(point);
    return answers.removeAt(0);
  }
}

void main() {
  const t = AppStrings(Locale('ar'));

  group('what the phone says, as address fields', () {
    test('an iPhone answer fills every field', () {
      final a = FoundAddress.fromParts(
        locality: 'الرياض',
        administrativeArea: 'منطقة الرياض',
        subLocality: 'العليا',
        thoroughfare: 'طريق الملك فهد',
        street: '2929 طريق الملك فهد',
        subThoroughfare: '2929',
      );
      expect([a.city, a.district, a.street, a.buildingNumber], ['الرياض', 'العليا', 'طريق الملك فهد', '2929']);
    });

    test('an Android answer with a plus code and a whole first line keeps only what a courier can use', () {
      final a = FoundAddress.fromParts(
        locality: null,
        subAdministrativeArea: 'جدة',
        subLocality: 'حي الروضة',
        thoroughfare: null,
        street: '8Q7X+2F، حي الروضة، جدة',
        subThoroughfare: 'بدون',
      );
      expect(a.city, 'جدة');
      expect(a.district, 'حي الروضة');
      expect(a.street, isNull, reason: 'a plus code or a whole line is not a street');
      expect(a.buildingNumber, isNull, reason: 'not a number');
    });

    test('nothing usable is nothing', () {
      expect(FoundAddress.fromParts(street: '8Q7X+2F').isEmpty, isTrue);
    });
  });

  group('address rules', () {
    test('a Saudi mobile is required, however it is typed', () {
      expect(AddressRules.phone(t, '0551234567'), isNull);
      expect(AddressRules.phone(t, '٠٥٥١٢٣٤٥٦٧'), isNull);
      expect(AddressRules.phone(t, '12345'), t.invalidSaudiMobile);
      expect(AddressRules.phone(t, ''), t.invalidSaudiMobile);
    });

    test('the short address is four letters then four digits', () {
      expect(AddressRules.normaliseShortAddress(' rrrd 2929 '), 'RRRD2929');
      expect(AddressRules.normaliseShortAddress('RRRD-٢٩٢٩'), 'RRRD2929');
      expect(AddressRules.shortAddress(t, ''), isNull, reason: 'it is optional');
      expect(AddressRules.shortAddress(t, 'rrrd2929'), isNull);
      expect(AddressRules.shortAddress(t, 'ABC123'), t.invalidShortAddress);
      expect(AddressRules.shortAddress(t, '12345678'), t.invalidShortAddress);
    });

    test('names and lengths', () {
      expect(AddressRules.name(t, ' '), t.fieldRequired(t.recipientName));
      expect(AddressRules.name(t, 'أ'), t.nameTooShort);
      expect(AddressRules.name(t, 'أحمد'), isNull);
      expect(AddressRules.required(t, 'x' * 81, t.city, AddressRules.cityMax), t.tooLong(80));
      expect(AddressRules.optional(t, null, 30), isNull);
    });

    test('a stored number reads back the way it is typed', () {
      expect(AddressRules.nationalPhone('+966551234567'), '0551234567');
    });
  });

  group('address form', () {
    Future<(_FakeAddresses, List<ShippingAddress?>)> open(WidgetTester tester,
        {ShippingAddress? existing, String? name, String? phone, _FakeLookup? lookup, List<LatLng>? picks}) async {
      final fake = _FakeAddresses();
      final popped = <ShippingAddress?>[];

      // Tall enough that every field is built and can be typed into.
      tester.view.physicalSize = const Size(1080, 3600);
      tester.view.devicePixelRatio = 2;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(ProviderScope(
        overrides: [
          addressRepositoryProvider.overrideWithValue(fake),
          if (lookup != null) addressLookupProvider.overrideWithValue(lookup),
          if (picks != null)
            locationPickerProvider.overrideWithValue(
                (context, {LatLng? initial, bool locate = false}) async => picks.removeAt(0)),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          locale: const Locale('ar'),
          localizationsDelegates: const [
            AppStringsDelegate(),
            GlobalMaterialLocalizations.delegate,
            GlobalWidgetsLocalizations.delegate,
            GlobalCupertinoLocalizations.delegate,
          ],
          supportedLocales: AppStrings.supportedLocales,
          home: Builder(
            builder: (context) => Scaffold(
              body: TextButton(
                onPressed: () async {
                  popped.add(await Navigator.of(context).push<ShippingAddress>(MaterialPageRoute(
                    builder: (_) => AddressFormScreen(
                        existing: existing, recipientName: name, recipientPhone: phone),
                  )));
                },
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ));
      await tester.pumpAndSettle();
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      return (fake, popped);
    }

    Future<void> type(WidgetTester tester, String key, String text) =>
        tester.enterText(find.byKey(ValueKey('address-$key')), text);

    Future<void> save(WidgetTester tester) async {
      await tester.tap(find.byKey(const ValueKey('address-save')));
      await tester.pumpAndSettle();
    }

    testWidgets('nothing is sent until the required fields are filled in', (tester) async {
      final (fake, popped) = await open(tester);

      await save(tester);

      expect(fake.created, isNull);
      expect(popped, isEmpty, reason: 'the form stays open');
      expect(find.text(t.fieldRequired(t.recipientName)), findsOneWidget);
      expect(find.text(t.invalidSaudiMobile), findsOneWidget);
      expect(find.text(t.fieldRequired(t.city)), findsOneWidget);
      expect(find.text(t.fieldRequired(t.district)), findsOneWidget);
      expect(find.text(t.fieldRequired(t.street)), findsOneWidget);
    });

    testWidgets('a wrong mobile number or short address is caught before sending', (tester) async {
      final (fake, _) = await open(tester, name: 'أحمد علي');

      await type(tester, 'phone', '0123');
      await type(tester, 'city', 'الرياض');
      await type(tester, 'district', 'العليا');
      await type(tester, 'street', 'طريق الملك فهد');
      await type(tester, 'short', 'AB12');
      await save(tester);

      expect(fake.created, isNull);
      expect(find.text(t.invalidSaudiMobile), findsOneWidget);
      expect(find.text(t.invalidShortAddress), findsOneWidget);
    });

    testWidgets('a complete address is sent cleaned up and the form closes with it',
        (tester) async {
      final (fake, popped) =
          await open(tester, name: 'أحمد علي', phone: '+966551234567');

      // The account holder is the recipient to start with.
      expect(find.text('أحمد علي'), findsOneWidget);
      expect(find.text('0551234567'), findsOneWidget);

      await type(tester, 'city', ' الرياض ');
      await type(tester, 'district', 'العليا');
      await type(tester, 'street', 'طريق الملك فهد');
      await type(tester, 'building', '1234');
      await type(tester, 'short', 'rrrd 2929');
      await type(tester, 'notes', 'الدور الثاني');
      await save(tester);

      final sent = fake.created!;
      expect(sent.fullName, 'أحمد علي');
      expect(sent.phone, '+966551234567');
      expect(sent.city, 'الرياض');
      expect(sent.buildingNumber, '1234');
      expect(sent.shortAddress, 'RRRD2929');
      expect(sent.notes, 'الدور الثاني');
      expect(sent.latitude, isNull, reason: 'no pin was dropped');
      expect(sent.toJson()['country'], 'SA');

      expect(popped.single?.id, 'new');
    });

    testWidgets('editing keeps the pin and sends the change for the same address', (tester) async {
      const existing = ShippingAddress(
        id: 'a1',
        fullName: 'سارة',
        phone: '+966501112222',
        city: 'جدة',
        district: 'الروضة',
        street: 'شارع التحلية',
        latitude: 21.54,
        longitude: 39.17,
        isDefault: true,
      );
      final (fake, popped) = await open(tester, existing: existing);

      expect(find.text(t.locationPicked), findsOneWidget);
      await type(tester, 'street', 'شارع فلسطين');
      await save(tester);

      expect(fake.updatedId, 'a1');
      expect(fake.updated!.street, 'شارع فلسطين');
      expect(fake.updated!.phone, '+966501112222');
      expect(fake.updated!.latitude, 21.54);
      expect(fake.updated!.isDefault, isTrue);
      expect(popped.single?.street, 'شارع فلسطين');
    });
  });

  group('the address from the map', () {
    Future<(_FakeAddresses, List<ShippingAddress?>)> openWith(WidgetTester tester, _FakeLookup lookup,
            List<LatLng> picks) =>
        _openForMap(tester, lookup, picks);

    Future<void> locate(WidgetTester tester) async {
      await tester.tap(find.byKey(const ValueKey('address-locate')));
      await tester.pumpAndSettle();
    }

    String text(WidgetTester tester, String key) =>
        tester.widget<TextFormField>(find.byKey(ValueKey('address-$key'))).controller!.text;

    testWidgets('my current location fills city, district, street and building', (tester) async {
      final lookup = _FakeLookup([
        const FoundAddress(city: 'الرياض', district: 'العليا', street: 'طريق الملك فهد', buildingNumber: '2929'),
      ]);
      await openWith(tester, lookup, [const LatLng(24.69, 46.68)]);

      await locate(tester);

      expect(lookup.asked.single, const LatLng(24.69, 46.68));
      expect(text(tester, 'city'), 'الرياض');
      expect(text(tester, 'district'), 'العليا');
      expect(text(tester, 'street'), 'طريق الملك فهد');
      expect(text(tester, 'building'), '2929');
      expect(find.text(t.addressFilledFromMap), findsOneWidget);
    });

    testWidgets('a field the customer typed is kept; moving the pin updates what the map filled', (tester) async {
      final lookup = _FakeLookup([
        const FoundAddress(city: 'الرياض', district: 'العليا', street: 'طريق الملك فهد'),
        const FoundAddress(city: 'جدة', district: 'الروضة', street: 'شارع التحلية'),
      ]);
      await openWith(tester, lookup, [const LatLng(24.69, 46.68), const LatLng(21.55, 39.17)]);

      await tester.enterText(find.byKey(const ValueKey('address-street')), 'شارعي أنا');
      await locate(tester);
      expect(text(tester, 'street'), 'شارعي أنا', reason: 'typed by the customer');
      expect(text(tester, 'city'), 'الرياض');

      await locate(tester);
      expect(text(tester, 'city'), 'جدة', reason: 'filled by the map, so the map may change it');
      expect(text(tester, 'district'), 'الروضة');
      expect(text(tester, 'street'), 'شارعي أنا');
    });

    testWidgets('when the phone cannot tell, the customer is told to type it', (tester) async {
      final lookup = _FakeLookup([null]);
      await openWith(tester, lookup, [const LatLng(24.69, 46.68)]);

      await locate(tester);

      expect(find.text(t.addressNotFoundFromMap), findsOneWidget);
      expect(text(tester, 'city'), isEmpty);
    });
  });
}

Future<(_FakeAddresses, List<ShippingAddress?>)> _openForMap(
    WidgetTester tester, _FakeLookup lookup, List<LatLng> picks) async {
  final fake = _FakeAddresses();
  final popped = <ShippingAddress?>[];

  tester.view.physicalSize = const Size(1080, 3600);
  tester.view.devicePixelRatio = 2;
  addTearDown(tester.view.reset);

  await tester.pumpWidget(ProviderScope(
    overrides: [
      addressRepositoryProvider.overrideWithValue(fake),
      addressLookupProvider.overrideWithValue(lookup),
      locationPickerProvider.overrideWithValue(
          (context, {LatLng? initial, bool locate = false}) async => picks.removeAt(0)),
    ],
    child: MaterialApp(
      theme: AppTheme.light(),
      locale: const Locale('ar'),
      localizationsDelegates: const [
        AppStringsDelegate(),
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      supportedLocales: AppStrings.supportedLocales,
      home: const AddressFormScreen(recipientName: 'أحمد', recipientPhone: '0551234567'),
    ),
  ));
  await tester.pumpAndSettle();
  return (fake, popped);
}
