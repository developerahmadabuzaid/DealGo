import 'package:flutter/services.dart' show rootBundle;

/// The terms and policies, as shipped with the app in `assets/legal/terms_ar.txt`.
///
/// The file is the text exactly as the business wrote it: a title line, then sections, each a
/// heading line followed by paragraphs, with blank lines between. Every paragraph is a sentence
/// and ends with a full stop; a heading does not. That is the only rule used to tell them apart,
/// so the text can be edited without touching code.
class TermsDocument {
  const TermsDocument({required this.title, required this.sections});

  final String title;
  final List<TermsSection> sections;

  static const asset = 'assets/legal/terms_ar.txt';

  /// The section the privacy link opens at.
  static const privacyHeading = 'الخصوصية وحماية البيانات';

  static Future<TermsDocument> load() async => parse(await rootBundle.loadString(asset));

  static TermsDocument parse(String text) {
    final lines = text
        .split(RegExp(r'\r?\n'))
        .map((l) => l.trim())
        .where((l) => l.isNotEmpty)
        .toList();
    if (lines.isEmpty) return const TermsDocument(title: '', sections: []);

    final sections = <TermsSection>[];
    for (final line in lines.skip(1)) {
      final isHeading = !line.endsWith('.');
      if (isHeading || sections.isEmpty) {
        sections.add(TermsSection(heading: isHeading ? line : '', paragraphs: []));
        if (isHeading) continue;
      }
      sections.last.paragraphs.add(line);
    }

    return TermsDocument(title: lines.first, sections: sections);
  }
}

class TermsSection {
  const TermsSection({required this.heading, required this.paragraphs});

  final String heading;
  final List<String> paragraphs;
}
