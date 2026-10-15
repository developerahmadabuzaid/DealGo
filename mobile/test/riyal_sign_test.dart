import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/theme/app_theme.dart';

/// The Riyal sign is drawn from a small font of its own. A button's text style replaces the
/// theme's, so a button that does not name that font again shows the sign as a broken box — which
/// is what customers saw on the orange "add to cart" button.
void main() {
  for (final entry in {'light': AppTheme.light(), 'dark': AppTheme.dark()}.entries) {
    test('every button style can draw the Riyal sign (${entry.key})', () {
      final theme = entry.value;
      final styles = <String, ButtonStyle?>{
        'filled': theme.filledButtonTheme.style,
        'outlined': theme.outlinedButtonTheme.style,
        'text': theme.textButtonTheme.style,
      };

      for (final style in styles.entries) {
        final text = style.value?.textStyle?.resolve({});
        if (text == null) continue;
        expect(text.fontFamilyFallback, contains('RiyalSign'), reason: '${style.key} button');
      }
    });
  }
}
