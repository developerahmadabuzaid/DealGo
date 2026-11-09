import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/network/api_client.dart';
import 'package:mobile/core/providers.dart';
import 'package:mobile/core/storage/token_store.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/store/data/store_repository.dart';
import 'package:mobile/features/store/presentation/store_screen.dart';

/// A shop with [total] products in one department, served a page at a time.
class _Store extends StoreRepository {
  _Store(this.total) : super(ApiClient(tokenStore: TokenStore()));

  final int total;
  final asked = <int>[];

  @override
  Future<Paged<StoreProduct>> products({
    String? search,
    String? categoryId,
    bool? shoppingCreditOnly,
    bool onOfferOnly = false,
    String? sortBy,
    int page = 1,
    int pageSize = 20,
  }) async {
    if (onOfferOnly) {
      return Paged(items: const [], page: 1, pageSize: pageSize, totalCount: 0, totalPages: 0);
    }

    asked.add(page);
    final first = (page - 1) * pageSize;
    final last = (first + pageSize).clamp(0, total);
    return Paged(
      items: [
        for (var i = first; i < last; i++)
          StoreProduct.fromJson({'id': 'p$i', 'name': 'منتج $i', 'price': 10, 'currency': 'SAR'}),
      ],
      page: page,
      pageSize: pageSize,
      totalCount: total,
      totalPages: (total / pageSize).ceil(),
    );
  }
}

const _department = StoreCategory(id: 'c1', name: 'عطور', imageUrl: null, productCount: 75);

void main() {
  Future<void> show(WidgetTester tester, _Store store, Widget screen) async {
    tester.view.physicalSize = const Size(400, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(ProviderScope(
      overrides: [
        storeRepositoryProvider.overrideWithValue(store),
        appAdsProvider.overrideWith((ref) async => []),
        storeHomeProvider.overrideWith((ref) async => const StoreHome(
              categories: [_department],
              featured: [],
              newArrivals: [],
              popular: [],
              creditEligible: [],
              offers: [],
              shoppingCreditBalance: 0,
              currency: 'SAR',
            )),
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
        home: screen,
      ),
    ));
    await tester.pump();
    await tester.pump();
  }

  /// Scrolls until [name] is on screen, the way a customer keeps going down the list.
  Future<void> scrollTo(WidgetTester tester, String name) async {
    for (var i = 0; i < 80 && find.text(name).evaluate().isEmpty; i++) {
      await tester.drag(find.byType(CustomScrollView).first, const Offset(0, -700));
      await tester.pump();
      await tester.pump();
    }
    expect(find.text(name), findsOneWidget);
    expect(tester.takeException(), isNull);
  }

  testWidgets('the See all list reaches the last product', (tester) async {
    final store = _Store(95);
    await show(tester, store, const StoreProductsScreen(categoryId: 'c1'));

    expect(store.asked, [1], reason: 'only what is needed at first');

    await scrollTo(tester, 'منتج 94');
    expect(store.asked, [1, 2, 3]);
  });

  testWidgets('a department in the store tab reaches the last product', (tester) async {
    final store = _Store(75);
    await show(tester, store, const StoreScreen(categoryId: 'c1'));

    await scrollTo(tester, 'منتج 74');
    expect(store.asked, [1, 2, 3]);
  });

  testWidgets('a list that fits on one page never asks for another', (tester) async {
    final store = _Store(6);
    await show(tester, store, const StoreProductsScreen(categoryId: 'c1'));
    await tester.pump();

    expect(find.text('منتج 5'), findsOneWidget);
    expect(store.asked, [1]);
  });
}
