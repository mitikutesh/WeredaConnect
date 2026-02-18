import 'dart:convert';

import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

/// Offline-first repository for trip listing CRUD operations.
///
/// All writes go to local Isar first, then enqueue a sync operation.
/// Reads are always from Isar (fast, works offline).
class TripListingRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;
  static const _uuid = Uuid();

  TripListingRepository({
    required Isar isar,
    required SyncQueue syncQueue,
  })  : _isar = isar,
        _syncQueue = syncQueue;

  /// Create a new trip listing.
  ///
  /// Generates a UUID, saves to Isar with status=open and version=1,
  /// then enqueues a sync operation for server upload.
  Future<Trip> createTrip({
    required String truckerId,
    required String truckerName,
    required String truckerPhone,
    required String fromWoreda,
    required String toWoreda,
    required DateTime departureDate,
    required double capacity,
    required double pricePerQuintal,
  }) async {
    final tripId = _uuid.v4();
    final now = DateTime.now().toUtc();

    final trip = Trip()
      ..tripId = tripId
      ..truckerId = truckerId
      ..truckerName = truckerName
      ..truckerPhone = truckerPhone
      ..fromWoreda = fromWoreda
      ..toWoreda = toWoreda
      ..departureDate = departureDate
      ..capacity = capacity
      ..pricePerQuintal = pricePerQuintal
      ..status = TripStatus.open
      ..matchedListings = []
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;

    await _isar.writeTxn(() async {
      await _isar.trips.put(trip);
    });

    // Enqueue sync operation
    final data = jsonEncode({
      'tripId': tripId,
      'truckerId': truckerId,
      'truckerName': truckerName,
      'truckerPhone': truckerPhone,
      'fromWoreda': fromWoreda,
      'toWoreda': toWoreda,
      'departureDate': departureDate.toIso8601String(),
      'capacity': capacity,
      'pricePerQuintal': pricePerQuintal,
      'status': trip.status.name,
      'matchedListings': <String>[],
      'createdAt': now.toIso8601String(),
      'version': 1,
    });

    await _syncQueue.enqueue(
      userId: truckerId,
      entityType: 'trip',
      entityId: tripId,
      action: 'create',
      data: data,
      operationId: _uuid.v4(),
    );

    return trip;
  }

  /// Get all trips for a specific trucker, sorted by createdAt descending.
  Future<List<Trip>> getTripsByTrucker(String truckerId) async {
    return _isar.trips
        .filter()
        .truckerIdEqualTo(truckerId)
        .sortByCreatedAtDesc()
        .findAll();
  }

  /// Get all open trips, optionally filtered by origin and/or destination woreda.
  Future<List<Trip>> getOpenTrips({
    String? fromWoreda,
    String? toWoreda,
  }) async {
    var query = _isar.trips.filter().statusEqualTo(TripStatus.open);

    if (fromWoreda != null) {
      query = query.fromWoredaEqualTo(fromWoreda);
    }

    if (toWoreda != null) {
      query = query.toWoredaEqualTo(toWoreda);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Cancel a trip (soft delete).
  ///
  /// Sets status=cancelled, updates timestamp and version,
  /// then enqueues a sync operation.
  Future<void> cancelTrip({
    required String tripId,
    required String truckerId,
  }) async {
    final trip = await _isar.trips.filter().tripIdEqualTo(tripId).findFirst();

    if (trip == null) return;

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      trip.status = TripStatus.cancelled;
      trip.updatedAt = now;
      trip.version += 1;
      await _isar.trips.put(trip);
    });

    await _syncQueue.enqueue(
      userId: truckerId,
      entityType: 'trip',
      entityId: tripId,
      action: 'delete',
      data: jsonEncode({
        'tripId': tripId,
        'status': TripStatus.cancelled.name,
        'updatedAt': now.toIso8601String(),
        'version': trip.version,
      }),
      operationId: _uuid.v4(),
    );
  }
}
