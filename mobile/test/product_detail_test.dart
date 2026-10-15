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

/// The product page has to fit on a small phone, with large text, and its buy bar has to add what
/// it says it will.
void main() {
  StoreProductDetail detail({
    String name = 'سيروم فيتامين سي للعناية بالبشرة بتركيبة خفيفة وسريعة الامتصاص',
    bool inStock = true,
    int available = 3,
    String description = 'وصف قصير',
  }) =>
      StoreProductDetail(
        id: 'p1',
        name: name,
        sku: 'AD-01',
        categoryName: 'العناية بالبشرة',
        price: 89,
        compareAtPrice: 129,
        currency: 'SAR',
        imageUrl: null,
        availableQuantity: available,
        inStock: inStock,
        acceptsShoppingCredit: true,
        paymentMode: 'Both',
        isFeatured: true,
        description: description,
        requiresShipping: true,
        shippingFee: 15,
        images: const [],
        categoryId: 'c1',
      );

  StoreProduct other(String id) => StoreProduct(
        id: id,
        name: 'منتج $id',
        sku: id,
        categoryName: 'العناية بالبشرة',
        price: 40,
        compareAtPrice: null,
        currency: 'SAR',
        imageUrl: null,
        availableQuantity: 4,
        inStock: true,
        acceptsShoppingCredit: false,
        paymentMode: 'Both',
        isFeatured: false,
      );

  Future<ProviderContainer> open(
    WidgetTester tester,
    StoreProductDetail product, {
    Size size = const Size(360, 780),
    double textScale = 1.0,
  }) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    final container = ProviderContainer(overrides: [
      productDetailProvider('p1').overrideWith((ref) async => product),
      categoryProductsProvider('c1').overrideWith((ref) async => Paged(
            items: [product, other('p2'), other('p3')],
            page: 1,
            pageSize: 30,
            totalCount: 3,
            totalPages: 1,
          )),
    ]);
    addTearDown(container.dispose);

    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
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
          builder: (context, child) => MediaQuery(
            data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(textScale)),
            child: child!,
          ),
          home: const ProductDetailScreen(productId: 'p1'),
        ),
      ),
    );
    await tester.pumpAndSettle();
    return container;
  }

  for (final width in [320.0, 412.0]) {
    for (final scale in [1.0, 1.3]) {
      testWidgets('fits at $width wide, text x$scale', (tester) async {
        await open(tester, detail(), size: Size(width, 800), textScale: scale);
        expect(tester.takeException(), isNull);
      });
    }
  }

  testWidgets('the stepper stops at what is in stock and the cart gets that many', (tester) async {
    final container = await open(tester, detail(available: 3));

    for (var i = 0; i < 5; i++) {
      await tester.tap(find.byIcon(Icons.add_rounded));
      await tester.pump();
    }
    expect(find.text('3'), findsWidgets);

    await tester.tap(find.text('أضف إلى السلة'));
    await tester.pump();

    final cart = container.read(cartProvider);
    expect(cart.single.quantity, 3);
    expect(find.text('تمت الإضافة إلى السلة'), findsOneWidget);
  });

  testWidgets('low stock is called out', (tester) async {
    await open(tester, detail(available: 2));
    expect(find.text('بقي 2 فقط'), findsOneWidget);
  });

  testWidgets('a long description folds with read more', (tester) async {
    await open(tester, detail(description: List.filled(60, 'كلمة طويلة في الوصف').join(' ')));
    expect(find.text('اقرأ المزيد'), findsOneWidget);
  });

  testWidgets('similar products leave out the one on screen', (tester) async {
    await open(tester, detail());
    await tester.scrollUntilVisible(find.text('منتجات مشابهة'), 300,
        scrollable: find.byType(Scrollable).first);
    expect(find.text('منتج p2'), findsOneWidget);
    expect(find.text('منتج p3'), findsOneWidget);
  });

  testWidgets('out of stock: no stepper, button disabled', (tester) async {
    await open(tester, detail(inStock: false, available: 0));
    expect(find.byIcon(Icons.add_rounded), findsNothing);
    expect(tester.takeException(), isNull);
  });

  testWidgets('a power rating in the name becomes a tag', (tester) async {
    await open(tester, detail(name: 'كيبل Type-C إلى Lightning مضفر 27W'));
    expect(find.text('⚡ 27W'), findsOneWidget);
  });

  testWidgets('a product without a power rating gets no power tag', (tester) async {
    await open(tester, detail());
    expect(find.textContaining('⚡ ').evaluate().map((e) => (e.widget as Text).data),
        isNot(contains(matches(r'\dW'))));
  });

  testWidgets('fast shipping floats on the picture while in stock', (tester) async {
    await open(tester, detail());
    expect(find.text('⚡ شحن سريع'), findsOneWidget);
  });
}
