import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/providers.dart';
import 'package:mobile/features/store/data/store_repository.dart';
import 'package:mobile/features/store/presentation/exclusive_offers.dart';

Map<String, dynamic> _offerJson({int max = 2, Duration startedAgo = const Duration(hours: 1)}) {
  final now = DateTime.now().toUtc();
  return {
    'id': 'p1',
    'name': 'آيفون عرض',
    'sku': 'OFFER-1',
    'price': 50,
    'compareAtPrice': 80,
    'currency': 'SAR',
    'availableQuantity': 10,
    'inStock': true,
    'paymentMode': 'Both',
    'offer': {
      'startsAt': now.subtract(startedAgo).toIso8601String(),
      'endsAt': now.add(const Duration(hours: 5, minutes: 3)).toIso8601String(),
      'maxPerUser': max,
    },
  };
}

void main() {
  test('an offer is read with its times and limit', () {
    final p = StoreProduct.fromJson(_offerJson(max: 3));
    expect(p.offer, isNotNull);
    expect(p.offer!.maxPerUser, 3);
    expect(p.offer!.isLive, isTrue);
    expect(p.maxPerOrder, 3, reason: 'the offer limit, below the stock of 10');
    expect(p.discountPercent, 37);
  });

  test('an offer that has not started is not live', () {
    final p = StoreProduct.fromJson(_offerJson(startedAgo: const Duration(hours: -2)));
    expect(p.offer!.hasStarted, isFalse);
    expect(p.offer!.isLive, isFalse);
  });

  test('the cart never holds more of an offer than its limit', () {
    final cart = CartController();
    final p = StoreProduct.fromJson(_offerJson(max: 2));
    cart.add(p, quantity: 1);
    cart.add(p, quantity: 5);
    expect(cart.state.single.quantity, 2);
    cart.setQuantity(p.id, 9);
    expect(cart.state.single.quantity, 2);
  });

  testWidgets('the home row shows the offer with its clock', (tester) async {
    final p = StoreProduct.fromJson(_offerJson());
    await tester.pumpWidget(MaterialApp(
      locale: const Locale('ar'),
      supportedLocales: const [Locale('ar'), Locale('en')],
      localizationsDelegates: GlobalMaterialLocalizations.delegates,
      home: Scaffold(body: ExclusiveOffersSection(products: [p])),
    ));
    await tester.pump();

    expect(find.text('عروض حصرية'), findsOneWidget);
    expect(find.text('آيفون عرض'), findsOneWidget);
    expect(find.text('الحد الأقصى 2 لكل عميل'), findsOneWidget);
    expect(find.textContaining(RegExp(r'0[45]:\d\d:\d\d')), findsOneWidget);
  });
}
