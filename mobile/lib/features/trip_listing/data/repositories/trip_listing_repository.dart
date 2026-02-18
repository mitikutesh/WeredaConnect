import 'dart:convert';

import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

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

/// Ethiopian phone number pattern: +251 followed by exactly 9 digits.
final _phoneRegex = RegExp(r'^\+251\d{9}$');

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

    // truckerPhone: Ethiopian E.164 format (+251XXXXXXXXX)
    if (!_phoneRegex.hasMatch(truckerPhone)) {
      throw TripValidationException(
        'truckerPhone',
        'Phone must be valid Ethiopian format (+251XXXXXXXXX).',
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
    _validateTripData(
      truckerName: truckerName,
      truckerPhone: truckerPhone,
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
  /// Validates that the trip exists and is in a cancellable state
  /// (not already delivered or cancelled). Sets status=cancelled,
  /// updates timestamp and version, then enqueues a sync operation.
  ///
  /// Throws [TripValidationException] if the trip cannot be cancelled.
  Future<void> cancelTrip({
    required String tripId,
    required String truckerId,
  }) async {
    final trip = await _isar.trips.filter().tripIdEqualTo(tripId).findFirst();

    if (trip == null) {
      throw TripValidationException('tripId', 'Trip not found.');
    }

    // Block cancellation of terminal-state trips
    if (trip.status == TripStatus.delivered) {
      throw TripValidationException(
        'status',
        'Cannot cancel a delivered trip.',
      );
    }
    if (trip.status == TripStatus.cancelled) {
      throw TripValidationException(
        'status',
        'Trip is already cancelled.',
      );
    }

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
