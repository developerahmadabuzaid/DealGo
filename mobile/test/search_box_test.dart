import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/shared/app_header.dart';

/// The search box's hint, what is typed and the cursor sit in the middle of the white box, not
/// at its top (they used to be about 10 px high, partly above the box).
void main() {
  Future<void> show(WidgetTester tester, {double textScale = 1.0}) async {
    await tester.pumpWidget(ProviderScope(
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
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context).copyWith(textScaler: TextScaler.linear(textScale)),
          child: child!,
        ),
        home: Scaffold(appBar: AppBar(bottom: const HeaderSearchBar())),
      ),
    ));
    await tester.pumpAndSettle();
  }

  Rect boxOf(WidgetTester tester) =>
      tester.getRect(find.ancestor(of: find.byType(TextField), matching: find.byType(Container)).first);

  for (final scale in [1.0, 1.3]) {
    testWidgets('the hint sits in the middle of the box (text x$scale)', (tester) async {
      await show(tester, textScale: scale);

      final box = boxOf(tester);
      final hint = tester.getRect(find.text('ابحث عن منتج'));
      expect(hint.center.dy, closeTo(box.center.dy, 1));
      expect(hint.top, greaterThanOrEqualTo(box.top), reason: 'inside the box, not above it');
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('what is typed, and so the cursor, sits in the middle too', (tester) async {
    await show(tester);

    await tester.enterText(find.byType(TextField), 'ساعة');
    await tester.pump();

    final box = boxOf(tester);
    final line = tester.getRect(find.byType(EditableText));
    expect(line.center.dy, closeTo(box.center.dy, 1));
    expect(line.height, greaterThan(0));
  });
}
