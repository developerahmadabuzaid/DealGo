import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/core/theme/app_theme.dart';
import 'package:mobile/features/shipping/data/shipment_repository.dart';
import 'package:mobile/features/shipping/presentation/shipment_tracking_card.dart';

/// Delivery by M5azn: what the server says is read safely, and the customer sees where the
/// parcel is, with its tracking number and a link only when the link is a web address.
void main() {
  Future<void> pump(WidgetTester tester, ShipmentTracking tracking, {String locale = 'ar'}) async {
    await tester.pumpWidget(MaterialApp(
      theme: AppTheme.light(),
      locale: Locale(locale),
      localizationsDelegates: const [
        AppStringsDelegate(),
        GlobalMaterialLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
      ],
      supportedLocales: AppStrings.supportedLocales,
      home: Scaffold(body: SingleChildScrollView(child: ShipmentTrackingCard(tracking: tracking))),
    ));
    await tester.pumpAndSettle();
  }

  test('reads the tracking the server sends', () {
    final t = ShipmentTracking.fromJson({
      'provider': 'M5azn',
      'status': 'Shipped',
      'remoteStatus': 'out_for_delivery',
      'trackingNumber': ' AWB-555 ',
      'trackingUrl': 'https://track.example/AWB-555',
      'carrier': 'SMSA',
      'createdAt': '2026-11-07T09:00:00Z',
      'submittedAt': '2026-11-07T09:01:00Z',
      'shippedAt': '2026-11-07T12:00:00Z',
      'lastSyncedAt': '2026-11-07T12:30:00Z',
    });

    expect(t.status, 'Shipped');
    expect(t.step, 2);
    expect(t.trackingNumber, 'AWB-555');
    expect(t.trackingUri, Uri.parse('https://track.example/AWB-555'));
    expect(t.shippedAt, DateTime.utc(2026, 11, 7, 12));
    expect(t.deliveredAt, isNull);
  });

  test('never offers to open anything but a web address', () {
    final t = ShipmentTracking.fromJson({'status': 'Shipped', 'trackingUrl': 'javascript:alert(1)'});
    expect(t.trackingUri, isNull);
    expect(ShipmentTracking.fromJson({'status': 'Shipped', 'trackingUrl': '  '}).trackingUrl, isNull);
  });

  testWidgets('a shipped parcel shows its status, courier and tracking number', (tester) async {
    await pump(
      tester,
      const ShipmentTracking(
        provider: 'M5azn',
        status: 'Shipped',
        trackingNumber: 'AWB-555',
        trackingUrl: 'https://track.example/AWB-555',
        carrier: 'SMSA',
      ),
    );

    expect(find.text('الشحن والتتبع'), findsOneWidget);
    expect(find.text('تم الشحن'), findsWidgets);
    expect(find.text('AWB-555'), findsOneWidget);
    expect(find.text('SMSA'), findsOneWidget);
    expect(find.text('تتبّع الشحنة'), findsOneWidget);
  });

  testWidgets('a parcel just sent has no tracking link yet, in English too', (tester) async {
    await pump(tester, const ShipmentTracking(provider: 'M5azn', status: 'Submitted'), locale: 'en');

    expect(find.text('Sent to the supplier'), findsOneWidget);
    expect(find.text('Track shipment'), findsNothing);
    expect(find.text('Tracking number'), findsNothing);
  });

  testWidgets('a cancelled shipment says so and hides the progress line', (tester) async {
    await pump(tester, const ShipmentTracking(provider: 'M5azn', status: 'Cancelled'));

    expect(find.text('أُلغي الشحن'), findsOneWidget);
    expect(find.text('قيد التجهيز'), findsNothing);
  });
}
