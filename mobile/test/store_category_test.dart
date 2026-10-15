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

/// Opening the shop at a department.
///
/// Tapping a department on the front page used to push a flat list above the tabs — no departments
/// to move between, no banner, a back arrow instead of the shop. It now opens the shop itself at
/// `/store?categoryId=…`, so the screen has to honour that.
///
/// It also has to stop honouring it the moment somebody starts moving around inside the shop: this
/// tab is kept alive behind the others and rebuilt for all sorts of reasons, and re-reading the
/// address on every rebuild would keep snapping them back to where they came in.
void main() {
  const electronics =
      StoreCategory(id: 'cat-electronics', name: 'إلكترونيات', imageUrl: null, productCount: 12);
  const kitchen =
      StoreCategory(id: 'cat-kitchen', name: 'المطبخ', imageUrl: null, productCount: 7);

  const home = StoreHome(
    categories: [electronics, kitchen],
    featured: [],
    newArrivals: [],
    popular: [],
    creditEligible: [],
    offers: [],
    shoppingCreditBalance: 0,
    currency: 'SAR',
  );

  /// The department's own section carries its id as a key, so finding it is the same question as
  /// "is the shop showing that department".
  Finder section(StoreCategory c) => find.byKey(ValueKey(c.id));

  Future<void> open(WidgetTester tester, String? categoryId) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          storeHomeProvider.overrideWith((ref) async => home),
          categoryProductsProvider.overrideWith(
            (ref, String id) async => Paged.empty<StoreProduct>(),
          ),
          // The whole-shop view carries the advertisement strip, which would otherwise reach for
          // the network and leave the test waiting on a real request.
          appAdsProvider.overrideWith((ref) async => const []),
          // A department's banner reads its offers.
          categoryOffersProvider.overrideWith((ref, String id) async => Paged.empty<StoreProduct>()),
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

    // Two pumps: one to build, one for the overridden futures to resolve into the tree.
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));
  }

  testWidgets('arriving with a department opens the shop on it', (tester) async {
    await open(tester, electronics.id);

    expect(section(electronics), findsOneWidget);
    expect(section(kitchen), findsNothing);
  });

  testWidgets('arriving with no department opens the whole shop', (tester) async {
    await open(tester, null);

    expect(section(electronics), findsNothing);
    expect(section(kitchen), findsNothing);
  });

  testWidgets('arriving at a different department moves the shop to it', (tester) async {
    await open(tester, electronics.id);
    await open(tester, kitchen.id);

    expect(section(kitchen), findsOneWidget);
    expect(section(electronics), findsNothing);
  });

  /// The one that stops the tab fighting the person using it.
  testWidgets('a rebuild at the same address leaves their own choice alone', (tester) async {
    await open(tester, electronics.id);

    // As if they tapped another department inside the shop.
    await tester.tap(find.text(kitchen.name).first);
    await tester.pump();
    expect(section(kitchen), findsOneWidget);

    // The shop is rebuilt at the address it was opened on, which is what happens to a tab that is
    // kept alive behind the others.
    await open(tester, electronics.id);

    expect(
      section(kitchen),
      findsOneWidget,
      reason: 'their own choice should survive a rebuild that changes nothing',
    );
  });
}
