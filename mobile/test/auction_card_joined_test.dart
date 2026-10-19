import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/auctions/data/auction_models.dart';
import 'package:mobile/features/auctions/presentation/auctions_screen.dart';

/// An upcoming auction the user has joined says "مشترك" rather than "upcoming".
void main() {
  AuctionSummary upcoming({required bool joined}) => AuctionSummary.fromJson({
        'id': 'a1',
        'auctionNumber': 'AUC-1',
        'title': 'مزاد',
        'status': 'WaitingForParticipants',
        'currency': 'SAR',
        'participantCount': 3,
        'requiredParticipants': 10,
        'hasJoined': joined,
      });

  Future<void> show(WidgetTester tester, AuctionSummary auction) async {
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
      home: Scaffold(body: SingleChildScrollView(child: AuctionCard(auction: auction))),
    ));
    await tester.pumpAndSettle();
  }

  testWidgets('a joined upcoming auction is marked as joined', (tester) async {
    await show(tester, upcoming(joined: true));
    expect(find.text('مشترك'), findsOneWidget);
    expect(find.text('مزادات قادمة'), findsNothing);
  });

  testWidgets('an upcoming auction the user is not in stays upcoming', (tester) async {
    await show(tester, upcoming(joined: false));
    expect(find.text('مزادات قادمة'), findsOneWidget);
    expect(find.text('مشترك'), findsNothing);
  });
}
