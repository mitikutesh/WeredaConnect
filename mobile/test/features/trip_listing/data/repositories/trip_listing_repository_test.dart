import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';

void main() {
  group('TripListingRepository', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;
    late TripListingRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('trip_repo_test_');
      isar = await Isar.open(
        [
          UserSchema,
          CropListingSchema,
          TripSchema,
          DeliverySchema,
          PaymentSchema,
          SyncOperationSchema,
        ],
        directory: tempDir.path,
        name: 'test_trip_repo_${DateTime.now().millisecondsSinceEpoch}',
      );
      syncQueue = SyncQueue(isar);
      repository = TripListingRepository(
        isar: isar,
        syncQueue: syncQueue,
      );
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('createTrip saves to Isar with correct fields', () async {
      final departureDate = DateTime.now().add(const Duration(days: 2)).toUtc();

      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departureDate,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      expect(trip.truckerId, 'trucker-001');
      expect(trip.fromWoreda, 'Bishoftu');
      expect(trip.toWoreda, 'Addis Ababa');
      expect(trip.capacity, 150.0);
      expect(trip.pricePerQuintal, 120.0);
      expect(trip.status, TripStatus.open);
      expect(trip.version, 1);
      expect(trip.matchedListings, isEmpty);
      expect(trip.tripId, isNotEmpty);

      // Verify persisted in Isar
      final fromDb =
          await isar.trips.filter().tripIdEqualTo(trip.tripId).findFirst();
      expect(fromDb, isNotNull);
      expect(fromDb!.fromWoreda, 'Bishoftu');
    });

    test('createTrip enqueues sync operation', () async {
      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      final pendingCount = await syncQueue.getPendingCount();
      expect(pendingCount, 1);

      final pending = await syncQueue.getPendingOperations();
      expect(pending.first.entityType, 'trip');
      expect(pending.first.action, 'create');
    });

    test('getTripsByTrucker returns only that trucker trips', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );
      await repository.createTrip(
        truckerId: 'trucker-002',
        truckerName: 'Test Trucker 2',
        truckerPhone: '+251911000001',
        fromWoreda: 'Adama',
        toWoreda: 'Hawassa',
        departureDate: departure,
        capacity: 100.0,
        pricePerQuintal: 80.0,
      );
      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Debre Birhan',
        toWoreda: 'Adama',
        departureDate: departure,
        capacity: 200.0,
        pricePerQuintal: 100.0,
      );

      final trips = await repository.getTripsByTrucker('trucker-001');
      expect(trips.length, 2);
      expect(
        trips.every((t) => t.truckerId == 'trucker-001'),
        isTrue,
      );
    });

    test('getOpenTrips returns only open trips', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );
      final tripToCancel = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Adama',
        toWoreda: 'Hawassa',
        departureDate: departure,
        capacity: 100.0,
        pricePerQuintal: 80.0,
      );

      // Cancel one trip
      await repository.cancelTrip(
        tripId: tripToCancel.tripId,
        truckerId: 'trucker-001',
      );

      final openTrips = await repository.getOpenTrips();
      expect(openTrips.length, 1);
      expect(openTrips.first.fromWoreda, 'Bishoftu');
    });

    test('getOpenTrips with fromWoreda filter', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );
      await repository.createTrip(
        truckerId: 'trucker-002',
        truckerName: 'Test Trucker 2',
        truckerPhone: '+251911000001',
        fromWoreda: 'Adama',
        toWoreda: 'Hawassa',
        departureDate: departure,
        capacity: 100.0,
        pricePerQuintal: 80.0,
      );

      final filtered = await repository.getOpenTrips(fromWoreda: 'Bishoftu');
      expect(filtered.length, 1);
      expect(filtered.first.fromWoreda, 'Bishoftu');
    });

    test('getOpenTrips with toWoreda filter', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );
      await repository.createTrip(
        truckerId: 'trucker-002',
        truckerName: 'Test Trucker 2',
        truckerPhone: '+251911000001',
        fromWoreda: 'Adama',
        toWoreda: 'Hawassa',
        departureDate: departure,
        capacity: 100.0,
        pricePerQuintal: 80.0,
      );

      final filtered = await repository.getOpenTrips(toWoreda: 'Hawassa');
      expect(filtered.length, 1);
      expect(filtered.first.toWoreda, 'Hawassa');
    });

    test('cancelTrip marks as cancelled and enqueues sync', () async {
      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      await repository.cancelTrip(
        tripId: trip.tripId,
        truckerId: 'trucker-001',
      );

      final fromDb =
          await isar.trips.filter().tripIdEqualTo(trip.tripId).findFirst();
      expect(fromDb!.status, TripStatus.cancelled);
      expect(fromDb.version, 2);

      // Should have 2 sync ops: create + delete
      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 2);
      expect(pending.last.action, 'delete');
    });

    // --- Contract Edge Cases: Capacity Boundaries ---

    test('createTrip with minimum capacity 0.1 quintals', () async {
      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 0.1,
        pricePerQuintal: 120.0,
      );

      expect(trip.capacity, 0.1);
      final fromDb =
          await isar.trips.filter().tripIdEqualTo(trip.tripId).findFirst();
      expect(fromDb!.capacity, closeTo(0.1, 0.001));
    });

    test('createTrip with maximum capacity 1000 quintals', () async {
      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 1000.0,
        pricePerQuintal: 120.0,
      );

      expect(trip.capacity, 1000.0);
    });

    test('createTrip with decimal precision in capacity', () async {
      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 150.75,
        pricePerQuintal: 120.50,
      );

      expect(trip.capacity, closeTo(150.75, 0.001));
      expect(trip.pricePerQuintal, closeTo(120.50, 0.001));
    });

    // --- Contract Edge Cases: Multilingual Woreda Names ---

    test('createTrip with Amharic woreda names', () async {
      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'ታደሰ ወርቁ',
        truckerPhone: '+251911000000',
        fromWoreda: 'ቢሾፍቱ',
        toWoreda: 'አዲስ አበባ',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      expect(trip.fromWoreda, 'ቢሾፍቱ');
      expect(trip.toWoreda, 'አዲስ አበባ');

      final fromDb =
          await isar.trips.filter().tripIdEqualTo(trip.tripId).findFirst();
      expect(fromDb!.fromWoreda, 'ቢሾፍቱ');
      expect(fromDb.toWoreda, 'አዲስ አበባ');
    });

    test('getOpenTrips filters by Amharic woreda name', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'ቢሾፍቱ',
        toWoreda: 'አዲስ አበባ',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );
      await repository.createTrip(
        truckerId: 'trucker-002',
        truckerName: 'Test Trucker 2',
        truckerPhone: '+251911000001',
        fromWoreda: 'Adama',
        toWoreda: 'Hawassa',
        departureDate: departure,
        capacity: 100.0,
        pricePerQuintal: 80.0,
      );

      final filtered = await repository.getOpenTrips(fromWoreda: 'ቢሾፍቱ');
      expect(filtered.length, 1);
      expect(filtered.first.fromWoreda, 'ቢሾፍቱ');
    });

    // --- Contract Edge Cases: Version Control ---

    test('createTrip always starts at version 1', () async {
      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      expect(trip.version, 1);
    });

    test('cancelTrip increments version to 2', () async {
      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      await repository.cancelTrip(
        tripId: trip.tripId,
        truckerId: 'trucker-001',
      );

      final fromDb =
          await isar.trips.filter().tripIdEqualTo(trip.tripId).findFirst();
      expect(fromDb!.version, 2);
    });

    // --- Contract Edge Cases: Cancel Behavior ---

    test('cancelTrip on non-existent trip throws TripValidationException',
        () async {
      expect(
        () => repository.cancelTrip(
          tripId: 'nonexistent-trip-id',
          truckerId: 'trucker-001',
        ),
        throwsA(isA<TripValidationException>()),
      );
    });

    test('cancelled trip excluded from getOpenTrips', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      final trip1 = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );
      await repository.createTrip(
        truckerId: 'trucker-002',
        truckerName: 'Test Trucker 2',
        truckerPhone: '+251911000001',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 100.0,
        pricePerQuintal: 80.0,
      );

      // Cancel first trip
      await repository.cancelTrip(
        tripId: trip1.tripId,
        truckerId: 'trucker-001',
      );

      final openTrips = await repository.getOpenTrips(fromWoreda: 'Bishoftu');
      expect(openTrips.length, 1);
      expect(openTrips.first.truckerId, 'trucker-002');
    });

    // --- Contract Edge Cases: Sync Queue Integration ---

    test('createTrip sync operation contains correct entityType', () async {
      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 1);
      expect(pending.first.entityType, 'trip');
      expect(pending.first.action, 'create');
      expect(pending.first.data, contains('"truckerId":"trucker-001"'));
      expect(pending.first.data, contains('"fromWoreda":"Bishoftu"'));
      expect(pending.first.data, contains('"capacity":150.0'));
    });

    test('multiple trips create separate sync operations', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );
      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Adama',
        toWoreda: 'Hawassa',
        departureDate: departure,
        capacity: 200.0,
        pricePerQuintal: 80.0,
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 2);

      // Each should have unique operation and entity IDs
      expect(pending[0].entityId, isNot(pending[1].entityId));
      expect(pending[0].operationId, isNot(pending[1].operationId));
    });

    // --- Contract Edge Cases: UUID Generation ---

    test('createTrip generates unique tripId for each trip', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      final trip1 = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );
      final trip2 = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Adama',
        toWoreda: 'Hawassa',
        departureDate: departure,
        capacity: 100.0,
        pricePerQuintal: 80.0,
      );

      expect(trip1.tripId, isNot(trip2.tripId));
      expect(trip1.tripId.length, greaterThan(0));
      expect(trip2.tripId.length, greaterThan(0));
    });

    // --- Contract Edge Cases: Status Starts as Open ---

    test('createTrip always sets status to open', () async {
      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      expect(trip.status, TripStatus.open);
      expect(trip.matchedListings, isEmpty);
    });

    // --- Contract Edge Cases: Timestamp Handling ---

    test('createTrip sets createdAt and updatedAt to current UTC time',
        () async {
      final before = DateTime.now().toUtc();

      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      final after = DateTime.now().toUtc();

      expect(
        trip.createdAt.isAfter(before.subtract(const Duration(seconds: 1))),
        isTrue,
      );
      expect(
        trip.createdAt.isBefore(after.add(const Duration(seconds: 1))),
        isTrue,
      );
      expect(trip.updatedAt, trip.createdAt);
    });

    // --- Contract Edge Cases: Both fromWoreda and toWoreda Filters ---

    test('getOpenTrips with both fromWoreda and toWoreda filters', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'T1',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );
      await repository.createTrip(
        truckerId: 'trucker-002',
        truckerName: 'T2',
        truckerPhone: '+251911000001',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Hawassa',
        departureDate: departure,
        capacity: 100.0,
        pricePerQuintal: 80.0,
      );
      await repository.createTrip(
        truckerId: 'trucker-003',
        truckerName: 'T3',
        truckerPhone: '+251911000002',
        fromWoreda: 'Adama',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 200.0,
        pricePerQuintal: 100.0,
      );

      final filtered = await repository.getOpenTrips(
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
      );
      expect(filtered.length, 1);
      expect(filtered.first.truckerId, 'trucker-001');
    });

    // --- Contract Edge Cases: getTripsByTrucker Includes All Statuses ---

    test('getTripsByTrucker includes cancelled trips', () async {
      final departure = DateTime.now().add(const Duration(days: 2)).toUtc();

      final trip = await repository.createTrip(
        truckerId: 'trucker-001',
        truckerName: 'Test Trucker',
        truckerPhone: '+251911000000',
        fromWoreda: 'Bishoftu',
        toWoreda: 'Addis Ababa',
        departureDate: departure,
        capacity: 150.0,
        pricePerQuintal: 120.0,
      );

      await repository.cancelTrip(
        tripId: trip.tripId,
        truckerId: 'trucker-001',
      );

      final trips = await repository.getTripsByTrucker('trucker-001');
      expect(trips.length, 1);
      expect(trips.first.status, TripStatus.cancelled);
    });

    test('getTripsByTrucker returns empty list for unknown trucker', () async {
      final trips = await repository.getTripsByTrucker('nonexistent-trucker');
      expect(trips, isEmpty);
    });

    test('getOpenTrips returns empty when no trips exist', () async {
      final trips = await repository.getOpenTrips();
      expect(trips, isEmpty);
    });

    // --- F-SEC-04: Client-side Validation Tests ---

    group('_validateTripData via createTrip', () {
      final validDeparture =
          DateTime.now().add(const Duration(days: 2)).toUtc();

      test('same fromWoreda and toWoreda throws TripValidationException',
          () async {
        expect(
          () => repository.createTrip(
            truckerId: 'trucker-001',
            truckerName: 'Test Trucker',
            truckerPhone: '+251911000000',
            fromWoreda: 'Bishoftu',
            toWoreda: 'Bishoftu',
            departureDate: validDeparture,
            capacity: 150.0,
            pricePerQuintal: 120.0,
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'toWoreda'),
          ),
        );
      });

      test('capacity below 0.1 throws TripValidationException', () async {
        expect(
          () => repository.createTrip(
            truckerId: 'trucker-001',
            truckerName: 'Test Trucker',
            truckerPhone: '+251911000000',
            fromWoreda: 'Bishoftu',
            toWoreda: 'Addis Ababa',
            departureDate: validDeparture,
            capacity: 0.05,
            pricePerQuintal: 120.0,
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'capacity'),
          ),
        );
      });

      test('capacity above 1000 throws TripValidationException', () async {
        expect(
          () => repository.createTrip(
            truckerId: 'trucker-001',
            truckerName: 'Test Trucker',
            truckerPhone: '+251911000000',
            fromWoreda: 'Bishoftu',
            toWoreda: 'Addis Ababa',
            departureDate: validDeparture,
            capacity: 1001.0,
            pricePerQuintal: 120.0,
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'capacity'),
          ),
        );
      });

      test('price below 0.01 throws TripValidationException', () async {
        expect(
          () => repository.createTrip(
            truckerId: 'trucker-001',
            truckerName: 'Test Trucker',
            truckerPhone: '+251911000000',
            fromWoreda: 'Bishoftu',
            toWoreda: 'Addis Ababa',
            departureDate: validDeparture,
            capacity: 150.0,
            pricePerQuintal: 0.005,
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'pricePerQuintal'),
          ),
        );
      });

      test('price above 10000 throws TripValidationException', () async {
        expect(
          () => repository.createTrip(
            truckerId: 'trucker-001',
            truckerName: 'Test Trucker',
            truckerPhone: '+251911000000',
            fromWoreda: 'Bishoftu',
            toWoreda: 'Addis Ababa',
            departureDate: validDeparture,
            capacity: 150.0,
            pricePerQuintal: 10001.0,
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'pricePerQuintal'),
          ),
        );
      });

      test('past departure date throws TripValidationException', () async {
        expect(
          () => repository.createTrip(
            truckerId: 'trucker-001',
            truckerName: 'Test Trucker',
            truckerPhone: '+251911000000',
            fromWoreda: 'Bishoftu',
            toWoreda: 'Addis Ababa',
            departureDate: DateTime.now().subtract(const Duration(hours: 1)),
            capacity: 150.0,
            pricePerQuintal: 120.0,
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'departureDate'),
          ),
        );
      });

      test('departure date over 30 days throws TripValidationException',
          () async {
        expect(
          () => repository.createTrip(
            truckerId: 'trucker-001',
            truckerName: 'Test Trucker',
            truckerPhone: '+251911000000',
            fromWoreda: 'Bishoftu',
            toWoreda: 'Addis Ababa',
            departureDate: DateTime.now().add(const Duration(days: 31)).toUtc(),
            capacity: 150.0,
            pricePerQuintal: 120.0,
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'departureDate'),
          ),
        );
      });

      test('invalid phone format throws TripValidationException', () async {
        final invalidPhones = [
          '+251abc123456',
          '+2519110000',
          '0911000000',
          '+252911000000',
          '+25191100000000',
        ];

        for (final phone in invalidPhones) {
          expect(
            () => repository.createTrip(
              truckerId: 'trucker-001',
              truckerName: 'Test Trucker',
              truckerPhone: phone,
              fromWoreda: 'Bishoftu',
              toWoreda: 'Addis Ababa',
              departureDate: validDeparture,
              capacity: 150.0,
              pricePerQuintal: 120.0,
            ),
            throwsA(
              isA<TripValidationException>()
                  .having((e) => e.field, 'field', 'truckerPhone'),
            ),
            reason: 'Phone "$phone" should fail validation',
          );
        }
      });

      test('empty trucker name throws TripValidationException', () async {
        expect(
          () => repository.createTrip(
            truckerId: 'trucker-001',
            truckerName: '',
            truckerPhone: '+251911000000',
            fromWoreda: 'Bishoftu',
            toWoreda: 'Addis Ababa',
            departureDate: validDeparture,
            capacity: 150.0,
            pricePerQuintal: 120.0,
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'truckerName'),
          ),
        );
      });

      test('trucker name over 100 chars throws TripValidationException',
          () async {
        expect(
          () => repository.createTrip(
            truckerId: 'trucker-001',
            truckerName: 'A' * 101,
            truckerPhone: '+251911000000',
            fromWoreda: 'Bishoftu',
            toWoreda: 'Addis Ababa',
            departureDate: validDeparture,
            capacity: 150.0,
            pricePerQuintal: 120.0,
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'truckerName'),
          ),
        );
      });
    });

    // --- F-SEC-04: cancelTrip Terminal State Validation ---

    group('cancelTrip terminal state validation', () {
      test('cancel a delivered trip throws TripValidationException', () async {
        final trip = await repository.createTrip(
          truckerId: 'trucker-001',
          truckerName: 'Test Trucker',
          truckerPhone: '+251911000000',
          fromWoreda: 'Bishoftu',
          toWoreda: 'Addis Ababa',
          departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
          capacity: 150.0,
          pricePerQuintal: 120.0,
        );

        // Simulate delivered status directly in Isar
        await isar.writeTxn(() async {
          trip.status = TripStatus.delivered;
          trip.updatedAt = DateTime.now().toUtc();
          await isar.trips.put(trip);
        });

        expect(
          () => repository.cancelTrip(
            tripId: trip.tripId,
            truckerId: 'trucker-001',
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'status')
                .having(
                  (e) => e.message,
                  'message',
                  contains('delivered'),
                ),
          ),
        );
      });

      test('cancel an already cancelled trip throws TripValidationException',
          () async {
        final trip = await repository.createTrip(
          truckerId: 'trucker-001',
          truckerName: 'Test Trucker',
          truckerPhone: '+251911000000',
          fromWoreda: 'Bishoftu',
          toWoreda: 'Addis Ababa',
          departureDate: DateTime.now().add(const Duration(days: 2)).toUtc(),
          capacity: 150.0,
          pricePerQuintal: 120.0,
        );

        await repository.cancelTrip(
          tripId: trip.tripId,
          truckerId: 'trucker-001',
        );

        expect(
          () => repository.cancelTrip(
            tripId: trip.tripId,
            truckerId: 'trucker-001',
          ),
          throwsA(
            isA<TripValidationException>()
                .having((e) => e.field, 'field', 'status')
                .having(
                  (e) => e.message,
                  'message',
                  contains('cancelled'),
                ),
          ),
        );
      });
    });
  });
}
