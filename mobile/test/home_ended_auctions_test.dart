import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/auctions/data/auction_models.dart';
import 'package:mobile/features/auctions/presentation/ended_auction_screen.dart';
import 'package:mobile/features/home/presentation/home_screen.dart';

/// The home page's finished auctions: the last three, side by side, swiped like a carousel.
void main() {
  AuctionSummary ended(String id) => AuctionSummary.fromJson({
        'id': id,
        'auctionNumber': 'AUC-$id',
        'title': 'مزاد $id',
        'status': 'Completed',
        'currency': 'SAR',
        'requiredParticipants': 10,
        'prizes': [
          {'rank': 1, 'name': 'ساعة', 'originalPrice': 900},
          {'rank': 2, 'name': 'سماعات', 'originalPrice': 400},
        ],
      });

  Future<void> show(WidgetTester tester, List<AuctionSummary> auctions) async {
    tester.view.physicalSize = const Size(375, 812);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

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
      home: Scaffold(body: SingleChildScrollView(child: HomeEndedAuctions(auctions: auctions))),
    ));
    await tester.pumpAndSettle();
  }

  testWidgets('the three cards sit side by side, the next one peeking in', (tester) async {
    await show(tester, [ended('1'), ended('2'), ended('3')]);
    expect(tester.takeException(), isNull);

    final cards = find.byType(EndedAuctionCard);
    expect(cards, findsNWidgets(3));

    final first = tester.getRect(cards.at(0));
    final second = tester.getRect(cards.at(1));
    expect(first.top, second.top, reason: 'one row, not stacked');
    expect(first.width, lessThan(375), reason: 'narrower than the screen');
    // Right to left: the first card on the right, the second starting off the left edge.
    expect(second.right, lessThan(first.left));
    expect(second.right, greaterThan(0), reason: 'the next card peeks in');
  });

  testWidgets('a swipe brings the next one in', (tester) async {
    await show(tester, [ended('1'), ended('2'), ended('3')]);

    final cards = find.byType(EndedAuctionCard);
    final before = tester.getRect(cards.at(1)).left;
    await tester.drag(find.byKey(const ValueKey('home-ended-auctions')), const Offset(300, 0));
    await tester.pumpAndSettle();

    expect(tester.getRect(cards.at(1)).left, greaterThan(before));
  });

  testWidgets('a single finished auction takes the full width', (tester) async {
    await show(tester, [ended('1')]);
    expect(tester.getRect(find.byType(EndedAuctionCard)).width, closeTo(375 - 2 * AppTheme.space3, 0.5));
  });
}
