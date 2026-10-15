import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/saudi_phone.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/brand.dart';
import '../../../shared/legal_links.dart';
import '../../../shared/widgets.dart';
import '../../../shared/otp_input.dart';
import '../data/auth_repository.dart' show OtpChallenge;

/// Sign-in by phone number and a one-time code.
///
/// This is also sign-up: the server creates an account the first time a new number is verified,
/// and the app then asks for the user's name on the complete-profile screen.
class LoginScreen extends ConsumerStatefulWidget {
  const LoginScreen({super.key});

  @override
  ConsumerState<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends ConsumerState<LoginScreen> {
  final _phone = TextEditingController();

  OtpChallenge? _challenge;
  String? _error;
  bool _busy = false;

  @override
  void dispose() {
    _phone.dispose();
    super.dispose();
  }

  String get _language => Localizations.localeOf(context).languageCode;

  // ------------------------------------------------------------------ actions

  /// The number in canonical +9665XXXXXXXX form, which is what gets sent and verified.
  String? get _canonicalPhone => SaudiPhone.normalise(_phone.text);

  Future<void> _sendCode() async {
    if (_phone.text.trim().isEmpty) return;

    final phone = _canonicalPhone;
    if (phone == null) {
      setState(() => _error = AppStrings.of(context).invalidSaudiPhone);
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final challenge = await ref
          .read(authControllerProvider.notifier)
          .requestLoginOtp(phone, _language);

      setState(() => _challenge = challenge);
    } on ApiException catch (e) {
      setState(() => _error = e.displayMessage);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _verifyCode(String code) async {
    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      await ref
          .read(authControllerProvider.notifier)
          .signInWithOtp(_canonicalPhone!, code);
    } on ApiException catch (e) {
      setState(() => _error = e.displayMessage);
    } catch (_) {
      setState(() => _error = AppStrings.of(context).somethingWentWrong);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  // ------------------------------------------------------------------ build

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(AppTheme.space6),
            child: ConstrainedBox(
              // Caps the form on a laptop or tablet: a full-width login field looks unfinished.
              constraints: const BoxConstraints(maxWidth: 420),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // The app signs its own name here rather than showing a stand-in icon.
                  Center(
                      child: DealGoLockup(
                    markHeight: 46,
                    fontSize: 28,
                    tagline: t.tagline,
                  )),
                  const SizedBox(height: AppTheme.space6),

                  SurfaceCard(
                    padding: const EdgeInsets.all(AppTheme.space5),
                    child: _challenge == null ? _phoneStep(t) : _codeStep(t),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _phoneStep(AppStrings t) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(t.phoneSignInHint,
            textAlign: TextAlign.center,
            style: Theme.of(context).textTheme.bodyMedium),
        const SizedBox(height: 16),
        TextField(
          controller: _phone,
          enabled: !_busy,
          decoration: InputDecoration(
            labelText: t.mobileNumber,
            hintText: SaudiPhone.hint,
            hintTextDirection: TextDirection.ltr,
            prefixIcon: const Icon(Icons.phone_iphone_rounded),
            // At the end of the field, which in Arabic is its left edge, so it reads
            // "+966 5X XXX XXXX" left to right just as the number is written.
            suffixIcon: const _CountryCode(),
          ),
          keyboardType: TextInputType.phone,
          inputFormatters: const [SaudiPhoneInputFormatter()],
          autofillHints: const [AutofillHints.telephoneNumberNational],
          // Phone numbers read left to right even in the Arabic layout.
          textDirection: TextDirection.ltr,
          textInputAction: TextInputAction.done,
          onChanged: (_) {
            if (_error != null) setState(() => _error = null);
          },
          onSubmitted: (_) => _sendCode(),
        ),
        if (_error != null) ...[
          const SizedBox(height: 12),
          Text(_error!,
              textAlign: TextAlign.center,
              style: TextStyle(color: Theme.of(context).colorScheme.error)),
        ],
        const SizedBox(height: 20),
        FilledButton(
          onPressed: _busy ? null : _sendCode,
          child: _busy
              ? const SizedBox(
                  height: 20, width: 20,
                  child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
              : Text(t.sendCode),
        ),
        const SizedBox(height: 16),
        const LegalConsentText(),
      ],
    );
  }

  Widget _codeStep(AppStrings t) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        OtpInput(
          challenge: _challenge!,
          busy: _busy,
          error: _error,
          onSubmit: _verifyCode,
          onResend: _sendCode,
        ),
        const SizedBox(height: 6),
        TextButton(
          onPressed: _busy
              ? null
              : () => setState(() {
                    _challenge = null;
                    _error = null;
                  }),
          child: Text('${t.changeDestination} · ${t.phone}'),
        ),
      ],
    );
  }
}

/// The fixed Saudi country code beside the number field.
class _CountryCode extends StatelessWidget {
  const _CountryCode();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsetsDirectional.only(end: 12, start: 4),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Container(width: 1, height: 24, color: theme.colorScheme.outlineVariant),
          const SizedBox(width: 10),
          Text(
            SaudiPhone.countryCode,
            textDirection: TextDirection.ltr,
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w700,
              fontFeatures: const [FontFeature.tabularFigures()],
            ),
          ),
        ],
      ),
    );
  }
}
