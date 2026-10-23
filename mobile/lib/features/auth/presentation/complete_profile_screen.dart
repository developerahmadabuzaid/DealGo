import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/brand.dart';
import '../../../shared/legal_links.dart';
import '../../../shared/widgets.dart';

/// Asks a newly created account for its name, and for its agreement to the terms.
///
/// An account made by verifying a new phone number starts with no name. The router keeps the
/// user here until one is saved, so every account that reaches the app has a name, and nobody
/// gets past this step without ticking that they have read and accept the terms.
class CompleteProfileScreen extends ConsumerStatefulWidget {
  const CompleteProfileScreen({super.key});

  @override
  ConsumerState<CompleteProfileScreen> createState() => _CompleteProfileScreenState();
}

class _CompleteProfileScreenState extends ConsumerState<CompleteProfileScreen> {
  final _name = TextEditingController();
  late final _readTerms = TapGestureRecognizer()..onTap = () => openTerms(context);

  bool _accepted = false;
  String? _error;
  bool _busy = false;

  @override
  void dispose() {
    _name.dispose();
    _readTerms.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final name = _name.text.trim();
    if (name.isEmpty || !_accepted) return;

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      await ref.read(authControllerProvider.notifier).completeProfile(name);
    } on ApiException catch (e) {
      setState(() => _error = e.displayMessage);
    } catch (_) {
      setState(() => _error = AppStrings.of(context).somethingWentWrong);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(AppTheme.space6),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Center(
                      child: DealGoLockup(
                    markHeight: 46,
                    fontSize: 28,
                    tagline: t.tagline,
                  )),
                  const SizedBox(height: AppTheme.space6),
                  SurfaceCard(
                    padding: const EdgeInsets.all(AppTheme.space5),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Text(t.whatsYourName,
                            textAlign: TextAlign.center,
                            style: theme.textTheme.titleLarge),
                        const SizedBox(height: 6),
                        Text(t.nameHelp,
                            textAlign: TextAlign.center,
                            style: theme.textTheme.bodyMedium),
                        const SizedBox(height: 16),
                        TextField(
                          controller: _name,
                          enabled: !_busy,
                          autofocus: true,
                          decoration: InputDecoration(
                            labelText: t.fullName,
                            prefixIcon: const Icon(Icons.person_outline_rounded),
                          ),
                          keyboardType: TextInputType.name,
                          textCapitalization: TextCapitalization.words,
                          autofillHints: const [AutofillHints.name],
                          maxLength: 256,
                          textInputAction: TextInputAction.done,
                          onChanged: (_) => setState(() {}),
                          onSubmitted: (_) => _save(),
                        ),
                        if (_error != null) ...[
                          const SizedBox(height: 12),
                          Text(_error!,
                              textAlign: TextAlign.center,
                              style: TextStyle(color: theme.colorScheme.error)),
                        ],
                        const SizedBox(height: 12),
                        // Unticked by default: agreement has to be given, not assumed.
                        Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Checkbox(
                              value: _accepted,
                              onChanged: _busy
                                  ? null
                                  : (v) => setState(() => _accepted = v ?? false),
                            ),
                            Expanded(
                              child: Padding(
                                padding: const EdgeInsets.only(top: 12),
                                child: Text.rich(
                                  TextSpan(children: [
                                    TextSpan(text: t.acceptTermsBefore),
                                    TextSpan(
                                      text: t.acceptTermsLink,
                                      recognizer: _readTerms,
                                      style: TextStyle(
                                        color: theme.colorScheme.primary,
                                        fontWeight: FontWeight.w700,
                                        decoration: TextDecoration.underline,
                                      ),
                                    ),
                                    TextSpan(text: t.acceptTermsAfter),
                                  ]),
                                  style: theme.textTheme.bodyMedium,
                                ),
                              ),
                            ),
                          ],
                        ),
                        const SizedBox(height: 16),
                        FilledButton(
                          onPressed: _busy || _name.text.trim().isEmpty || !_accepted ? null : _save,
                          child: _busy
                              ? const SizedBox(
                                  height: 20, width: 20,
                                  child: CircularProgressIndicator(
                                      strokeWidth: 2, color: Colors.white))
                              : Text(t.continueLabel),
                        ),
                        const SizedBox(height: 6),
                        TextButton(
                          onPressed: _busy
                              ? null
                              : () => ref.read(authControllerProvider.notifier).signOut(),
                          child: Text(t.logout),
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
