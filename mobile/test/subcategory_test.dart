import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/network/api_client.dart';
import 'package:mobile/core/providers.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/store/data/store_repository.dart';
import 'package:mobile/features/store/presentation/store_screen.dart';

/// Departments in two levels: كوابل شحن, then كوابل شحن سيارة inside it, then the products.
void main() {
  const cables = StoreCategory(id: 'cables', name: 'كوابل شحن', imageUrl: null, productCount: 3);
  const car = StoreCategory(
      id: 'car', name: 'كوابل شحن سيارة', imageUrl: null, productCount: 1,
      parentId: 'cables', parentName: 'كوابل شحن');
  const iphone = StoreCategory(
      id: 'iphone', name: 'كوابل آيفون', imageUrl: null, productCount: 1,
      parentId: 'cables', parentName: 'كوابل شحن');
  const kitchen = StoreCategory(id: 'kitchen', name: 'المطبخ', imageUrl: null, productCount: 2);

  const home = StoreHome(
    categories: [cables, car, iphone, kitchen],
    featured: [],
    newArrivals: [],
    popular: [],
    creditEligible: [],
    offers: [],
    shoppingCreditBalance: 0,
    currency: 'SAR',
  );

  /// Each department's list holds one product named after it, so the name on screen says which
  /// list is showing.
  String productIn(String categoryId) => 'منتج $categoryId';

  Future<void> open(WidgetTester tester, String? categoryId) async {
    tester.view.physicalSize = const Size(800, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          storeHomeProvider.overrideWith((ref) async => home),
          categoryProductsProvider.overrideWith((ref, String id) async => Paged(
                items: [
                  StoreProduct(
                    id: 'p-$id', name: productIn(id), sku: id, categoryName: null, price: 10,
                    compareAtPrice: null, currency: 'SAR', imageUrl: null, availableQuantity: 3,
                    inStock: true, acceptsShoppingCredit: false, paymentMode: 'Both',
                    isFeatured: false),
                ],
                page: 1,
                pageSize: 30,
                totalCount: 1,
                totalPages: 1,
              )),
          categoryOffersProvider.overrideWith((ref, String id) async => Paged.empty<StoreProduct>()),
          appAdsProvider.overrideWith((ref) async => const []),
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
          supportedLocales: const [Locale('ar'), Locale('en')],
          home: StoreScreen(categoryId: categoryId),
        ),
      ),
    );
    // The shop loads, then the department's products: a frame for each.
    for (var i = 0; i < 3; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
  }

  testWidgets('the store lists main departments only', (tester) async {
    await open(tester, null);

    expect(find.text(cables.name), findsWidgets);
    expect(find.text(kitchen.name), findsWidgets);
    expect(find.text(car.name), findsNothing);
    expect(find.text(iphone.name), findsNothing);
  });

  testWidgets('a main department shows its sub-departments as chips, and all its products',
      (tester) async {
    await open(tester, cables.id);

    expect(find.widgetWithText(ChoiceChip, 'الكل'), findsOneWidget);
    expect(find.widgetWithText(ChoiceChip, car.name), findsOneWidget);
    expect(find.widgetWithText(ChoiceChip, iphone.name), findsOneWidget);
    expect(find.text(productIn(cables.id)), findsOneWidget);
  });

  testWidgets('tapping a sub-department shows only its products', (tester) async {
    await open(tester, cables.id);

    await tester.tap(find.widgetWithText(ChoiceChip, car.name));
    for (var i = 0; i < 3; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(find.text(productIn(car.id)), findsOneWidget);
    expect(find.text(productIn(cables.id)), findsNothing);

    // And back to all of it.
    await tester.tap(find.widgetWithText(ChoiceChip, 'الكل'));
    for (var i = 0; i < 3; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
    expect(find.text(productIn(cables.id)), findsOneWidget);
  });

  testWidgets('arriving at a sub-department opens its main department with it chosen',
      (tester) async {
    await open(tester, car.id);

    expect(find.byKey(const ValueKey('cables')), findsOneWidget);
    final chip = tester.widget<ChoiceChip>(find.widgetWithText(ChoiceChip, car.name));
    expect(chip.selected, isTrue);
    expect(find.text(productIn(car.id)), findsOneWidget);
  });

  testWidgets('each chip shows how many products it holds', (tester) async {
    await open(tester, cables.id);

    Finder countOn(String chipLabel, String count) => find.descendant(
        of: find.widgetWithText(ChoiceChip, chipLabel), matching: find.text(count));

    expect(countOn('الكل', '3'), findsOneWidget);
    expect(countOn(car.name, '1'), findsOneWidget);
    expect(countOn(iphone.name, '1'), findsOneWidget);
  });

  testWidgets('a department without sub-departments shows no chips', (tester) async {
    await open(tester, kitchen.id);
    expect(find.byType(ChoiceChip), findsNothing);
  });
}
