import 'package:flutter/material.dart';

import 'widgets.dart';

/// Backgrounds the app falls back to when something has no picture of its own.
///
/// An auction created in the console before its cover photograph is ready used to appear as a grey
/// box with a broken-picture icon. It now appears on one of these — abstract, branded, and chosen
/// from the auction's own number, so the same auction keeps the same artwork rather than changing
/// every time the list is rebuilt.
const _brandArt = [
  'assets/images/ads/dept_1.png',
  'assets/images/ads/dept_2.png',
  'assets/images/ads/dept_3.png',
  'assets/images/ads/dept_4.png',
  'assets/images/ads/dept_5.png',
  'assets/images/ads/dept_6.png',
];

String brandArt(Object seed) => _brandArt[seed.hashCode.abs() % _brandArt.length];

/// A picture, or the brand's artwork when there is none.
class ArtOrPicture extends StatelessWidget {
  const ArtOrPicture({super.key, required this.imageUrl, required this.seed});

  final String? imageUrl;

  /// Whatever identifies the thing being drawn — its number, or its name.
  final Object seed;

  @override
  Widget build(BuildContext context) {
    final url = imageUrl;
    if (url != null && url.isNotEmpty) return RemoteImage(url, radius: 0);

    return Image.asset(brandArt(seed), fit: BoxFit.cover);
  }
}
