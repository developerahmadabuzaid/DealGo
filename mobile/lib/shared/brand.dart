/// The DealGo mark and wordmark.
///
/// Drawn rather than loaded from an image file, for the same reason the admin console draws it:
/// one definition, no asset to ship at four densities, and it stays sharp at any size. The
/// geometry is the console's own artwork, laid out on the same 128 x 68 box, so the phone and the
/// dashboard show the same mark rather than two drawings of it.
///
/// Replacing this with the real artwork later is a one-line change at each call site; nothing
/// else reaches into the shapes.
library;

import 'package:flutter/material.dart';

import '../core/config/app_config.dart';
import '../core/theme/app_theme.dart';


/// The mark on its own: speed lines and an arrow running into a black D and an orange G.
///
/// Shows the real artwork when the API is serving it and falls back to the drawn mark when it is
/// not, which is the same arrangement the admin console uses. Dropping one file into the server's
/// wwwroot/brand folder is all it takes to switch both of them over, with nothing to rebuild.
class DealGoMark extends StatelessWidget {
  const DealGoMark({super.key, this.height = 34, this.onDark = false});

  /// The drawn height. Width follows the artwork's proportions.
  final double height;

  /// Flips the D to white. The mark's dark half disappears on a dark surface otherwise.
  final bool onDark;

  static const _ratio = _MarkPainter.boxWidth / _MarkPainter.boxHeight;

  @override
  Widget build(BuildContext context) {
    final drawn = SizedBox(
      height: height,
      width: height * _ratio,
      child: CustomPaint(
        painter: _MarkPainter(inkColor: onDark ? Colors.white : AppTheme.ink),
        // The mark carries no information a screen reader needs beyond the name beside it.
        isComplex: false,
      ),
    );

    return Image.network(
      AppConfig.logoUrl,
      height: height,
      // Height only, so real artwork keeps its own proportions instead of being squeezed into
      // the drawn mark's box.
      fit: BoxFit.contain,
      errorBuilder: (_, _, _) => drawn,
      // The drawn mark stands in while the file is on its way, so a header never has a hole in
      // it and never jumps between two different logos on a slow connection.
      frameBuilder: (_, child, frame, wasSynchronouslyLoaded) =>
          frame == null && !wasSynchronouslyLoaded ? drawn : child,
    );
  }
}

class _MarkPainter extends CustomPainter {
  const _MarkPainter({required this.inkColor});

  final Color inkColor;

  /*
   * The artwork's own coordinate box. Everything below is written in these units, then scaled
   * once, so the shapes never have to be rewritten for a different size.
   *
   * Wider than the console's first draft of this mark, which laid the D and the G out on a box
   * too narrow to hold both: the G's circle started to the left of where the D's bowl ended, and
   * since the G is painted last it simply covered the D. The two letters now have their own room.
   */
  static const boxWidth = 162.0;
  static const boxHeight = 68.0;

  /// Where the D's bowl is centred, and where the G's ring is. The letters are built around
  /// these two points, so moving one cannot leave the other behind. Kept as plain numbers
  /// because an Offset's fields cannot be read in a constant expression.
  static const _dBowlX = 62.0;
  static const _gRingX = 126.0;
  static const _midY = 34.0;

  /// The outer and inner radius shared by both letters, so they read as one alphabet.
  static const _outerR = 29.0;
  static const _innerR = 16.0;

  @override
  void paint(Canvas canvas, Size size) {
    final scale = size.height / boxHeight;
    canvas.save();
    canvas.scale(scale);

    final orange = Paint()
      ..color = AppTheme.brand
      ..isAntiAlias = true;
    final ink = Paint()
      ..color = inkColor
      ..isAntiAlias = true;

    // Three speed lines, stacked and offset, reading left to right into the mark.
    for (final line in const [
      Rect.fromLTWH(2, 19, 27, 7),
      Rect.fromLTWH(0, 30.5, 19, 7),
      Rect.fromLTWH(5, 42, 24, 7),
    ]) {
      canvas.drawRRect(RRect.fromRectXY(line, 3.5, 3.5), orange);
    }

    // The shaft and the arrowhead. Drawn before the letters, so the arrow runs behind the D
    // rather than sitting on top of it — the motion arrives at the word.
    canvas.drawRRect(RRect.fromRectXY(const Rect.fromLTWH(16, 30.5, 42, 7), 3.5, 3.5), orange);
    canvas.drawPath(
      Path()
        ..moveTo(52, 23.5)
        ..lineTo(70, 34)
        ..lineTo(52, 44.5)
        ..close(),
      orange,
    );

    canvas.drawPath(_letterD(), ink);

    // The G is a ring plus a crossbar. The bar is painted over the ring rather than merged into
    // it, and clipped to the letter's own circle so it cannot poke out past the edge.
    canvas.drawPath(_letterG(), orange);
    canvas.save();
    canvas.clipPath(Path()..addOval(_gOuter));
    canvas.drawRect(const Rect.fromLTWH(_gRingX, _midY - 6, _outerR, 12), orange);
    canvas.restore();

    canvas.restore();
  }

