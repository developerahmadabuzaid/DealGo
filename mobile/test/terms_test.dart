import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/auth/presentation/complete_profile_screen.dart';
import 'package:mobile/features/legal/terms_document.dart';

void main() {
  test('the shipped terms read as a title and fourteen headed sections', () {
    final doc = TermsDocument.parse(File(TermsDocument.asset).readAsStringSync());

    expect(doc.title, 'الشروط والأحكام وسياسات منصة ديلقو (DealGo)');
    expect(doc.sections, hasLength(14));
    expect(doc.sections.first.heading, 'التعريف بالمنصة وقبول الشروط');
    expect(doc.sections.last.heading, 'الأحكام العامة');
    expect(doc.sections.map((s) => s.heading), contains(TermsDocument.privacyHeading));
    // Every section has its paragraphs, and every paragraph is a sentence.
    for (final s in doc.sections) {
      expect(s.paragraphs, isNotEmpty, reason: s.heading);
      expect(s.paragraphs.every((p) => p.endsWith('.')), isTrue, reason: s.heading);
    }
  });

  testWidgets('a new account cannot continue until the terms are accepted', (tester) async {
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
        home: const CompleteProfileScreen(),
      ),
    ));
    await tester.pumpAndSettle();

    FilledButton button() => tester.widget<FilledButton>(find.byType(FilledButton));

    await tester.enterText(find.byType(TextField), 'أحمد');
    await tester.pump();
    expect(button().onPressed, isNull, reason: 'a name alone is not enough');

    await tester.tap(find.byType(Checkbox));
    await tester.pump();
    expect(button().onPressed, isNotNull);
  });
}
