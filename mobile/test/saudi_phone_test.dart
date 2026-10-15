import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/format/saudi_phone.dart';

void main() {
  group('SaudiPhone.normalise', () {
    for (final raw in [
      '0501234567',
      '501234567',
      '050 123 4567',
      '+966501234567',
      '00966501234567',
      '٠٥٠١٢٣٤٥٦٧',
      '۰۵۰۱۲۳۴۵۶۷',
    ]) {
      test('"$raw" is +966501234567', () {
        expect(SaudiPhone.normalise(raw), '+966501234567');
      });
    }

    for (final raw in ['', '0112345678', '050123456', '05012345678', '+962791234567']) {
      test('"$raw" is refused', () => expect(SaudiPhone.normalise(raw), isNull));
    }
  });

  group('SaudiPhoneInputFormatter', () {
    String type(String text) => const SaudiPhoneInputFormatter()
        .formatEditUpdate(TextEditingValue.empty, TextEditingValue(text: text))
        .text;

    test('keeps only digits, converting Arabic ones', () => expect(type('٠٥٠-١٢٣ 45'), '05012345'));
    test('drops a pasted country code', () => expect(type('+966 50 123 4567'), '501234567'));
    test('stops at ten digits', () => expect(type('05012345678999'), '0501234567'));
  });
}
