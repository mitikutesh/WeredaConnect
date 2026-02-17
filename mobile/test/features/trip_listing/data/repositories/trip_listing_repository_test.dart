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
      final fromDb = await isar.trips
          .filter()
          .tripIdEqualTo(trip.tripId)
          .findFirst();
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

      final filtered =
          await repository.getOpenTrips(fromWoreda: 'Bishoftu');
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

      final filtered =
          await repository.getOpenTrips(toWoreda: 'Hawassa');
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

      final fromDb = await isar.trips
          .filter()
          .tripIdEqualTo(trip.tripId)
          .findFirst();
      expect(fromDb!.status, TripStatus.cancelled);
      expect(fromDb.version, 2);

      // Should have 2 sync ops: create + delete
      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 2);
      expect(pending.last.action, 'delete');
    });
  });
}
