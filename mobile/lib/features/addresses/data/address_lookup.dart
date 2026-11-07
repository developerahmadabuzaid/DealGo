import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:geocoding/geocoding.dart';
import 'package:latlong2/latlong.dart';

/// The parts of an address a point on the map turned into. Any of them may be missing.
class FoundAddress {
  const FoundAddress({this.city, this.district, this.street, this.buildingNumber});

  final String? city;
  final String? district;
  final String? street;
  final String? buildingNumber;

  bool get isEmpty => city == null && district == null && street == null && buildingNumber == null;

  /// Plus codes ("8Q7X+2F") are what the phone answers when it knows no street; they mean
  /// nothing to a courier.
  static final _plusCode = RegExp(r'^[23456789CFGHJMPQRVWX]{2,8}\+[23456789CFGHJMPQRVWX]{0,3}', caseSensitive: false);

  /// What the phone's geocoder calls each part, mapped to the form's fields. Apple and Google name
  /// them differently and leave different ones empty, so each field takes the first that is there.
  static FoundAddress fromParts({
    String? locality,
    String? subAdministrativeArea,
    String? administrativeArea,
    String? subLocality,
    String? thoroughfare,
    String? street,
    String? subThoroughfare,
  }) {
    String? clean(String? v) {
      final s = v?.trim();
      if (s == null || s.isEmpty || _plusCode.hasMatch(s)) return null;
      return s;
    }

    final city = clean(locality) ?? clean(subAdministrativeArea) ?? clean(administrativeArea);

    // "street" is often the whole first line ("12 King Fahd Rd, Al Olaya"); only a plain one is used.
    final plainStreet = clean(street);
    final streetName = clean(thoroughfare) ??
        (plainStreet != null && !plainStreet.contains(',') && !plainStreet.contains('،') ? plainStreet : null);

    final building = clean(subThoroughfare);

    return FoundAddress(
      city: city,
      district: clean(subLocality) == city ? null : clean(subLocality),
      street: streetName == city ? null : streetName,
      buildingNumber: building != null && RegExp(r'\d').hasMatch(building) ? building : null,
    );
  }
}

/// Turns a point into an address with the phone's own geocoder (Apple's on iPhone, Google's on
/// Android): no key, nothing to pay, in Arabic. Behind a provider so tests never touch the platform.
class AddressLookup {
  const AddressLookup();

  /// Null when the phone cannot say (no geocoder, no network, the web build, a timeout).
  Future<FoundAddress?> at(LatLng point, {Locale locale = const Locale('ar', 'SA')}) async {
    if (kIsWeb) return null;
    try {
      final marks = await Geocoding(locale: locale)
          .placemarkFromCoordinates(point.latitude, point.longitude)
          .timeout(const Duration(seconds: 10));
      if (marks.isEmpty) return null;

      final m = marks.first;
      final found = FoundAddress.fromParts(
        locality: m.locality,
        subAdministrativeArea: m.subAdministrativeArea,
        administrativeArea: m.administrativeArea,
        subLocality: m.subLocality,
        thoroughfare: m.thoroughfare,
        street: m.street,
        subThoroughfare: m.subThoroughfare,
      );
      return found.isEmpty ? null : found;
    } catch (_) {
      return null;
    }
  }
}

final addressLookupProvider = Provider<AddressLookup>((ref) => const AddressLookup());
