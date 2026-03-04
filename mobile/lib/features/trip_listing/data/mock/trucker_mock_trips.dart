import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/trip.dart';

const _uuid = Uuid();

/// Realistic Ethiopian truck routes:
/// (from, to, capacity quintals, price ETB/quintal, truckerName).
const _routeData = [
  ('Bishoftu', 'Addis Ababa', 120.0, 180.0, 'Kebede Transport'),
  ('Adama', 'Addis Ababa', 150.0, 250.0, 'Dawit Logistics'),
  ('Hawassa', 'Addis Ababa', 200.0, 450.0, 'Getachew Cargo'),
  ('Bale Robe', 'Adama', 100.0, 350.0, 'Tesfaye Movers'),
  ('Jimma', 'Addis Ababa', 180.0, 400.0, 'Abdi Transport'),
];

/// Ethiopian phone numbers for mock truckers.
const _phones = [
  '+251912345678',
  '+251923456789',
  '+251934567890',
  '+251945678901',
  '+251956789012',
];

/// All trip statuses in lifecycle order.
const _mixedStatuses = [
  TripStatus.open,
  TripStatus.matched,
  TripStatus.inTransit,
  TripStatus.delivered,
  TripStatus.cancelled,
];

/// Seeds mock trips where [userId] is the trucker.
///
/// When [statusFilter] is null, seeds one trip per status (mixed view).
/// When [statusFilter] is set, seeds 3 trips all at that status.
Future<void> seedTruckerMockTrips(
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

/// Seeds one trip per status: open, matched, inTransit, delivered, cancelled.
Future<void> _seedMixed(Isar isar, String userId) async {
  for (var i = 0; i < _mixedStatuses.length; i++) {
    await _createAtStatus(isar, userId, _mixedStatuses[i], i);
  }
}

/// Seeds 3 trips all at the given [status].
Future<void> _seedForStatus(Isar isar, String userId, String status) async {
  final tripStatus = TripStatus.values.byName(status);
  for (var i = 0; i < 3; i++) {
    await _createAtStatus(isar, userId, tripStatus, i);
  }
}

/// Creates a single trip at the given [status].
Future<void> _createAtStatus(
  Isar isar,
  String userId,
  TripStatus status,
  int dataIndex,
) async {
  final idx = dataIndex % _routeData.length;
  final (from, to, capacity, price, truckerName) = _routeData[idx];
  final phone = _phones[idx];
  final now = DateTime.now().toUtc();

  // Terminal statuses use past departure dates for realism;
  // active statuses use future dates.
  final departureDate =
      (status == TripStatus.delivered || status == TripStatus.cancelled)
          ? now.subtract(Duration(days: 2 + dataIndex))
          : now.add(Duration(days: 1 + dataIndex));

  // Matched/inTransit/delivered trips have associated crop listings.
  final matchedListings = (status == TripStatus.matched ||
          status == TripStatus.inTransit ||
          status == TripStatus.delivered)
      ? [_uuid.v4(), _uuid.v4()]
      : <String>[];

  final trip = Trip()
    ..tripId = _uuid.v4()
    ..truckerId = userId
    ..truckerName = truckerName
    ..truckerPhone = phone
    ..fromWoreda = from
    ..toWoreda = to
    ..departureDate = departureDate
    ..capacity = capacity
    ..pricePerQuintal = price
    ..status = status
    ..matchedListings = matchedListings
    ..createdAt = now.subtract(Duration(hours: dataIndex * 8))
    ..updatedAt = now
    ..version = 1;

  await isar.writeTxn(() async {
    await isar.trips.put(trip);
  });
}
