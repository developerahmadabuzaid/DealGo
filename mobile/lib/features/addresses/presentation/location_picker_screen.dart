import 'package:flutter/material.dart';
import 'package:flutter_map/flutter_map.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:geolocator/geolocator.dart';
import 'package:latlong2/latlong.dart';

import '../../../core/localization/app_strings.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/app_header.dart';

/// Why the phone's position could not be read, in terms the screen can explain.
enum LocationFailure { servicesOff, denied, deniedForever, unavailable }

class LocationResult {
  const LocationResult.found(LatLng this.position) : failure = null;
  const LocationResult.failed(LocationFailure this.failure) : position = null;

  final LatLng? position;
  final LocationFailure? failure;
}

/// The phone's current position, asking for permission the first time. Behind a provider so
/// tests never touch the platform channel.
class DeviceLocation {
  const DeviceLocation();

  Future<LocationResult> current() async {
    try {
      if (!await Geolocator.isLocationServiceEnabled()) {
        return const LocationResult.failed(LocationFailure.servicesOff);
      }

      var permission = await Geolocator.checkPermission();
      if (permission == LocationPermission.denied) {
        permission = await Geolocator.requestPermission();
      }
      if (permission == LocationPermission.deniedForever) {
        return const LocationResult.failed(LocationFailure.deniedForever);
      }
      if (permission == LocationPermission.denied) {
        return const LocationResult.failed(LocationFailure.denied);
      }

      final position = await Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
          accuracy: LocationAccuracy.high,
          timeLimit: Duration(seconds: 20),
        ),
      );
      return LocationResult.found(LatLng(position.latitude, position.longitude));
    } catch (_) {
      // A timeout, or a platform with no location at all (the web build on some browsers).
      return const LocationResult.failed(LocationFailure.unavailable);
    }
  }

  Future<void> openSettings() => Geolocator.openAppSettings();
}

final deviceLocationProvider = Provider<DeviceLocation>((ref) => const DeviceLocation());

/// Opens the map and returns the point picked; behind a provider so tests can pick without a map.
typedef PickLocation = Future<LatLng?> Function(BuildContext context, {LatLng? initial, bool locate});

final locationPickerProvider = Provider<PickLocation>((ref) => LocationPickerScreen.open);

/// A map with a fixed pin in the middle: the customer moves the map under it, which is easier
/// on a phone than dragging a small marker. Pops with the point under the pin.
class LocationPickerScreen extends ConsumerStatefulWidget {
  const LocationPickerScreen({super.key, this.initial, this.locate = false});

  final LatLng? initial;

  /// Start by finding the phone, for "use my current location".
  final bool locate;

  /// Riyadh, when there is nothing better to start from.
  static const fallbackCenter = LatLng(24.7136, 46.6753);

  static Future<LatLng?> open(BuildContext context, {LatLng? initial, bool locate = false}) =>
      Navigator.of(context, rootNavigator: true).push<LatLng>(MaterialPageRoute(
        builder: (_) => LocationPickerScreen(initial: initial, locate: locate),
      ));

  @override
  ConsumerState<LocationPickerScreen> createState() => _LocationPickerScreenState();
}

class _LocationPickerScreenState extends ConsumerState<LocationPickerScreen> {
  final _map = MapController();
  bool _locating = false;
  bool _ready = false;

  /// A position found before the map had drawn, applied once it has.
  LatLng? _pending;

  @override
  void initState() {
    super.initState();
    if (widget.locate || widget.initial == null) {
      WidgetsBinding.instance.addPostFrameCallback((_) => _locate(quiet: !widget.locate));
    }
  }

  @override
  void dispose() {
    _map.dispose();
    super.dispose();
  }

  /// [quiet] when the screen locates on its own: a refusal then needs no message, the map is
  /// simply where it started.
  Future<void> _locate({bool quiet = false}) async {
    setState(() => _locating = true);
    final result = await ref.read(deviceLocationProvider).current();
    if (!mounted) return;
    setState(() => _locating = false);

    if (result.position != null) {
      if (_ready) {
        _map.move(result.position!, 17);
      } else {
        _pending = result.position;
      }
      return;
    }
    if (quiet) return;

    final t = AppStrings.of(context);
    final message = switch (result.failure!) {
      LocationFailure.servicesOff => t.locationServicesOff,
      LocationFailure.denied || LocationFailure.deniedForever => t.locationPermissionDenied,
      LocationFailure.unavailable => t.locationUnavailable,
    };

    ScaffoldMessenger.of(context).showSnackBar(SnackBar(
      content: Text(message),
      action: result.failure == LocationFailure.deniedForever
          ? SnackBarAction(
              label: t.openSettings,
              onPressed: () => ref.read(deviceLocationProvider).openSettings(),
            )
          : null,
    ));
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Scaffold(
      appBar: BrandAppBar(title: Text(t.locationOnMap)),
      body: Stack(
        children: [
          FlutterMap(
            mapController: _map,
            options: MapOptions(
              initialCenter: widget.initial ?? LocationPickerScreen.fallbackCenter,
              initialZoom: widget.initial == null ? 11 : 17,
              minZoom: 4,
              maxZoom: 19,
              // Turning the map only gets in the way of finding a door.
              interactionOptions: const InteractionOptions(
                flags: InteractiveFlag.all & ~InteractiveFlag.rotate,
              ),
              onMapReady: () {
                _ready = true;
                if (_pending != null) _map.move(_pending!, 17);
              },
            ),
            children: [
              TileLayer(
                urlTemplate: 'https://tile.openstreetmap.org/{z}/{x}/{y}.png',
                // OpenStreetMap asks every app to name itself.
                userAgentPackageName: 'com.dealgosa.app',
                maxNativeZoom: 19,
              ),
              const SimpleAttributionWidget(source: Text('OpenStreetMap contributors')),
            ],
          ),

          // The pin's point sits on the centre of the map, so it is lifted by half its height.
          const IgnorePointer(
            child: Center(
              child: Padding(
                padding: EdgeInsets.only(bottom: 44),
                child: Icon(Icons.location_on_rounded, size: 48, color: AppTheme.brand),
              ),
            ),
          ),

          PositionedDirectional(
            top: 12,
            start: 12,
            end: 12,
            child: Material(
              color: theme.colorScheme.surface.withValues(alpha: 0.94),
              borderRadius: BorderRadius.circular(12),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                child: Text(t.moveMapToPin,
                    textAlign: TextAlign.center, style: theme.textTheme.bodyMedium),
              ),
            ),
          ),

          PositionedDirectional(
            end: 16,
            bottom: 16,
            child: FloatingActionButton.small(
              heroTag: 'locate',
              tooltip: t.useMyLocation,
              onPressed: _locating ? null : () => _locate(),
              child: _locating
                  ? const SizedBox(
                      height: 18, width: 18, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Icon(Icons.my_location_rounded),
            ),
          ),
        ],
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: FilledButton.icon(
            onPressed: () => Navigator.of(context).pop(_map.camera.center),
            icon: const Icon(Icons.check_rounded),
            label: Text(t.confirmLocation),
          ),
        ),
      ),
    );
  }
}
