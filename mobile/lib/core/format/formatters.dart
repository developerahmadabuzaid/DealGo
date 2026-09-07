import 'package:intl/intl.dart';

/// Presentation-only formatting.
///
/// The server owns every number shown here; these helpers only decide how it looks.
class Fmt {
  const Fmt._();

  static String money(num? value, String currency, {String locale = 'en'}) {
    if (value == null) return '—';
    final formatter = NumberFormat.currency(
      locale: locale == 'ar' ? 'ar' : 'en_US',
      symbol: '',
      decimalDigits: 2,
    );
    return '${formatter.format(value).trim()} $currency';
  }

  static String compactMoney(num? value, String currency) {
    if (value == null) return '—';
    return '${NumberFormat.compact().format(value)} $currency';
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
