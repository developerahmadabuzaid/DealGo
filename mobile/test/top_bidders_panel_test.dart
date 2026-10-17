import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/format/formatters.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/auctions/data/auction_models.dart';
import 'package:mobile/features/auctions/presentation/top_bidders_panel.dart';

/// The auction page's leading-bidders panel.
void main() {
  LeaderboardEntry entry(int rank, String name, {int bids = 3, bool me = false}) =>
      LeaderboardEntry(
        rank: rank,
        participantId: 'p$rank',
        displayName: name,
        highestPrice: 2000 - rank * 20.0,
        bidCount: bids,
        isMe: me,
      );

  Future<void> pump(WidgetTester tester, List<LeaderboardEntry> entries) async {
    await tester.pumpWidget(MaterialApp(
      theme: AppTheme.light(),
      locale: const Locale('ar'),
      localizationsDelegates: const [
        AppStringsDelegate(),
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      supportedLocales: AppStrings.supportedLocales,
      home: Scaffold(
        body: SingleChildScrollView(
          child: TopBiddersPanel(entries: entries, currency: 'SAR', participantCount: 12),
        ),
      ),
    ));
    // The strings load asynchronously; nothing is on screen until they have.
    await tester.pumpAndSettle();
  }

  testWidgets('lists only people who have bid', (tester) async {
    await pump(tester, [entry(1, 'سارة'), entry(2, 'خالد', bids: 0)]);

    expect(find.text(Fmt.name('سارة')), findsOneWidget);
    expect(find.text(Fmt.name('خالد')), findsNothing);
  });

  testWidgets('says so when nobody has bid', (tester) async {
    await pump(tester, [entry(1, 'سارة', bids: 0)]);

    expect(find.text('لم يزايد أحد بعد. كن أول المزايدين!'), findsOneWidget);
  });

  testWidgets('shows the first five, keeps my row, and expands on request', (tester) async {
    final entries = [
      for (var i = 1; i <= 8; i++) entry(i, 'مزايد $i', me: i == 7),
    ];
    await pump(tester, entries);

    expect(find.text(Fmt.name('مزايد 5')), findsOneWidget);
    expect(find.text(Fmt.name('مزايد 6')), findsNothing);
    expect(find.text(Fmt.name('مزايد 7')), findsOneWidget, reason: 'my own row stays visible');
    expect(find.text('حدود المراكز الفائزة'), findsOneWidget);

    await tester.tap(find.text('عرض جميع المزايدين (8)'));
    await tester.pumpAndSettle();

    expect(find.text(Fmt.name('مزايد 6')), findsOneWidget);
    expect(find.text(Fmt.name('مزايد 8')), findsOneWidget);
  });
}
