import 'package:flutter/services.dart';

/// Saudi mobile numbers, the only phone numbers the platform accepts.
///
/// Mirrors the server's rule: nine digits starting with 5 after the country code, however they
/// were typed. The server re-checks everything; this only catches a typo before a request is sent.
class SaudiPhone {
  const SaudiPhone._();

  static const countryCode = '+966';

  /// Shown in the empty field: the part the user types after +966.
  static const hint = '5X XXX XXXX';

  /// The canonical +9665XXXXXXXX form, or null when [raw] is not a Saudi mobile number.
  static String? normalise(String raw) {
    var digits = westernDigits(raw);

    if (digits.startsWith('00966')) {
      digits = digits.substring(5);
    } else if (digits.startsWith('966')) {
      digits = digits.substring(3);
    } else if (digits.startsWith('0')) {
      digits = digits.substring(1);
    }

    final valid = digits.length == 9 && digits.startsWith('5');
    return valid ? '$countryCode$digits' : null;
  }

  /// Keeps only digits, turning Arabic-Indic and Eastern Arabic-Indic ones into Western digits,
  /// so a number typed on an Arabic keyboard is the same number.
  static String westernDigits(String raw) {
    final out = StringBuffer();
    for (final r in raw.runes) {
      if (r >= 0x30 && r <= 0x39) {
        out.writeCharCode(r);
      } else if (r >= 0x660 && r <= 0x669) {
        out.writeCharCode(0x30 + r - 0x660);
      } else if (r >= 0x6F0 && r <= 0x6F9) {
        out.writeCharCode(0x30 + r - 0x6F0);
      }
    }
    return out.toString();
  }
}

/// Lets only the national part of a Saudi number into the field: Western digits, at most ten
/// ("05XXXXXXXX"). A pasted +966 or 00966 is dropped, because the field already shows it.
class SaudiPhoneInputFormatter extends TextInputFormatter {
  const SaudiPhoneInputFormatter();

  @override
  TextEditingValue formatEditUpdate(TextEditingValue oldValue, TextEditingValue newValue) {
    var digits = SaudiPhone.westernDigits(newValue.text);

    // A local number never begins with 9, so a leading 966 can only be the country code.
    if (digits.startsWith('00966')) {
      digits = digits.substring(5);
    } else if (digits.startsWith('966')) {
      digits = digits.substring(3);
    }
    if (digits.length > 10) digits = digits.substring(0, 10);

    return TextEditingValue(
      text: digits,
      selection: TextSelection.collapsed(offset: digits.length),
    );
  }
}
