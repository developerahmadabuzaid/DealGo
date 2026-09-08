import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../core/localization/app_strings.dart';
import '../features/auth/data/auth_repository.dart' show OtpChallenge;

/// Code entry, shared by the sign-in and withdrawal flows.
///
/// Shows where the code was sent (already masked by the server), counts down the resend cooldown,
/// and submits automatically once the full code has been typed.
class OtpInput extends StatefulWidget {
  const OtpInput({
    super.key,
    required this.challenge,
    required this.onSubmit,
    required this.onResend,
    this.busy = false,
    this.error,
  });

  final OtpChallenge challenge;
  final ValueChanged<String> onSubmit;
  final Future<void> Function() onResend;
  final bool busy;
  final String? error;

  @override
  State<OtpInput> createState() => _OtpInputState();
}

class _OtpInputState extends State<OtpInput> {
  final _controller = TextEditingController();
  final _focus = FocusNode();

  Timer? _timer;
  int _resendIn = 0;
  int _expiresIn = 0;

  @override
  void initState() {
    super.initState();
    _startCountdowns();
    WidgetsBinding.instance.addPostFrameCallback((_) => _focus.requestFocus());

    // Development convenience: when the API echoes the code, prefill it so the flow can be
    // exercised without a real SMS or email provider.
    final devCode = widget.challenge.developmentCode;
    if (devCode != null && devCode.isNotEmpty) _controller.text = devCode;
  }

  @override
  void didUpdateWidget(OtpInput old) {
    super.didUpdateWidget(old);
    if (old.challenge != widget.challenge) {
      _controller.clear();
      _startCountdowns();

      final devCode = widget.challenge.developmentCode;
      if (devCode != null && devCode.isNotEmpty) _controller.text = devCode;
    }
  }

  void _startCountdowns() {
    _timer?.cancel();
    _resendIn = widget.challenge.resendAvailableInSeconds;
    _tick();
    _timer = Timer.periodic(const Duration(seconds: 1), (_) => _tick());
  }

  void _tick() {
    if (!mounted) return;
    setState(() {
      if (_resendIn > 0) _resendIn--;
      _expiresIn = widget.challenge.expiresAt
          .difference(DateTime.now().toUtc())
          .inSeconds
          .clamp(0, 3600);
    });
  }

  @override
  void dispose() {
    _timer?.cancel();
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(t.codeSentTo, style: theme.textTheme.bodySmall, textAlign: TextAlign.center),
        const SizedBox(height: 4),
        Text(
          widget.challenge.destination,
          textAlign: TextAlign.center,
          style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700),
        ),
        const SizedBox(height: 20),

        // The code is digits only and always reads left-to-right, even in an RTL layout.
        Directionality(
          textDirection: TextDirection.ltr,
          child: TextField(
            controller: _controller,
            focusNode: _focus,
            enabled: !widget.busy,
            autofocus: true,
            keyboardType: TextInputType.number,
            textAlign: TextAlign.center,
            maxLength: widget.challenge.codeLength,
            autofillHints: const [AutofillHints.oneTimeCode],
            inputFormatters: [FilteringTextInputFormatter.digitsOnly],
            style: const TextStyle(
              fontSize: 30,
              fontWeight: FontWeight.bold,
              letterSpacing: 14,
              fontFeatures: [FontFeature.tabularFigures()],
            ),
            decoration: const InputDecoration(counterText: '', hintText: '······'),
            onChanged: (value) {
              if (value.length == widget.challenge.codeLength) {
                widget.onSubmit(value);
              }
            },
          ),
        ),

        if (widget.error != null) ...[
          const SizedBox(height: 8),
          Text(widget.error!,
              textAlign: TextAlign.center,
              style: TextStyle(color: theme.colorScheme.error)),
        ],

        const SizedBox(height: 14),
        FilledButton(
          onPressed: widget.busy || _controller.text.length < widget.challenge.codeLength
              ? null
              : () => widget.onSubmit(_controller.text),
          child: widget.busy
              ? const SizedBox(
                  height: 20, width: 20,
                  child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
              : Text(t.confirm),
        ),

        const SizedBox(height: 8),
        TextButton(
          onPressed: _resendIn > 0 || widget.busy
              ? null
              : () async {
                  await widget.onResend();
                  _startCountdowns();
                },
          child: Text(_resendIn > 0 ? t.resendIn(_resendIn) : t.resendCode),
        ),

        if (_expiresIn > 0)
          Text(t.codeExpiresIn(_expiresIn),
              textAlign: TextAlign.center, style: theme.textTheme.bodySmall)
        else
          Text(t.codeExpired,
              textAlign: TextAlign.center,
              style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.error)),
      ],
    );
  }
}
