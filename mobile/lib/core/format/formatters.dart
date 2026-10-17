import 'package:intl/intl.dart';

/// Presentation-only formatting.
///
/// The server owns every number shown here; these helpers only decide how it looks.
class Fmt {
  const Fmt._();

  /// What each currency is written as. The server sends the ISO code; only the label
  /// changes here, never the amount.
  static const _symbols = {
    /*
     * The Saudi Riyal sign.
     *
     * U+E900 is a private code point carried by the RiyalSign font that ships with the app, which
     * the theme lists as a fallback. The sign is too new for system fonts; writing it this way
     * keeps prices as ordinary text — searchable, selectable, and styled like everything else —
     * instead of pictures pasted between the digits.
     */
    'SAR': '\ue900',
    'AED': 'د.إ',
    'KWD': 'د.ك',
    'EGP': 'ج.م',
    'USD': r'$',
    'EUR': '€',
  };

  /// Digits stay Western even in Arabic. The amounts sit next to Latin-script codes and
  /// server data, and mixing digit systems in one screen reads badly.
  static String symbolFor(String currency) => _symbols[currency.toUpperCase()] ?? currency;

  /// A person's name, isolated from the text around it.
  ///
  /// Names arrive in either script. A Latin name with a trailing initial, "Test 3.", laid out in an
  /// Arabic line put its full stop on the wrong end; isolating it lets the name keep its own
  /// direction whatever the page's.
  static String name(String value) =>
      String.fromCharCode(0x2068) + value + String.fromCharCode(0x2069); // FSI … PDI

  static String money(num? value, String currency, {String locale = 'en'}) {
    if (value == null) return '—';
    final formatter = NumberFormat.currency(
      locale: 'en_US',
      symbol: '',
      decimalDigits: 2,
    );
    return '${formatter.format(value).trim()} ${symbolFor(currency)}';
  }

  static String compactMoney(num? value, String currency) {
    if (value == null) return '—';
    return '${NumberFormat.compact().format(value)} ${symbolFor(currency)}';
  }

  static String number(num? value) =>
      value == null ? '—' : NumberFormat.decimalPattern().format(value);

  static String percent(num fraction) =>
      NumberFormat.percentPattern().format(fraction.clamp(0, 1));

  static String dateTime(DateTime? value, {String locale = 'en'}) {
    if (value == null) return '—';
    return DateFormat.yMMMd(locale == 'ar' ? 'ar' : 'en_US')
        .add_jm()
        .format(value.toLocal());
  }

  /// hh:mm:ss for long waits, mm:ss once the auction is under an hour.
  static String duration(Duration d) {
    if (d.isNegative) d = Duration.zero;

    final hours = d.inHours;
    final minutes = d.inMinutes.remainder(60).toString().padLeft(2, '0');
    final seconds = d.inSeconds.remainder(60).toString().padLeft(2, '0');

    return hours > 0
        ? '${hours.toString().padLeft(2, '0')}:$minutes:$seconds'
        : '$minutes:$seconds';
  }
}
