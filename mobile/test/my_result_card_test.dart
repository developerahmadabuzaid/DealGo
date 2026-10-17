import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/auctions/data/auction_models.dart';
import 'package:mobile/features/auctions/presentation/auction_results_screen.dart';

/// The user's own result once an auction is over: a place out of how many, whether they won.
void main() {
  AuctionDetail detail({int? myRank}) => AuctionDetail.fromJson({
        'id': 'a1',
        'auctionNumber': 'AUC-1',
        'title': 'مزاد',
        'status': 'Completed',
        'currency': 'SAR',
        'entryFee': 20,
        'participantCount': 8,
        'hasJoined': true,
        'myRank': myRank,
        'prizes': [],
      });

  final winners = [
    AuctionWinner.fromJson({'rank': 1, 'prizeName': 'آيفون', 'prizeValue': 2500, 'currency': 'SAR'}),
    AuctionWinner.fromJson({
      'rank': 2,
      'prizeName': 'سماعات',
      'prizeValue': 1200,
      'currency': 'SAR',
      'claimStatus': 'PaymentRequired',
    }),
  ];

  Future<void> pump(WidgetTester tester, Widget child) async {
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
      home: Scaffold(body: SingleChildScrollView(child: child)),
    ));
    await tester.pumpAndSettle();
  }

  testWidgets('a participant outside the prizes still learns their place', (tester) async {
    await pump(tester, MyResultCard(detail: detail(myRank: 6), winners: winners));

    expect(find.text('المركز 6 من 8'), findsOneWidget);
    expect(find.textContaining('رصيد تسوق'), findsOneWidget);
    expect(find.text('ادفع قيمة الجائزة'), findsNothing);
  });

  testWidgets('a winner sees the prize and how to pay for it', (tester) async {
    await pump(tester, MyResultCard(detail: detail(myRank: 2), winners: winners));

    expect(find.text('المركز 2 من 8'), findsOneWidget);
    expect(find.text('فزت بـ سماعات'), findsOneWidget);
    expect(find.text('ادفع قيمة الجائزة'), findsOneWidget);
  });
}
