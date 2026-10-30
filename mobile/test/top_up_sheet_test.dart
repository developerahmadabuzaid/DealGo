import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/providers.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/wallets/data/wallet_repository.dart';
import 'package:mobile/features/wallets/presentation/top_up_sheet.dart';

/// The server's side of a top-up, scripted: a checkout to open, then whatever status is set.
class _FakeWallets implements WalletRepository {
  double? started;
  String status = 'Processing';

  @override
  Future<TopUp> startTopUp(double amount) async {
    started = amount;
    return TopUp(paymentId: 'p1', amount: amount, currency: 'SAR', checkoutUrl: 'https://checkout.test/p1');
  }

  @override
  Future<TopUpStatus> topUpStatus(String paymentId) async => TopUpStatus(status: status);

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  Future<_FakeWallets> open(WidgetTester tester) async {
    final fake = _FakeWallets();
    await tester.pumpWidget(ProviderScope(
      overrides: [walletRepositoryProvider.overrideWithValue(fake)],
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
        home: Builder(
          builder: (context) => Scaffold(
            body: TextButton(
              onPressed: () => showModalBottomSheet<void>(
                context: context,
                isScrollControlled: true,
                builder: (_) => const TopUpSheet(currency: 'SAR'),
              ),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    return fake;
  }

  testWidgets('an amount under the minimum is not sent to the server', (tester) async {
    final fake = await open(tester);

    await tester.enterText(find.byType(TextField), '5');
    await tester.tap(find.text('ادفع الآن'));
    await tester.pumpAndSettle();

    expect(fake.started, isNull);
    expect(find.textContaining('أقل مبلغ للشحن'), findsOneWidget);
  });

  testWidgets('the sheet waits for the server, and closes once the payment is confirmed', (tester) async {
    final fake = await open(tester);

    await tester.tap(find.byType(ActionChip).at(1)); // 100
    await tester.pump();
    await tester.tap(find.text('ادفع الآن'));
    await tester.pump();
    await tester.pump();

    expect(fake.started, 100);
    expect(find.text('بانتظار تأكيد الدفع…'), findsOneWidget);

    // Still processing: nothing changes.
    await tester.pump(const Duration(seconds: 5));
    expect(find.text('بانتظار تأكيد الدفع…'), findsOneWidget);

    // Paymob confirms to the server; the next check sees it.
    fake.status = 'Completed';
    await tester.pump(const Duration(seconds: 5));
    await _settle(tester);

    expect(find.byType(TopUpSheet), findsNothing);
    expect(find.textContaining('تم شحن محفظتك'), findsOneWidget);
  });

  testWidgets('a failed payment says nothing was charged and offers to try again', (tester) async {
    final fake = await open(tester);

    await tester.tap(find.byType(ActionChip).first);
    await tester.pump();
    await tester.tap(find.text('ادفع الآن'));
    await tester.pump();
    await tester.pump();

    fake.status = 'Failed';
    await tester.pump(const Duration(seconds: 5));
    await _settle(tester);

    expect(find.textContaining('لم يُخصم أي مبلغ'), findsOneWidget);
    expect(find.text('المحاولة مرة أخرى'), findsOneWidget);
  });
}

/// Moves the clock on in slices. The waiting spinner never stops, so pumpAndSettle never would.
Future<void> _settle(WidgetTester tester) async {
  for (var i = 0; i < 20; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}
