import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/auctions/data/auction_models.dart';
import 'package:mobile/features/auctions/presentation/auction_end_dialog.dart';

/// The message an auction leaves the moment it closes: congratulations, or better luck next time.
void main() {
  AuctionSnapshot snapshot(String status, {required bool meFirst}) => AuctionSnapshot.fromJson({
        'id': 'a1',
        'status': status,
        'currentPrice': 403,
        'currency': 'SAR',
        'leaderboard': [
          {'rank': 1, 'displayName': meFirst ? 'Ahmad A.' : 'Marwan A.', 'bidCount': 3, 'isMe': meFirst},
          for (var r = 2; r <= 5; r++)
            {'rank': r, 'displayName': 'User $r', 'bidCount': 1, 'isMe': false},
          {'rank': 6, 'displayName': 'Ahmad A.', 'bidCount': 2, 'isMe': !meFirst},
        ],
      });

  Future<void> run(WidgetTester tester, AuctionSnapshot before, AuctionSnapshot now) async {
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
      home: Builder(
        builder: (context) => Scaffold(
          body: TextButton(
            onPressed: () => announceAuctionEnd(context, before: before, now: now),
            child: const Text('go'),
          ),
        ),
      ),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.text('go'));
    await tester.pumpAndSettle();
  }

  testWidgets('everyone outside the prizes is told who won and wished better luck', (tester) async {
    await run(tester, snapshot('Live', meFirst: false), snapshot('Finished', meFirst: false));

    expect(find.text('انتهى المزاد'), findsOneWidget);
    expect(find.textContaining('مبروك لـ'), findsOneWidget);
    expect(find.textContaining('Marwan A.'), findsOneWidget);
    expect(find.text('انتهى هذا المزاد، حظاً أوفر في المزاد القادم'), findsOneWidget);

    await tester.tap(find.text('تأكيد'));
    await tester.pumpAndSettle();
    expect(find.text('انتهى المزاد'), findsNothing);
  });

  testWidgets('the winner is congratulated and pointed at their prize', (tester) async {
    await run(tester, snapshot('FinalPhase', meFirst: true), snapshot('Finished', meFirst: true));

    expect(find.text('لقد فزت بالمزاد'), findsOneWidget);
    expect(find.text('مبروك! 🎉'), findsOneWidget);
    expect(find.textContaining('403.00'), findsOneWidget);
    expect(find.text('جوائزي'), findsOneWidget);
    expect(find.textContaining('حظاً أوفر'), findsNothing);
  });

  testWidgets('an auction that was already over says nothing', (tester) async {
    await run(tester, snapshot('Finished', meFirst: true), snapshot('Finished', meFirst: true));

    expect(find.text('لقد فزت بالمزاد'), findsNothing);
    expect(find.text('انتهى المزاد'), findsNothing);
  });

  testWidgets('a cancelled auction is not announced as won or lost', (tester) async {
    await run(tester, snapshot('Live', meFirst: true), snapshot('Cancelled', meFirst: true));

    expect(find.text('لقد فزت بالمزاد'), findsNothing);
    expect(find.text('انتهى المزاد'), findsNothing);
  });
}
