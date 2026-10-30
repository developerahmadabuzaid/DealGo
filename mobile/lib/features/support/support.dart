import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../core/localization/app_strings.dart';
import '../../core/network/api_client.dart';
import '../../core/providers.dart';

/// The support line: a WhatsApp number an administrator sets in the console.
class SupportRepository {
  SupportRepository(this._api);

  final ApiClient _api;

  /// Used when the server cannot be reached, so the button still leads somewhere.
  static const defaultWhatsapp = '+966533851110';

  Future<String> whatsappNumber() async {
    final data = await _api.get('/support') as Map<String, dynamic>?;
    final number = data?['whatsappNumber'] as String?;
    return (number == null || number.trim().isEmpty) ? defaultWhatsapp : number;
  }
}

/// WhatsApp's own green, so the button is recognised before it is read.
const whatsappGreen = Color(0xFF25D366);

/// Opens a WhatsApp chat with support, or says why it could not.
Future<void> openWhatsAppSupport(BuildContext context, WidgetRef ref) async {
  final t = AppStrings.of(context);
  final messenger = ScaffoldMessenger.of(context);

  String number;
  try {
    number = await ref.read(supportWhatsappProvider.future);
  } catch (_) {
    number = SupportRepository.defaultWhatsapp;
  }

  // wa.me wants the international number as digits only, without the plus.
  final digits = number.replaceAll(RegExp(r'\D'), '');
  final url = Uri.parse('https://wa.me/$digits');

  try {
    if (await launchUrl(url, mode: LaunchMode.externalApplication)) return;
  } catch (_) {
    // Falls through to the message below.
  }

  messenger.showSnackBar(SnackBar(content: Text(t.couldNotOpenLink)));
}

/// The round WhatsApp button that floats over every tab.
class WhatsAppSupportButton extends ConsumerWidget {
  const WhatsAppSupportButton({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return FloatingActionButton.small(
      heroTag: 'whatsapp-support',
      tooltip: AppStrings.of(context).whatsappSupport,
      backgroundColor: whatsappGreen,
      foregroundColor: Colors.white,
      shape: const CircleBorder(),
      onPressed: () => openWhatsAppSupport(context, ref),
      child: const WhatsAppIcon(size: 24, color: Colors.white),
    );
  }
}

/// WhatsApp's mark: a round speech bubble with its tail at the lower corner and a handset inside.
///
/// Material's icon set has no brand marks, and a whole icon package for one glyph is more than
/// this needs, so it is drawn here.
class WhatsAppIcon extends StatelessWidget {
  const WhatsAppIcon({super.key, this.size = 24, this.color = whatsappGreen});

  final double size;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return SizedBox.square(
      dimension: size,
      child: Stack(
        alignment: Alignment.center,
        children: [
          CustomPaint(size: Size.square(size), painter: _BubblePainter(color)),
          Padding(
            padding: EdgeInsets.only(bottom: size * 0.04),
            child: Icon(Icons.call, size: size * 0.48, color: color),
          ),
        ],
      ),
    );
  }
}

class _BubblePainter extends CustomPainter {
  const _BubblePainter(this.color);

  final Color color;

  @override
  void paint(Canvas canvas, Size size) {
    final s = size.width;
    final stroke = s * 0.085;
    final paint = Paint()
      ..color = color
      ..style = PaintingStyle.stroke
      ..strokeWidth = stroke
      ..strokeJoin = StrokeJoin.round
      ..strokeCap = StrokeCap.round;

    final center = Offset(s * 0.52, s * 0.48);
    final r = s * 0.40;

    // Most of a circle, opened at the lower left where the tail leaves it.
    const gapStart = 2.15; // radians, measured clockwise from the right
    const gapEnd = 2.75;
    final rect = Rect.fromCircle(center: center, radius: r);
    final path = Path()
      ..addArc(rect, gapEnd, 2 * 3.141592653589793 - (gapEnd - gapStart));

    // The tail: from the end of the arc down to the corner and back to its start.
    final tip = Offset(s * 0.08, s * 0.92);
    Offset onCircle(double a) => center + Offset(r * _cos(a), r * _sin(a));
    path
      ..moveTo(onCircle(gapStart).dx, onCircle(gapStart).dy)
      ..lineTo(tip.dx, tip.dy)
      ..lineTo(onCircle(gapEnd).dx, onCircle(gapEnd).dy);

    canvas.drawPath(path, paint);
  }

  static double _cos(double a) => math.cos(a);
  static double _sin(double a) => math.sin(a);

  @override
  bool shouldRepaint(_BubblePainter old) => old.color != color;
}
