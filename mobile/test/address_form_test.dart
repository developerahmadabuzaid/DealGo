import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/addresses/data/address_repository.dart';
import 'package:mobile/features/addresses/presentation/address_form_screen.dart';

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

void main() {
  const t = AppStrings(Locale('ar'));

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
        {ShippingAddress? existing, String? name, String? phone}) async {
      final fake = _FakeAddresses();
      final popped = <ShippingAddress?>[];

      // Tall enough that every field is built and can be typed into.
      tester.view.physicalSize = const Size(1080, 3600);
      tester.view.devicePixelRatio = 2;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(ProviderScope(
        overrides: [addressRepositoryProvider.overrideWithValue(fake)],
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
}
