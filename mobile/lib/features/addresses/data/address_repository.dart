import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../core/network/api_client.dart';
import '../../../core/providers.dart';

/// Where a store order or a won prize is delivered, laid out the Saudi way.
class ShippingAddress {
  const ShippingAddress({
    required this.id,
    required this.fullName,
    required this.phone,
    required this.city,
    required this.district,
    required this.street,
    this.buildingNumber,
    this.notes,
    this.shortAddress,
    this.latitude,
    this.longitude,
    this.isDefault = false,
  });

  final String id;

  /// The recipient, who may not be the account holder.
  final String fullName;

  /// +9665XXXXXXXX, as the server stores it.
  final String phone;
  final String city;
  final String district;
  final String street;
  final String? buildingNumber;
  final String? notes;

  /// The National Address short code, e.g. RRRD2929.
  final String? shortAddress;
  final double? latitude;
  final double? longitude;
  final bool isDefault;

  bool get hasLocation => latitude != null && longitude != null;

  /// "١٢٣٤ طريق الملك فهد، العليا، الرياض": what goes on the parcel, without the person.
  String get summary => [
        [if (buildingNumber != null) buildingNumber, street].join(' '),
        district,
        city,
      ].where((s) => s.trim().isNotEmpty).join('، ');

  factory ShippingAddress.fromJson(Map<String, dynamic> j) => ShippingAddress(
        id: j['id'] as String,
        fullName: j['fullName'] as String? ?? '',
        phone: j['phone'] as String? ?? '',
        city: j['city'] as String? ?? '',
        district: j['district'] as String? ?? '',
        street: j['street'] as String? ?? '',
        buildingNumber: j['buildingNumber'] as String?,
        notes: j['notes'] as String?,
        shortAddress: j['shortAddress'] as String?,
        latitude: (j['latitude'] as num?)?.toDouble(),
        longitude: (j['longitude'] as num?)?.toDouble(),
        isDefault: j['isDefault'] as bool? ?? false,
      );
}

/// What the form sends. The server checks every field again.
class AddressInput {
  const AddressInput({
    required this.fullName,
    required this.phone,
    required this.city,
    required this.district,
    required this.street,
    this.buildingNumber,
    this.notes,
    this.shortAddress,
    this.latitude,
    this.longitude,
    this.isDefault = false,
  });

  final String fullName;
  final String phone;
  final String city;
  final String district;
  final String street;
  final String? buildingNumber;
  final String? notes;
  final String? shortAddress;
  final double? latitude;
  final double? longitude;
  final bool isDefault;

  Map<String, dynamic> toJson() => {
        'fullName': fullName,
        'phone': phone,
        'city': city,
        'district': district,
        'street': street,
        'buildingNumber': buildingNumber,
        'notes': notes,
        'shortAddress': shortAddress,
        'latitude': latitude,
        'longitude': longitude,
        'isDefault': isDefault,
        'country': 'SA',
      };
}

class AddressRepository {
  AddressRepository(this._api);

  final ApiClient _api;

  Future<List<ShippingAddress>> list() async {
    final data = await _api.get('/me/addresses');
    return (data as List)
        .map((e) => ShippingAddress.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<ShippingAddress> create(AddressInput input) async {
    final data = await _api.post('/me/addresses', body: input.toJson());
    return ShippingAddress.fromJson(data as Map<String, dynamic>);
  }

  /// The returned address can carry a new id: one an order already went to is kept as it was
  /// on the server, and the change becomes a new address.
  Future<ShippingAddress> update(String id, AddressInput input) async {
    final data = await _api.put('/me/addresses/$id', body: input.toJson());
    return ShippingAddress.fromJson(data as Map<String, dynamic>);
  }

  Future<ShippingAddress> setDefault(String id) async {
    final data = await _api.post('/me/addresses/$id/default');
    return ShippingAddress.fromJson(data as Map<String, dynamic>);
  }

  Future<void> delete(String id) => _api.delete('/me/addresses/$id');
}

final addressRepositoryProvider =
    Provider((ref) => AddressRepository(ref.watch(apiClientProvider)));

/// The signed-in customer's addresses, the default first.
final myAddressesProvider = FutureProvider.autoDispose(
    (ref) => ref.watch(addressRepositoryProvider).list());
