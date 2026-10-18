import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/errors/api_exception.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/shared/auto_refresh.dart';

void main() {
  testWidgets('a list refreshes the moment the user comes back to it', (tester) async {
    var refreshes = 0;
    final navigator = GlobalKey<NavigatorState>();

    await tester.pumpWidget(MaterialApp(
      navigatorKey: navigator,
      home: AutoRefresh(
        // Long enough that the timer cannot be what refreshes it here.
        interval: const Duration(hours: 1),
        onRefresh: () => refreshes++,
        child: const Text('auctions'),
      ),
    ));
    await tester.pumpAndSettle();
    expect(refreshes, 0);

    // An auction page opened on top, then closed again.
    navigator.currentState!.push(MaterialPageRoute<void>(builder: (_) => const Text('auction')));
    await tester.pumpAndSettle();
    expect(refreshes, 0, reason: 'a screen under another one costs no requests');

    navigator.currentState!.pop();
    await tester.pumpAndSettle();
    expect(refreshes, 1);
  });

  test('a failure the app has no wording for is still shown in Arabic', () {
    const unknown = ApiException(code: 'SOMETHING_NEW', message: 'Something went wrong.');
    expect(unknown.displayMessage, AppStrings.genericError);

    const known = ApiException(code: ErrorCodes.rateLimited, message: 'Too many requests.');
    expect(known.displayMessage, contains('محاولات كثيرة'));
  });
}
