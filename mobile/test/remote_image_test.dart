import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/config/app_config.dart';
import 'package:mobile/shared/widgets.dart';

/// What RemoteImage actually asks the network for, and at what size.
///
/// Two things are checked here. The address, because the case that was broken is a picture an
/// administrator uploaded: it is stored as "/uploads/….jpg", which the admin console can open
/// because it is served from the API's own origin, and the app cannot open at all. And the decode
/// size, because a photograph straight from a camera held at full resolution in a thumbnail is
/// what closes the app on a cheap phone.
void main() {
  /// The provider underneath, with the resizing wrapper peeled off, plus the width it was asked
  /// to decode at. Null when the placeholder was drawn and nothing was requested.
  ({String url, int? decodeWidth})? requested(WidgetTester tester) {
    final images = tester.widgetList<Image>(find.byType(Image));
    if (images.isEmpty) return null;

    final provider = images.first.image;

    // Asking for a decode size wraps the provider; the address lives on the one inside.
    if (provider is ResizeImage) {
      return (
        url: (provider.imageProvider as NetworkImage).url,
        decodeWidth: provider.width,
      );
    }

    return (url: (provider as NetworkImage).url, decodeWidth: null);
  }

  /// Draws the widget in a slot of a known size, on a screen of a known pixel density.
  Future<void> pump(
    WidgetTester tester,
    String? stored, {
    double? width = 40,
    double? height = 40,
    double devicePixelRatio = 3.0,
  }) async {
    tester.view.devicePixelRatio = devicePixelRatio;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        home: Scaffold(
          body: Center(
            child: SizedBox(
              width: width,
              height: height,
              child: RemoteImage(stored, width: width, height: height),
            ),
          ),
        ),
      ),
    );
  }

  group('the address', () {
    testWidgets('an uploaded picture is asked for from the API host', (tester) async {
      await pump(tester, '/uploads/89b41f97.png');

      expect(requested(tester)?.url, '${AppConfig.apiBaseUrl}/uploads/89b41f97.png');
    });

    testWidgets('a picture that names its own host is asked for unchanged', (tester) async {
      const outside = 'https://picsum.photos/seed/phone/800/600';
      await pump(tester, outside);

      expect(requested(tester)?.url, outside);
    });

    testWidgets('no picture draws the placeholder and asks for nothing', (tester) async {
      await pump(tester, null);
      expect(requested(tester), isNull);

      await pump(tester, '');
      expect(requested(tester), isNull);
    });
  });

  group('the decode size', () {
    testWidgets('is the drawn size in real device pixels, not logical ones', (tester) async {
      await pump(tester, '/uploads/a.png', width: 124, height: 124, devicePixelRatio: 3.0);

      // A 124 wide product card on a 3x phone really is 372 pixels of screen.
      expect(requested(tester)?.decodeWidth, 372);
    });

    testWidgets('follows the screen it is actually on', (tester) async {
      await pump(tester, '/uploads/a.png', width: 124, height: 124, devicePixelRatio: 2.0);

      expect(requested(tester)?.decodeWidth, 248);
    });

    /// `cover` crops to fill, so the side that needs the most resolution depends on the picture's
    /// own shape — which is not known until it loads. Taking the larger side keeps a wide picture
    /// in a short slot sharp instead of blowing it up from a few dozen pixels.
    testWidgets('takes the taller side when the slot is short and wide', (tester) async {
      await pump(tester, '/uploads/a.png', width: 430, height: 48, devicePixelRatio: 3.0);

      expect(requested(tester)?.decodeWidth, 1290);
    });

    testWidgets('takes the wider side when the slot is tall and narrow', (tester) async {
      await pump(tester, '/uploads/a.png', width: 48, height: 430, devicePixelRatio: 3.0);

      expect(requested(tester)?.decodeWidth, 1290);
    });

    testWidgets('is capped, so an absurd slot cannot ask for an absurd bitmap', (tester) async {
      await pump(tester, '/uploads/a.png', width: 4000, height: 4000, devicePixelRatio: 3.0);

      expect(requested(tester)?.decodeWidth, 2048);
    });

    /// Guessing a size here would risk a picture blurrier than the one that was asked for, so the
    /// picture is left exactly as it is.
    testWidgets('nothing is resized when no size is known', (tester) async {
      tester.view.devicePixelRatio = 3.0;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        const MaterialApp(
          home: Scaffold(
            body: UnconstrainedBox(child: RemoteImage('/uploads/a.png')),
          ),
        ),
      );

      expect(requested(tester)?.decodeWidth, isNull);
    });
  });
}
