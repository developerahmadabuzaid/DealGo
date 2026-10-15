import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/store/data/store_repository.dart';
import 'package:mobile/features/store/presentation/store_screen.dart';

/// The shop's tiles have to fit what is written on them.
///
/// Their height used to come from an aspect ratio, so the band left under the picture for the name
/// and the prices followed the card's *width* — while the writing underneath is the same height
/// whatever the width. A phone narrow enough for four columns gave the text less room than one
/// with three, and the tile overflowed, which a customer sees as a black and yellow bar across the
/// shop.
///
/// These do catch things: writing them turned up a second bug straight away — the "out of stock"
/// pill was 75 pixels wider than a narrow tile and ran off the side of every one of them.
void main() {
  StoreProduct product({
    required String name,
    double price = 1310,
    double? compareAtPrice = 1541.18,
    bool inStock = true,
    bool credit = true,
  }) {
    return StoreProduct(
      id: 'p1',
      name: name,
      sku: 'SKU-1',
      categoryName: 'إلكترونيات',
      price: price,
      compareAtPrice: compareAtPrice,
      currency: 'SAR',
      imageUrl: null,
      availableQuantity: 5,
      inStock: inStock,
      acceptsShoppingCredit: credit,
      paymentMode: 'Both',
      isFeatured: false,
    );
  }

  /// Lays one tile out at an exact size and says whether it complained.
  Future<Object?> layOut(
    WidgetTester tester,
    StoreProduct item, {
    required double width,
    double height = ProductCard.tileHeight,
    double textScale = 1.0,
  }) async {
    await tester.pumpWidget(
      ProviderScope(
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
          home: MediaQuery(
            data: MediaQueryData(textScaler: TextScaler.linear(textScale)),
            child: Scaffold(
              body: Center(
                child: SizedBox(
                  width: width,
                  height: height,
                  child: ProductCard(product: item),
                ),
              ),
            ),
          ),
        ),
      ),
    );

    return tester.takeException();
  }

  /// Every width the grid can hand a tile. `maxCrossAxisExtent: 124` with the gutters and spacing
  /// this app uses lands between about 90 and 124 on the phones it runs on.
  const widths = [90.0, 94.0, 104.0, 114.0, 124.0];

  const longName = 'هاتف بكاميرا 64 ميجابكسل من الجيل الجديد مع ذاكرة كبيرة';

  testWidgets('a tile fits at every width the grid produces', (tester) async {
    for (final width in widths) {
      expect(
        await layOut(tester, product(name: longName), width: width),
        isNull,
        reason: 'overflowed at width $width',
      );
    }
  });

  testWidgets('a tile fits when the phone is set to larger text', (tester) async {
    for (final scale in [1.15, 1.3]) {
      expect(
        await layOut(tester, product(name: longName), width: 94, textScale: scale),
        isNull,
        reason: 'overflowed at text scale $scale',
      );
    }
  });

  testWidgets('a tile fits whatever it is or is not offering', (tester) async {
    final variants = [
      product(name: longName),
      product(name: longName, compareAtPrice: null),
      product(name: longName, credit: false),
      product(name: longName, compareAtPrice: null, credit: false),
      product(name: longName, inStock: false),
      product(name: 'جهاز'),
    ];

    for (final item in variants) {
      expect(await layOut(tester, item, width: 94), isNull, reason: item.name);
    }
  });

}
