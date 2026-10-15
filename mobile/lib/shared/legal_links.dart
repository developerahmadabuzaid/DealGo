import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/config/app_config.dart';
import '../core/localization/app_strings.dart';

/// Opens one of the policy pages in the browser, and says so when nothing could open it.
Future<void> openLegalPage(BuildContext context, String address) async {
  try {
    final opened =
        await launchUrl(Uri.parse(address), mode: LaunchMode.externalApplication);
    if (opened || !context.mounted) return;
  } catch (_) {
    if (!context.mounted) return;
  }

  ScaffoldMessenger.of(context).showSnackBar(
    SnackBar(content: Text(AppStrings.of(context).couldNotOpenLink)),
  );
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
  late final _terms = TapGestureRecognizer()
    ..onTap = () => openLegalPage(context, AppConfig.termsUrl);
  late final _privacy = TapGestureRecognizer()
    ..onTap = () => openLegalPage(context, AppConfig.privacyUrl);

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
