import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../core/localization/app_strings.dart';
import '../features/legal/terms_document.dart';

/// Opens the terms and policies inside the app. The privacy policy is a section of the same
/// document, so [privacy] opens it at that section.
void openTerms(BuildContext context, {bool privacy = false}) {
  context.push(Uri(
    path: '/terms',
    queryParameters: privacy ? {'section': TermsDocument.privacyHeading} : null,
  ).toString());
}

/// "By continuing you agree to the terms of use and the privacy policy", with both linked.
///
/// Shown under the sign-in button: an account is created on first sign-in, so this is the moment
/// the terms are accepted and the person must be able to read them before going on.
class LegalConsentText extends StatefulWidget {
  const LegalConsentText({super.key});

  @override
  State<LegalConsentText> createState() => _LegalConsentTextState();
}

class _LegalConsentTextState extends State<LegalConsentText> {
  // Recognizers own gesture state and must be disposed with the widget.
  late final _terms = TapGestureRecognizer()..onTap = () => openTerms(context);
  late final _privacy = TapGestureRecognizer()..onTap = () => openTerms(context, privacy: true);

  @override
  void dispose() {
    _terms.dispose();
    _privacy.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final base = theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurfaceVariant);
    final link = base?.copyWith(
      color: theme.colorScheme.primary,
      fontWeight: FontWeight.w700,
      decoration: TextDecoration.underline,
    );

    return Text.rich(
      TextSpan(style: base, children: [
        TextSpan(text: '${t.agreeToTermsPrefix} '),
        TextSpan(text: t.termsOfUse, style: link, recognizer: _terms),
        TextSpan(text: ' ${t.and}'),
        TextSpan(text: t.privacyPolicy, style: link, recognizer: _privacy),
      ]),
      textAlign: TextAlign.center,
    );
  }
}
