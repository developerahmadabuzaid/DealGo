import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../shared/otp_input.dart';
import '../data/auth_repository.dart' show OtpChallenge;

/// How the user is signing in.
enum _Mode { otp, password, register }

/// Sign-in and registration.
///
/// A one-time code is the default: it needs no password, and proving control of the phone or email
/// is the same evidence a password reset would rely on anyway. Password sign-in and registration
/// remain available.
class LoginScreen extends ConsumerStatefulWidget {
  const LoginScreen({super.key});

  @override
  ConsumerState<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends ConsumerState<LoginScreen> {
  final _formKey = GlobalKey<FormState>();

  final _destination = TextEditingController();
  final _email = TextEditingController();
  final _password = TextEditingController();
  final _name = TextEditingController();
  final _phone = TextEditingController();

  _Mode _mode = _Mode.otp;
  OtpChallenge? _challenge;
  String? _error;
  bool _busy = false;

  @override
  void dispose() {
    _destination.dispose();
    _email.dispose();
    _password.dispose();
    _name.dispose();
    _phone.dispose();
    super.dispose();
  }

  String get _language => Localizations.localeOf(context).languageCode;

  // ------------------------------------------------------------------ actions

  Future<void> _sendCode() async {
    final destination = _destination.text.trim();
    if (destination.isEmpty) return;

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final challenge = await ref
          .read(authControllerProvider.notifier)
          .requestLoginOtp(destination, _language);

      setState(() => _challenge = challenge);
    } on ApiException catch (e) {
      setState(() => _error = e.message);
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
          .signInWithOtp(_destination.text.trim(), code);
    } on ApiException catch (e) {
      setState(() => _error = e.message);
    } catch (_) {
      setState(() => _error = AppStrings.of(context).somethingWentWrong);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _submitPassword() async {
    if (!_formKey.currentState!.validate()) return;

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final auth = ref.read(authControllerProvider.notifier);

      if (_mode == _Mode.register) {
        await auth.register(
          fullName: _name.text.trim(),
          email: _email.text.trim(),
          password: _password.text,
          phone: _phone.text.trim().isEmpty ? null : _phone.text.trim(),
          language: _language,
        );
      } else {
        await auth.signIn(_email.text.trim(), _password.text);
      }
    } on ApiException catch (e) {
      setState(() => _error = e.details.isNotEmpty ? e.details.join('\n') : e.message);
    } catch (_) {
      setState(() => _error = AppStrings.of(context).somethingWentWrong);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _switchMode(_Mode mode) => setState(() {
        _mode = mode;
        _challenge = null;
        _error = null;
      });

  // ------------------------------------------------------------------ build

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Icon(Icons.gavel_rounded, size: 56, color: theme.colorScheme.primary),
                  const SizedBox(height: 16),
                  Text(t.appName,
                      textAlign: TextAlign.center,
                      style: theme.textTheme.headlineSmall
                          ?.copyWith(fontWeight: FontWeight.bold)),
                  const SizedBox(height: 28),

                  if (_mode == _Mode.otp)
                    _challenge == null ? _destinationStep(t) : _codeStep(t)
                  else
                    _passwordForm(t),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }

  Widget _destinationStep(AppStrings t) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        TextField(
          controller: _destination,
          enabled: !_busy,
          decoration: InputDecoration(
            labelText: t.emailOrPhone,
            prefixIcon: const Icon(Icons.alternate_email_rounded),
          ),
          keyboardType: TextInputType.emailAddress,
          textInputAction: TextInputAction.done,
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
        const SizedBox(height: 10),
        TextButton(
          onPressed: _busy ? null : () => _switchMode(_Mode.password),
          child: Text(t.signInWithPassword),
        ),
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
          child: Text('${t.changeDestination} · ${t.emailOrPhone}'),
        ),
      ],
    );
  }

  Widget _passwordForm(AppStrings t) {
    final registering = _mode == _Mode.register;

    return Form(
      key: _formKey,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (registering) ...[
            TextFormField(
              controller: _name,
              decoration: InputDecoration(labelText: t.fullName),
              textInputAction: TextInputAction.next,
              validator: (v) => (v == null || v.trim().isEmpty) ? '—' : null,
            ),
            const SizedBox(height: 12),
          ],

          TextFormField(
            controller: _email,
            decoration: InputDecoration(labelText: t.email),
            keyboardType: TextInputType.emailAddress,
            textInputAction: TextInputAction.next,
            autofillHints: const [AutofillHints.email],
            validator: (v) => (v == null || !v.contains('@')) ? '—' : null,
          ),
          const SizedBox(height: 12),

          TextFormField(
            controller: _password,
            decoration: InputDecoration(labelText: t.password),
            obscureText: true,
            textInputAction: TextInputAction.done,
            onFieldSubmitted: (_) => _submitPassword(),
            validator: (v) => (v == null || v.length < 8) ? '—' : null,
          ),

          if (registering) ...[
            const SizedBox(height: 12),
            TextFormField(
              controller: _phone,
              decoration: InputDecoration(labelText: t.phone),
              keyboardType: TextInputType.phone,
            ),
          ],

          if (_error != null) ...[
            const SizedBox(height: 14),
            Text(_error!,
                textAlign: TextAlign.center,
                style: TextStyle(color: Theme.of(context).colorScheme.error)),
          ],

          const SizedBox(height: 22),
          FilledButton(
            onPressed: _busy ? null : _submitPassword,
            child: _busy
                ? const SizedBox(
                    height: 20, width: 20,
                    child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                : Text(registering ? t.register : t.login),
          ),
          const SizedBox(height: 10),
          TextButton(
            onPressed: _busy
                ? null
                : () => _switchMode(registering ? _Mode.password : _Mode.register),
            child: Text(registering ? t.haveAccount : t.noAccount),
          ),
          TextButton(
            onPressed: _busy ? null : () => _switchMode(_Mode.otp),
            child: Text(t.signInWithCode),
          ),
        ],
      ),
    );
  }
}
