import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/auctions/data/auction_policy.dart';
import 'package:mobile/features/auctions/presentation/auction_policy_sheet.dart';

/// Joining an auction waits on the policies sheet: it says yes only after the box is ticked.
void main() {
  const policy = AuctionPolicy(
    title: 'سياسات المزاد',
    body: 'الفائزون\nعلى الفائز دفع قيمة الجهاز خلال 24 ساعة.',
    version: 3,
    required: true,
  );

  Future<List<bool>> open(WidgetTester tester) async {
    final results = <bool>[];
    await tester.pumpWidget(MaterialApp(
      theme: AppTheme.light(),
      locale: const Locale('ar'),
      localizationsDelegates: const [
        AppStringsDelegate(),
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      supportedLocales: const [Locale('ar'), Locale('en')],
      home: Builder(
        builder: (context) => Scaffold(
          body: TextButton(
            onPressed: () async => results.add(await confirmAuctionPolicy(context, policy)),
            child: const Text('join'),
          ),
        ),
      ),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.text('join'));
    await tester.pumpAndSettle();
    return results;
  }

  testWidgets('shows the policies and accepts only once the box is ticked', (tester) async {
    final results = await open(tester);

    expect(find.text('الفائزون'), findsOneWidget);
    expect(find.text('على الفائز دفع قيمة الجهاز خلال 24 ساعة.'), findsOneWidget);

    final accept = find.byKey(const Key('auction-policy-accept'));
    expect(tester.widget<FilledButton>(accept).onPressed, isNull);

    await tester.tap(find.byKey(const Key('auction-policy-agree')));
    await tester.pump();
    await tester.tap(accept);
    await tester.pumpAndSettle();

    expect(results, [true]);
  });

  testWidgets('closing the sheet without accepting does not join', (tester) async {
    final results = await open(tester);

    await tester.tapAt(const Offset(10, 10));
    await tester.pumpAndSettle();

    expect(results, [false]);
  });
}