  static const _gOuter =
      Rect.fromLTRB(_gRingX - _outerR, _midY - _outerR, _gRingX + _outerR, _midY + _outerR);

  /*
   * A counter is punched with an even-odd fill, not with Path.combine.
   *
   * Path.combine is a no-op on some renderers, which silently returned the outer shape alone and
   * drew both letters as solid blobs. Two subpaths in one even-odd path is the same thing the
   * original artwork does with fill-rule="evenodd", and every renderer honours it.
   */

  /// A stem with a half-round bowl, hollowed by the same shape one size down.
  Path _letterD() {
    const stemX = _dBowlX - 20;
    const innerStemX = _dBowlX - 7;

    return Path()
      ..fillType = PathFillType.evenOdd
      ..moveTo(stemX, _midY - _outerR)
      ..lineTo(_dBowlX, _midY - _outerR)
      ..arcToPoint(const Offset(_dBowlX, _midY + _outerR),
          radius: const Radius.circular(_outerR))
      ..lineTo(stemX, _midY + _outerR)
      ..close()
      ..moveTo(innerStemX, _midY - _innerR)
      ..lineTo(_dBowlX, _midY - _innerR)
      ..arcToPoint(const Offset(_dBowlX, _midY + _innerR),
          radius: const Radius.circular(_innerR))
      ..lineTo(innerStemX, _midY + _innerR)
      ..close();
  }

  /// The ring the crossbar turns into a G.
  Path _letterG() => Path()
    ..fillType = PathFillType.evenOdd
    ..addOval(_gOuter)
    ..addOval(Rect.fromCircle(center: const Offset(_gRingX, _midY), radius: _innerR));

  @override
  bool shouldRepaint(_MarkPainter old) => old.inkColor != inkColor;
}

/// The name, in the two brand colours: Deal in black, Go in orange.
class DealGoWordmark extends StatelessWidget {
  const DealGoWordmark({super.key, this.fontSize = 22, this.onDark = false});

  final double fontSize;
  final bool onDark;

  @override
  Widget build(BuildContext context) {
    final base = TextStyle(
      fontSize: fontSize,
      fontWeight: FontWeight.w800,
      letterSpacing: -0.5,
      height: 1.1,
    );

    return Text.rich(
      TextSpan(children: [
        TextSpan(
          text: 'Deal',
          style: base.copyWith(color: onDark ? Colors.white : AppTheme.ink),
        ),
        TextSpan(text: 'Go', style: base.copyWith(color: AppTheme.brand)),
      ]),
      // The brand name is written left to right in an Arabic interface too.
      textDirection: TextDirection.ltr,
    );
  }
}

/// The mark and the name side by side, with the tagline underneath.
///
/// This is the full signature: the login screen and anywhere else the app introduces itself. A
/// bar or a header that only needs identifying uses [DealGoWordmark] on its own.
class DealGoLockup extends StatelessWidget {
  const DealGoLockup({
    super.key,
    this.markHeight = 44,
    this.fontSize = 26,
    this.tagline,
  });

  final double markHeight;
  final double fontSize;

  /// Shown under the name. Omitted when null.
  final String? tagline;

  @override
  Widget build(BuildContext context) {
    final onDark = Theme.of(context).brightness == Brightness.dark;

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        DealGoMark(height: markHeight, onDark: onDark),
        const SizedBox(width: AppTheme.space3),
        Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            DealGoWordmark(fontSize: fontSize, onDark: onDark),
            if (tagline != null)
              Padding(
                padding: const EdgeInsets.only(top: 2),
                child: Text(
                  tagline!,
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(fontSize: 12),
                ),
              ),
          ],
        ),
      ],
    );
  }
}
