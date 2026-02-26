import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';

const _uuid = Uuid();

/// Realistic Ethiopian crop data:
/// (cropType, quantity in quintals, price ETB/kg, description).
const _cropData = [
  (
    'teff',
    15.0,
    65.0,
    'White teff, cleaned and ready. Harvested this season from Ada woreda.',
  ),
  (
    'wheat',
    30.0,
    35.0,
    'Durum wheat, grade A quality from Arsi zone. Low moisture content.',
  ),
  (
    'coffee',
    8.0,
    120.0,
    'Yirgacheffe washed coffee, sundried on raised beds. Export grade.',
  ),
  (
    'maize',
    40.0,
    22.0,
    'Yellow maize, well dried. Suitable for animal feed or flour milling.',
  ),
  (
    'barley',
    20.0,
    30.0,
    'Food barley from Bale highlands. Suitable for tella and injera mix.',
  ),
  (
    'chickpea',
    10.0,
    55.0,
    'Desi chickpea, hand-sorted. Good for shiro and hummus production.',
  ),
];

/// Farm locations: (latitude, longitude, woreda name) — real Ethiopian coordinates.
const _locations = [
  (8.9806, 38.7578, 'Ada, Bishoftu'),
  (7.0504, 38.4955, 'Hawassa Zuria'),
  (8.5400, 39.2700, 'Adama'),
  (9.0200, 38.7500, 'Akaki Kality'),
  (7.2800, 39.7100, 'Bale Robe'),
  (6.0600, 38.4800, 'Yirgacheffe'),
];

/// All listing statuses in lifecycle order.
const _mixedStatuses = [
  ListingStatus.draft,
  ListingStatus.active,
  ListingStatus.reserved,
  ListingStatus.sold,
  ListingStatus.expired,
];

/// Seeds mock crop listings where [userId] is the farmer.
///
/// When [statusFilter] is null, seeds one listing per status (mixed view).
/// When [statusFilter] is set, seeds 3 listings all at that status.
Future<void> seedFarmerMockListings(
  Isar isar,
  String userId, {
  String? statusFilter,
}) async {
  if (statusFilter == null) {
    await _seedMixed(isar, userId);
  } else {
    await _seedForStatus(isar, userId, statusFilter);
  }
}

/// Seeds one listing per status: draft, active, sold, expired.
Future<void> _seedMixed(Isar isar, String userId) async {
  for (var i = 0; i < _mixedStatuses.length; i++) {
    await _createAtStatus(isar, userId, _mixedStatuses[i], i);
  }
}

/// Seeds 3 listings all at the given [status].
Future<void> _seedForStatus(Isar isar, String userId, String status) async {
  final listingStatus = ListingStatus.values.byName(status);
  for (var i = 0; i < 3; i++) {
    await _createAtStatus(isar, userId, listingStatus, i);
  }
}

/// Creates a single crop listing at the given [status].
Future<void> _createAtStatus(
  Isar isar,
  String userId,
  ListingStatus status,
  int dataIndex,
) async {
  final idx = dataIndex % _cropData.length;
  final (cropType, quantity, pricePerKg, description) = _cropData[idx];
  final (lat, lng, weredaName) = _locations[idx];
  final now = DateTime.now().toUtc();

  final location = LatLng()
    ..latitude = lat
    ..longitude = lng;

  final listing = CropListing()
    ..listingId = _uuid.v4()
    ..farmerId = userId
    ..cropType = cropType
    ..quantity = quantity
    ..pricePerKg = pricePerKg
    ..location = location
    ..locationName = weredaName
    ..description = description
    ..status = status
    ..createdAt = now.subtract(Duration(hours: dataIndex * 12))
    ..updatedAt = now
    ..version = 1;

  await isar.writeTxn(() async {
    await isar.cropListings.put(listing);
  });
}
