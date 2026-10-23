import 'package:flutter/material.dart';

import '../../core/localization/app_strings.dart';
import '../../core/theme/app_theme.dart';
import '../../shared/app_header.dart';
import '../../shared/widgets.dart';
import 'terms_document.dart';

/// The terms and policies, read inside the app rather than in a browser.
///
/// Open to anyone, signed in or not, because it is read before an account exists. [section] names
/// a heading to open at; the privacy link uses it to land on the privacy section.
class TermsScreen extends StatefulWidget {
  const TermsScreen({super.key, this.section});

  final String? section;

  @override
  State<TermsScreen> createState() => _TermsScreenState();
}

class _TermsScreenState extends State<TermsScreen> {
  late final Future<TermsDocument> _document = TermsDocument.load();
  final _keys = <String, GlobalKey>{};
  bool _jumped = false;

  GlobalKey _keyFor(String heading) => _keys.putIfAbsent(heading, GlobalKey.new);

  void _jumpToSection() {
    if (_jumped || widget.section == null) return;
    _jumped = true;

    // After the page has finished sliding in: scrolled during the transition, the move was lost.
    Future<void> attempt(int left) async {
      await Future<void>.delayed(const Duration(milliseconds: 350));
      if (!mounted) return;
      final target = _keys[widget.section!]?.currentContext;
      if (target != null && target.mounted) {
        await Scrollable.ensureVisible(target, duration: const Duration(milliseconds: 300));
      } else if (left > 0) {
        await attempt(left - 1);
      }
    }

    attempt(3);
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Scaffold(
      appBar: BrandAppBar(title: Text(t.termsOfUse)),
      body: FutureBuilder<TermsDocument>(
        future: _document,
        builder: (context, snapshot) {
          if (snapshot.hasError) {
            return ErrorView(message: t.somethingWentWrong);
          }
          final doc = snapshot.data;
          if (doc == null) return const Center(child: CircularProgressIndicator());

          _jumpToSection();

          // The text is Arabic whatever language the rest of the app is in.
          return Directionality(
            textDirection: TextDirection.rtl,
            child: SingleChildScrollView(
              padding: const EdgeInsets.fromLTRB(
                  AppTheme.space4, AppTheme.space4, AppTheme.space4, AppTheme.space8),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(
                    doc.title,
                    style: theme.textTheme.headlineSmall?.copyWith(fontWeight: FontWeight.w800),
                  ),
                  const SizedBox(height: AppTheme.space4),
                  for (final s in doc.sections)
                    Padding(
                      key: s.heading.isEmpty ? null : _keyFor(s.heading),
                      padding: const EdgeInsets.only(bottom: AppTheme.space5),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          if (s.heading.isNotEmpty) ...[
                            Text(
                              s.heading,
                              style: theme.textTheme.titleMedium?.copyWith(
                                fontWeight: FontWeight.w800,
                                color: AppTheme.brand,
                              ),
                            ),
                            const SizedBox(height: AppTheme.space2),
                          ],
                          for (final p in s.paragraphs)
                            Padding(
                              padding: const EdgeInsets.only(bottom: AppTheme.space2),
                              child: Text(
                                p,
                                style: theme.textTheme.bodyMedium?.copyWith(height: 1.7),
                              ),
                            ),
                        ],
                      ),
                    ),
                ],
              ),
            ),
          );
        },
      ),
    );
  }
}
