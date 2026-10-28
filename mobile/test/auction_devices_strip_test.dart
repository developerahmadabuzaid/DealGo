import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/auctions/data/auction_models.dart';
import 'package:mobile/features/auctions/presentation/auctions_screen.dart';
import 'package:mobile/features/auctions/presentation/ended_auction_screen.dart';

/// Every auction card, running, upcoming or finished, shows the four devices under it.
void main() {
  AuctionSummary auction(String status) => AuctionSummary.fromJson({
        'id': 'a1',
        'auctionNumber': 'AUC-1',
        'title': 'مزاد',
        'status': status,
        'currency': 'SAR',
        'requiredParticipants': 10,
        // Sent out of order on purpose: the card shows them by place.
        'prizes': [
          {'rank': 3, 'name': 'شاحن', 'originalPrice': 100},
          {'rank': 1, 'name': 'ساعة', 'originalPrice': 900},
          {'rank': 4, 'name': 'كابل', 'originalPrice': 50},
          {'rank': 2, 'name': 'سماعات', 'originalPrice': 400},
        ],
      });

  Future<void> show(WidgetTester tester, Widget card) async {
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
      home: Scaffold(body: SingleChildScrollView(child: card)),
    ));
    await tester.pumpAndSettle();
  }

  void expectDevicesInOrder(WidgetTester tester) {
    final names = ['ساعة', 'سماعات', 'شاحن', 'كابل'];
    for (final n in names) {
      expect(find.text(n), findsOneWidget, reason: n);
    }
    // Right to left in Arabic: first place is furthest right.
    final xs = [for (final n in names) tester.getCenter(find.text(n)).dx];
    expect(xs, orderedEquals([...xs]..sort((a, b) => b.compareTo(a))));
  }

  testWidgets('a live auction card lists its four devices by place', (tester) async {
    await show(tester, AuctionCard(auction: auction('Live')));
    expectDevicesInOrder(tester);
  });

  testWidgets('an upcoming auction card lists them too', (tester) async {
    await show(tester, AuctionCard(auction: auction('WaitingForParticipants')));
    expectDevicesInOrder(tester);
  });

  testWidgets('a finished auction card lists them too', (tester) async {
    await show(tester, EndedAuctionCard(auction: auction('Completed')));
    expectDevicesInOrder(tester);
  });
}
