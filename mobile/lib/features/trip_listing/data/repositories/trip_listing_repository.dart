import 'dart:convert';

import 'package:flutter/foundation.dart';

import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/trip_departure.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/features/trip_listing/data/trip_destination.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';

/// Validation error thrown when trip data fails client-side checks.
///
/// The [field] and [message] can be used by the BLoC to display
/// localized error messages to the user.
class TripValidationException implements Exception {
  final String field;
  final String message;

  TripValidationException(this.field, this.message);

  @override
  String toString() => 'TripValidationException($field): $message';
}

/// Offline-first repository for trip listing CRUD operations.
///
/// All writes go to local Isar first, then enqueue a sync operation.
/// Reads are always from Isar (fast, works offline).
class TripListingRepository extends BaseTripListingRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;
  static const _uuid = Uuid();

  /// The clock for the open-trips cutoff (L-7); the device clock by default.
  final DateTime Function() _clock;

  TripListingRepository({
    required Isar isar,
    required SyncQueue syncQueue,
    DateTime Function()? clock,
  })  : _isar = isar,
        _syncQueue = syncQueue,
        _clock = clock ?? DateTime.now;

  /// Validate trip data before saving to Isar / enqueueing sync.
  ///
  /// Mirrors server-side validation rules from the trip-listing-api contract.
  /// Throws [TripValidationException] on the first validation failure.
  void _validateTripData({
    required String truckerName,
    required String truckerPhone,
    required String fromWoreda,
    required String toWoreda,
    required DateTime departureDate,
    required double capacity,
    required double pricePerQuintal,
  }) {
    // truckerName: 1-100 characters
    if (truckerName.isEmpty || truckerName.length > 100) {
      throw TripValidationException(
        'truckerName',
        'Name must be between 1 and 100 characters.',
      );
    }

    // truckerPhone: Ethiopian phone (accepts +251, 00251, or 0 prefix)
    if (!isValidEthiopianPhone(truckerPhone)) {
      throw TripValidationException(
        'truckerPhone',
        'Phone must be valid Ethiopian format (+251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX).',
      );
    }

    // fromWoreda: required, 1-100 characters
    if (fromWoreda.isEmpty || fromWoreda.length > 100) {
      throw TripValidationException(
        'fromWoreda',
        'Origin woreda must be between 1 and 100 characters.',
      );
    }

    // toWoreda: required, 1-100 characters, must differ from fromWoreda
    if (toWoreda.isEmpty || toWoreda.length > 100) {
      throw TripValidationException(
        'toWoreda',
        'Destination woreda must be between 1 and 100 characters.',
      );
    }
    if (fromWoreda == toWoreda) {
      throw TripValidationException(
        'toWoreda',
        'Origin and destination woreda must be different.',
      );
    }

    // departureDate: must be in the future and within 30 days
    final now = DateTime.now().toUtc();
    if (!departureDate.isAfter(now)) {
      throw TripValidationException(
        'departureDate',
        'Departure date must be in the future.',
      );
    }
    if (departureDate.isAfter(now.add(const Duration(days: 30)))) {
      throw TripValidationException(
        'departureDate',
        'Departure date must be within 30 days.',
      );
    }

    // capacity: 0.1-1000 quintals
    if (capacity < 0.1 || capacity > 1000) {
      throw TripValidationException(
        'capacity',
        'Capacity must be between 0.1 and 1,000 quintals.',
      );
    }

    // pricePerQuintal: 0.01-10000 ETB
    if (pricePerQuintal < 0.01 || pricePerQuintal > 10000) {
      throw TripValidationException(
        'pricePerQuintal',
        'Price must be between 0.01 and 10,000 ETB per quintal.',
      );
    }
  }

  /// Create a new trip listing.
  ///
  /// Validates input, generates a UUID, saves to Isar with status=open
  /// and version=1, then enqueues a sync operation for server upload.
  ///
  /// Throws [TripValidationException] if input data is invalid.
  @override
  Future<Trip> createTrip({
    required String truckerId,
    required String truckerName,
    required String truckerPhone,
    required String fromWoreda,
    required String toWoreda,
    required DateTime departureDate,
    required double capacity,
    required double pricePerQuintal,
    String? vehiclePlate,
    String? vehicleDescription,
    String? fromPlaceId,
    String? toPlaceId,
  }) async {
    // Normalize phone to canonical E.164 before validation/storage
    final normalizedPhone =
        normalizeEthiopianPhone(truckerPhone) ?? truckerPhone;

    _validateTripData(
      truckerName: truckerName,
      truckerPhone: normalizedPhone,
      fromWoreda: fromWoreda,
      toWoreda: toWoreda,
      departureDate: departureDate,
      capacity: capacity,
      pricePerQuintal: pricePerQuintal,
    );

    final tripId = _uuid.v4();
    final now = DateTime.now().toUtc();

    final trip = Trip()
      ..tripId = tripId
      ..truckerId = truckerId
      ..truckerName = truckerName
      ..truckerPhone = normalizedPhone
      ..vehiclePlate = vehiclePlate
      ..vehicleDescription = vehicleDescription
      ..fromWoreda = fromWoreda
      ..toWoreda = toWoreda
      ..fromPlaceId = fromPlaceId
      ..toPlaceId = toPlaceId
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
    // Only include server-whitelisted fields in data payload.
    // tripId is sent as entityId; createdAt/updatedAt are set server-side.
    final syncData = <String, dynamic>{
      'truckerId': truckerId,
      'truckerName': truckerName,
      'truckerPhone': normalizedPhone,
      'fromWoreda': fromWoreda,
      'toWoreda': toWoreda,
      'departureDate': departureDate.toUtc().toIso8601String(),
      'capacity': capacity,
      'pricePerQuintal': pricePerQuintal,
      'status': trip.status.name,
      'matchedListings': <String>[],
      'version': 1,
      ...tripDestinationFields(toPlaceId),
    };
    if (vehiclePlate != null && vehiclePlate.isNotEmpty) {
      syncData['vehiclePlate'] = vehiclePlate;
    }
    if (vehicleDescription != null && vehicleDescription.isNotEmpty) {
      syncData['vehicleDescription'] = vehicleDescription;
    }
    final data = jsonEncode(syncData);

    // The local Isar write above is authoritative (offline-first). Enqueue
    // failures (queue full / duplicate idempotency key) must NOT surface as a
    // save failure — the trip is persisted; the sync layer reconciles later.
    try {
      await _syncQueue.enqueue(
        userId: truckerId,
        entityType: 'trip',
        entityId: tripId,
        action: 'create',
        data: data,
        operationId: _uuid.v4(),
      );
    } on Object catch (e) {
      debugPrint(
        '[TripListingRepository] trip saved locally but sync enqueue '
        'failed: $e',
      );
    }

    return trip;
  }

  /// Get all trips for a specific trucker, sorted by createdAt descending.
  @override
  Future<List<Trip>> getTripsByTrucker(String truckerId) async {
    return _isar.trips
        .filter()
        .truckerIdEqualTo(truckerId)
        .sortByCreatedAtDesc()
        .findAll();
  }

  /// Get all open trips, optionally filtered by origin and/or destination woreda.
  ///
  /// L-7: only trips departing today or later in Ethiopia, as the server
  /// lists them, so a cache from before a sync never offers a truck that
  /// already left. A trucker's own trips ([getTripsByTrucker]) and a trip read
  /// by id keep every departure.
  @override
  Future<List<Trip>> getOpenTrips({
    String? fromWoreda,
    String? toWoreda,
  }) async {
    var query = _isar.trips
        .filter()
        .statusEqualTo(TripStatus.open)
        .departureDateGreaterThan(
          tripListingCutoff(_clock()),
          include: true,
        );

    if (fromWoreda != null) {
      query = query.fromWoredaEqualTo(fromWoreda);
    }

    if (toWoreda != null) {
      query = query.toWoredaEqualTo(toWoreda);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Shared trip status-transition logic.
  ///
  /// Looks up the trip, validates ownership and that [isValidFrom] accepts
  /// the trip as it is, applies the new status, bumps version, and enqueues
  /// an `update` sync op (every status change, the cancel included, is an
  /// update since ESC-17).
  ///
  /// Throws [TripValidationException] on any validation failure.
  Future<Trip> _transitionTrip({
    required String tripId,
    required String truckerId,
    required bool Function(Trip trip) isValidFrom,
    required String statusErrorMessage,
    required TripStatus newStatus,
  }) async {
    final trip = await _isar.trips.filter().tripIdEqualTo(tripId).findFirst();

    if (trip == null) {
      throw TripValidationException('tripId', 'Trip not found.');
    }

    if (trip.truckerId != truckerId) {
      throw TripValidationException(
        'truckerId',
        'Not authorized to modify this trip.',
      );
    }

    if (!isValidFrom(trip)) {
      throw TripValidationException('status', statusErrorMessage);
    }

    final now = DateTime.now().toUtc();

    // The server's optimistic-concurrency check (sync-protocol.md) expects the
    // version the client *had* before the change — the version the server still
    // holds. Capture it before bumping and send THAT; the server applies the op
    // when `clientVersion == serverVersion` and stores `clientVersion + 1`.
    // Sending the already-incremented version instead guarantees a version
    // conflict, after which the server-wins ConflictResolver reverts the local
    // status back to the server's (e.g. a cancel silently flips back to open).
    final baseVersion = trip.version;

    await _isar.writeTxn(() async {
      trip.status = newStatus;
      trip.updatedAt = now;
      // Bump to what the server will store on success, keeping the two in
      // lockstep so the next transition sends the correct base version.
      trip.version = baseVersion + 1;
      await _isar.trips.put(trip);
    });

    await _syncQueue.enqueue(
      userId: truckerId,
      entityType: 'trip',
      entityId: tripId,
      action: 'update',
      data: jsonEncode({
        'status': newStatus.name,
        'version': baseVersion,
      }),
      operationId: _uuid.v4(),
    );

    return trip;
  }

  /// Cancel a trip.
  ///
  /// Validates ownership, that the trip exists and is in a cancellable state:
  /// `open` or `matched`, the server's transitions (a trip in transit has
  /// crops on the road and is refused there). Sets status=cancelled.
  ///
  /// ESC-17 (trip-listing-api.md 2.4.0): queued as an `update` to
  /// `cancelled`, the same operation web sends, so the server checks it
  /// against the trip's status transitions. Builds before it queued a
  /// `delete`, which the server still takes.
  ///
  /// Throws [TripValidationException] if the trip cannot be cancelled.
  @override
  Future<void> cancelTrip({
    required String tripId,
    required String truckerId,
  }) async {
    await _transitionTrip(
      tripId: tripId,
      truckerId: truckerId,
      isValidFrom: (t) =>
          t.status == TripStatus.open || t.status == TripStatus.matched,
      statusErrorMessage: 'Trip cannot be cancelled in its current state.',
      newStatus: TripStatus.cancelled,
    );
  }

  /// Start a trip: `matched -> inTransit`, or `open -> inTransit` when the
  /// trip has bookings (ESC-15, trip-listing-api.md 2.0.0: `matched` means
  /// full, so a partly booked truck may leave). The trip detail screen offers
  /// Start for the same two cases.
  ///
  /// Throws [TripValidationException] if the trip cannot be started.
  @override
  Future<Trip> startTrip({
    required String tripId,
    required String truckerId,
  }) async {
    return _transitionTrip(
      tripId: tripId,
      truckerId: truckerId,
      isValidFrom: (t) =>
          t.status == TripStatus.matched ||
          (t.status == TripStatus.open && tripHasBookings(t)),
      statusErrorMessage:
          'Only a full trip, or an open one with bookings, can be started.',
      newStatus: TripStatus.inTransit,
    );
  }

  /// Mark a trip as delivered (transition from inTransit -> delivered).
  ///
  /// Throws [TripValidationException] if the trip cannot be marked delivered.
  @override
  Future<Trip> markDelivered({
    required String tripId,
    required String truckerId,
  }) async {
    return _transitionTrip(
      tripId: tripId,
      truckerId: truckerId,
      isValidFrom: (t) => t.status == TripStatus.inTransit,
      statusErrorMessage: 'Only in-transit trips can be marked as delivered.',
      newStatus: TripStatus.delivered,
    );
  }

  /// Get recent distinct routes for a trucker.
  ///
  /// Returns up to [limit] unique (fromWoreda, toWoreda) pairs from
  /// the trucker's most recent trips, useful for quick-fill chips.
  @override
  Future<List<({String from, String to})>> getRecentRoutes(
    String truckerId, {
    int limit = 5,
  }) async {
    final trips = await _isar.trips
        .filter()
        .truckerIdEqualTo(truckerId)
        .sortByCreatedAtDesc()
        .limit(20)
        .findAll();

    final seen = <String>{};
    final routes = <({String from, String to})>[];

    for (final trip in trips) {
      final key = '${trip.fromWoreda}|${trip.toWoreda}';
      if (seen.add(key)) {
        routes.add((from: trip.fromWoreda, to: trip.toWoreda));
        if (routes.length >= limit) break;
      }
    }

    return routes;
  }

  /// Get the most relevant trip for a trucker's current state.
  ///
  /// Priority order: inTransit > matched > open > recently delivered.
  /// Returns null if the trucker has no trips.
  @override
  Future<Trip?> getMostRelevantTrip(String truckerId) async {
    // Check inTransit first (highest priority)
    final inTransit = await _isar.trips
        .filter()
        .truckerIdEqualTo(truckerId)
        .statusEqualTo(TripStatus.inTransit)
        .sortByUpdatedAtDesc()
        .findFirst();
    if (inTransit != null) return inTransit;

    // Then matched
    final matched = await _isar.trips
        .filter()
        .truckerIdEqualTo(truckerId)
        .statusEqualTo(TripStatus.matched)
        .sortByUpdatedAtDesc()
        .findFirst();
    if (matched != null) return matched;

    // Then open
    final open = await _isar.trips
        .filter()
        .truckerIdEqualTo(truckerId)
        .statusEqualTo(TripStatus.open)
        .sortByCreatedAtDesc()
        .findFirst();
    if (open != null) return open;

    // Finally, most recently delivered
    final delivered = await _isar.trips
        .filter()
        .truckerIdEqualTo(truckerId)
        .statusEqualTo(TripStatus.delivered)
        .sortByUpdatedAtDesc()
        .findFirst();
    return delivered;
  }

  @override
  Future<Trip?> getTripById(String tripId) {
    return _isar.trips.filter().tripIdEqualTo(tripId).findFirst();
  }
}
