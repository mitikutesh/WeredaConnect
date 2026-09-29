import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/conflict_resolver.dart';

void main() {
  group('ConflictResolver', () {
    late Isar isar;
    late Directory tempDir;
    late ConflictResolver resolver;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('conflict_resolver_test_');
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
        name: 'test_resolver_${DateTime.now().millisecondsSinceEpoch}',
      );
      resolver = ConflictResolver(isar);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    group('CropListing conflict resolution', () {
      test('resolves conflict when server version > client version', () async {
        // Arrange: Create local CropListing with version 1
        final listing = CropListing()
          ..listingId = 'listing-001'
          ..farmerId = 'farmer-001'
          ..cropType = 'teff'
          ..quantity = 10.0
          ..pricePerKg = 25.0
          ..location = (LatLng()
            ..latitude = 9.0
            ..longitude = 38.7)
          ..description = 'Old description'
          ..locationName = 'Bishoftu'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-10T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-10T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        // Create sync operation for update
        final operation = SyncOperation()
          ..operationId = 'op-001'
          ..idempotencyKey = 'key-001'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server data with version 5 (conflict - server is ahead)
        final serverData = {
          'listingId': 'listing-001',
          'farmerId': 'farmer-001',
          'cropType': 'wheat',
          'quantity': 15.5,
          'pricePerKg': 30.0,
          'location': {'latitude': 9.1, 'longitude': 38.8},
          'description': 'Server version description',
          'audioNoteRef': 'audio/farmer-001/listing-001.m4a',
          'status': 'active',
          'createdAt': '2026-02-10T10:00:00Z',
          'updatedAt': '2026-02-14T15:30:00Z',
        };

        // Act: Resolve conflict
        final result = await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 5,
        );

        // Assert: Local entity should be updated with server data
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-001')
            .findFirst();

        expect(updated, isNotNull);
        expect(
          updated!.version,
          equals(5),
        ); // Version updated to server version
        expect(updated.cropType, equals('wheat')); // Server data accepted
        expect(updated.quantity, equals(15.5));
        expect(updated.pricePerKg, equals(30.0));
        expect(updated.description, equals('Server version description'));
        expect(
          updated.audioNoteRef,
          equals('audio/farmer-001/listing-001.m4a'),
        );
        expect(updated.status, equals(ListingStatus.active));
        expect(updated.location.latitude, equals(9.1));
        expect(updated.location.longitude, equals(38.8));
        expect(
          updated.updatedAt.toUtc(),
          equals(DateTime.parse('2026-02-14T15:30:00Z').toUtc()),
        );

        // Assert: Result contains audit info
        expect(result['entityType'], equals('crop_listing'));
        expect(result['entityId'], equals('listing-001'));
        expect(result['serverVersion'], equals(5));
        expect(result['resolvedAt'], isNotNull);
      });

      test('handles conflict when client was editing locally', () async {
        // Arrange: Local entity with pending changes (version 2)
        final listing = CropListing()
          ..listingId = 'listing-002'
          ..farmerId = 'farmer-002'
          ..cropType = 'barley'
          ..quantity = 20.0
          ..pricePerKg = 22.0
          ..location = (LatLng()
            ..latitude = 10.0
            ..longitude = 39.0)
          ..description = 'Local edits not synced'
          ..locationName = 'Adama'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-12T08:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-14T12:00:00Z')
          ..version = 2;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-002'
          ..idempotencyKey = 'key-002'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-002'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server already at version 8 (someone else edited)
        final serverData = {
          'listingId': 'listing-002',
          'farmerId': 'farmer-002',
          'cropType': 'maize',
          'quantity': 50.0,
          'pricePerKg': 18.0,
          'location': {'latitude': 10.2, 'longitude': 39.1},
          'description': 'Updated by another device',
          'audioNoteRef': null,
          'status': 'active',
          'createdAt': '2026-02-12T08:00:00Z',
          'updatedAt': '2026-02-14T16:00:00Z',
        };

        // Act: Resolve conflict (server wins)
        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 8,
        );

        // Assert: Local changes discarded, server version accepted
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-002')
            .findFirst();

        expect(updated, isNotNull);
        expect(updated!.version, equals(8));
        expect(updated.cropType, equals('maize')); // Server wins
        expect(updated.quantity, equals(50.0)); // Not 20.0 (local)
        expect(updated.description, equals('Updated by another device'));
      });

      test('updates all CropListing fields including optional ones', () async {
        // Arrange: Local listing without audio note
        final listing = CropListing()
          ..listingId = 'listing-003'
          ..farmerId = 'farmer-003'
          ..cropType = 'chickpea'
          ..quantity = 5.0
          ..pricePerKg = 35.0
          ..location = (LatLng()
            ..latitude = 8.5
            ..longitude = 38.0)
          ..description = 'No audio'
          ..locationName = 'Hawassa'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-13T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-13T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-003'
          ..idempotencyKey = 'key-003'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-003'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server has audio note added
        final serverData = {
          'listingId': 'listing-003',
          'farmerId': 'farmer-003',
          'cropType': 'chickpea',
          'quantity': 5.0,
          'pricePerKg': 35.0,
          'location': {'latitude': 8.5, 'longitude': 38.0},
          'description': 'With audio note now',
          'audioNoteRef': 'audio/farmer-003/listing-003.m4a',
          'status': 'active',
          'createdAt': '2026-02-13T10:00:00Z',
          'updatedAt': '2026-02-14T09:00:00Z',
        };

        // Act
        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 2,
        );

        // Assert: Audio note field updated
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-003')
            .findFirst();

        expect(
          updated!.audioNoteRef,
          equals('audio/farmer-003/listing-003.m4a'),
        );
        expect(updated.status, equals(ListingStatus.active));
      });

      test('handles status enum parsing correctly', () async {
        // Arrange
        final listing = CropListing()
          ..listingId = 'listing-004'
          ..farmerId = 'farmer-004'
          ..cropType = 'teff'
          ..quantity = 12.0
          ..pricePerKg = 28.0
          ..location = (LatLng()
            ..latitude = 9.5
            ..longitude = 39.5)
          ..description = 'Active listing'
          ..locationName = 'Debre Zeit'
          ..audioNoteRef = null
          ..status = ListingStatus.active
          ..createdAt = DateTime.parse('2026-02-10T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-10T10:00:00Z')
          ..version = 3;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-004'
          ..idempotencyKey = 'key-004'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-004'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server changed status to sold
        final serverData = {
          'listingId': 'listing-004',
          'farmerId': 'farmer-004',
          'cropType': 'teff',
          'quantity': 12.0,
          'pricePerKg': 28.0,
          'location': {'latitude': 9.5, 'longitude': 39.5},
          'description': 'Sold successfully',
          'audioNoteRef': null,
          'status': 'sold',
          'createdAt': '2026-02-10T10:00:00Z',
          'updatedAt': '2026-02-14T18:00:00Z',
        };

        // Act
        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 4,
        );

        // Assert: Status correctly parsed
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-004')
            .findFirst();

        expect(updated!.status, equals(ListingStatus.sold));
        expect(updated.description, equals('Sold successfully'));
      });

      test('creates entity from server data when local entity not found',
          () async {
        // Arrange: Operation for non-existent local entity
        final operation = SyncOperation()
          ..operationId = 'op-999'
          ..idempotencyKey = 'key-999'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-nonexistent'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {
          'listingId': 'listing-nonexistent',
          'farmerId': 'farmer-001',
          'cropType': 'wheat',
          'quantity': 10.0,
          'pricePerKg': 25.0,
          'location': {'latitude': 9.0, 'longitude': 38.0},
          'description': 'Test',
          'audioNoteRef': null,
          'status': 'active',
          'createdAt': '2026-02-14T10:00:00Z',
          'updatedAt': '2026-02-14T10:00:00Z',
        };

        // Act: Server-wins strategy creates the entity locally
        final result = await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 1,
        );

        // Assert: Entity was created from server data
        final created = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-nonexistent')
            .findFirst();

        expect(created, isNotNull);
        expect(created!.cropType, equals('wheat'));
        expect(created.version, equals(1));
        expect(result['entityType'], equals('crop_listing'));
        expect(result['entityId'], equals('listing-nonexistent'));
      });

      test('throws exception when server data is malformed', () async {
        // Arrange: Create local listing
        final listing = CropListing()
          ..listingId = 'listing-005'
          ..farmerId = 'farmer-005'
          ..cropType = 'teff'
          ..quantity = 10.0
          ..pricePerKg = 25.0
          ..location = (LatLng()
            ..latitude = 9.0
            ..longitude = 38.0)
          ..description = 'Test'
          ..locationName = 'Jimma'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-005'
          ..idempotencyKey = 'key-005'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-005'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Malformed server data (missing required fields)
        final serverData = {
          'listingId': 'listing-005',
          // Missing farmerId, cropType, etc.
        };

        // Act & Assert
        expect(
          () => resolver.resolve(
            operation: operation,
            serverData: serverData,
            serverVersion: 2,
          ),
          throwsA(isA<ConflictResolutionException>()),
        );
      });

      test('preserves entity createdAt timestamp', () async {
        // Arrange
        final originalCreatedAt = DateTime.parse('2026-02-01T10:00:00Z');
        final listing = CropListing()
          ..listingId = 'listing-006'
          ..farmerId = 'farmer-006'
          ..cropType = 'teff'
          ..quantity = 10.0
          ..pricePerKg = 25.0
          ..location = (LatLng()
            ..latitude = 9.0
            ..longitude = 38.0)
          ..description = 'Test'
          ..locationName = 'Bahir Dar'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = originalCreatedAt
          ..updatedAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-006'
          ..idempotencyKey = 'key-006'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-006'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {
          'listingId': 'listing-006',
          'farmerId': 'farmer-006',
          'cropType': 'wheat',
          'quantity': 15.0,
          'pricePerKg': 30.0,
          'location': {'latitude': 9.0, 'longitude': 38.0},
          'description': 'Updated',
          'audioNoteRef': null,
          'status': 'active',
          'createdAt': '2026-02-01T10:00:00Z',
          'updatedAt': '2026-02-14T16:00:00Z',
        };

        // Act
        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 2,
        );

        // Assert: createdAt preserved (not changed)
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-006')
            .findFirst();

        expect(updated!.createdAt.toUtc(), equals(originalCreatedAt.toUtc()));
        expect(
          updated.updatedAt.toUtc(),
          equals(DateTime.parse('2026-02-14T16:00:00Z').toUtc()),
        );
      });
    });

    group('Trip conflict resolution', () {
      test('resolves conflict when server version > client version', () async {
        // Arrange: Create local Trip with version 1
        final trip = Trip()
          ..tripId = 'trip-001'
          ..truckerId = 'trucker-001'
          ..truckerName = 'Dawit Assefa'
          ..truckerPhone = '+251912345678'
          ..fromWoreda = 'Bishoftu'
          ..toWoreda = 'Addis Ababa'
          ..departureDate = DateTime.parse('2026-02-20T06:00:00Z')
          ..capacity = 100.0
          ..pricePerQuintal = 250.0
          ..status = TripStatus.open
          ..matchedListings = <String>[]
          ..createdAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.trips.put(trip);
        });

        final operation = SyncOperation()
          ..operationId = 'op-trip-001'
          ..idempotencyKey = 'key-trip-001'
          ..entityType = 'trip'
          ..entityId = 'trip-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server data with version 3 (conflict - server is ahead)
        final serverData = {
          'tripId': 'trip-001',
          'truckerId': 'trucker-001',
          'truckerName': 'Dawit Assefa',
          'truckerPhone': '+251912345678',
          'fromWoreda': 'Debre Zeit',
          'toWoreda': 'Adama',
          'departureDate': '2026-02-21T07:00:00Z',
          'capacity': 150.0,
          'pricePerQuintal': 300.0,
          'status': 'matched',
          'matchedListings': ['listing-001', 'listing-002'],
          'createdAt': '2026-02-15T10:00:00Z',
          'updatedAt': '2026-02-16T14:00:00Z',
        };

        // Act
        final result = await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 3,
        );

        // Assert
        final updated =
            await isar.trips.filter().tripIdEqualTo('trip-001').findFirst();

        expect(updated, isNotNull);
        expect(updated!.version, equals(3));
        expect(updated.fromWoreda, equals('Debre Zeit'));
        expect(updated.toWoreda, equals('Adama'));
        expect(updated.capacity, equals(150.0));
        expect(updated.pricePerQuintal, equals(300.0));
        expect(updated.status, equals(TripStatus.matched));
        expect(updated.matchedListings, equals(['listing-001', 'listing-002']));
        expect(
          updated.departureDate.toUtc(),
          equals(DateTime.parse('2026-02-21T07:00:00Z').toUtc()),
        );
        expect(
          updated.updatedAt.toUtc(),
          equals(DateTime.parse('2026-02-16T14:00:00Z').toUtc()),
        );

        // Audit info
        expect(result['entityType'], equals('trip'));
        expect(result['entityId'], equals('trip-001'));
        expect(result['serverVersion'], equals(3));
        expect(result['resolvedAt'], isNotNull);
      });

      test('creates trip from server data when local trip not found', () async {
        final operation = SyncOperation()
          ..operationId = 'op-trip-missing'
          ..idempotencyKey = 'key-trip-missing'
          ..entityType = 'trip'
          ..entityId = 'trip-nonexistent'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {
          'tripId': 'trip-nonexistent',
          'truckerId': 'trucker-001',
          'truckerName': 'Dawit',
          'truckerPhone': '+251912345678',
          'fromWoreda': 'Bishoftu',
          'toWoreda': 'Addis Ababa',
          'departureDate': '2026-02-20T06:00:00Z',
          'capacity': 100.0,
          'pricePerQuintal': 250.0,
          'status': 'open',
          'matchedListings': <String>[],
          'createdAt': '2026-02-15T10:00:00Z',
          'updatedAt': '2026-02-15T10:00:00Z',
        };

        // Act: Server-wins strategy creates the trip locally
        final result = await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 1,
        );

        // Assert: Trip was created from server data
        final created = await isar.trips
            .filter()
            .tripIdEqualTo('trip-nonexistent')
            .findFirst();

        expect(created, isNotNull);
        expect(created!.fromWoreda, equals('Bishoftu'));
        expect(created.toWoreda, equals('Addis Ababa'));
        expect(created.version, equals(1));
        expect(result['entityType'], equals('trip'));
        expect(result['entityId'], equals('trip-nonexistent'));
      });

      test('throws exception when server data is malformed', () async {
        final trip = Trip()
          ..tripId = 'trip-002'
          ..truckerId = 'trucker-001'
          ..truckerName = 'Dawit'
          ..truckerPhone = '+251912345678'
          ..fromWoreda = 'Bishoftu'
          ..toWoreda = 'Addis Ababa'
          ..departureDate = DateTime.parse('2026-02-20T06:00:00Z')
          ..capacity = 100.0
          ..pricePerQuintal = 250.0
          ..status = TripStatus.open
          ..matchedListings = <String>[]
          ..createdAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.trips.put(trip);
        });

        final operation = SyncOperation()
          ..operationId = 'op-trip-bad'
          ..idempotencyKey = 'key-trip-bad'
          ..entityType = 'trip'
          ..entityId = 'trip-002'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Malformed: missing required fields
        final serverData = {'tripId': 'trip-002'};

        expect(
          () => resolver.resolve(
            operation: operation,
            serverData: serverData,
            serverVersion: 2,
          ),
          throwsA(isA<ConflictResolutionException>()),
        );
      });

      test('handles numeric types correctly (int vs double)', () async {
        final trip = Trip()
          ..tripId = 'trip-003'
          ..truckerId = 'trucker-001'
          ..truckerName = 'Dawit'
          ..truckerPhone = '+251912345678'
          ..fromWoreda = 'Bishoftu'
          ..toWoreda = 'Addis Ababa'
          ..departureDate = DateTime.parse('2026-02-20T06:00:00Z')
          ..capacity = 100.0
          ..pricePerQuintal = 250.0
          ..status = TripStatus.open
          ..matchedListings = <String>[]
          ..createdAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-15T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.trips.put(trip);
        });

        final operation = SyncOperation()
          ..operationId = 'op-trip-num'
          ..idempotencyKey = 'key-trip-num'
          ..entityType = 'trip'
          ..entityId = 'trip-003'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server sends integers instead of doubles
        final serverData = {
          'tripId': 'trip-003',
          'truckerId': 'trucker-001',
          'truckerName': 'Dawit',
          'truckerPhone': '+251912345678',
          'fromWoreda': 'Bishoftu',
          'toWoreda': 'Addis Ababa',
          'departureDate': '2026-02-20T06:00:00Z',
          'capacity': 200, // int
          'pricePerQuintal': 350, // int
          'status': 'open',
          'matchedListings': <String>[],
          'createdAt': '2026-02-15T10:00:00Z',
          'updatedAt': '2026-02-16T10:00:00Z',
        };

        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 2,
        );

        final updated =
            await isar.trips.filter().tripIdEqualTo('trip-003').findFirst();

        expect(updated!.capacity, equals(200.0));
        expect(updated.pricePerQuintal, equals(350.0));
      });
    });

    group('Delivery conflict resolution', () {
      test('keeps a self-pickup delivery\'s type from the server', () async {
        final operation = SyncOperation()
          ..operationId = 'op-del'
          ..idempotencyKey = 'key-del'
          ..entityType = 'delivery'
          ..entityId = 'del-self'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        await resolver.resolve(
          operation: operation,
          serverData: {
            'deliveryId': 'del-self',
            'tripId': 'del-self',
            'orderId': 'order-1',
            'listingId': 'crop-1',
            'farmerId': 'farmer-1',
            'farmerName': 'Kedir',
            'buyerId': 'buyer-1',
            'buyerName': 'Dawit',
            'deliveryType': 'self_pickup',
            'status': 'pending',
            'createdAt': '2026-09-29T10:00:00Z',
            'updatedAt': '2026-09-29T10:00:00Z',
          },
          serverVersion: 2,
        );

        final stored = await isar.deliverys
            .filter()
            .deliveryIdEqualTo('del-self')
            .findFirst();
        expect(stored!.deliveryType, 'self_pickup');
        expect(stored.status, DeliveryStatus.pending);
      });
    });

    group('Unsupported entity types', () {
      test('throws exception for payment entity', () async {
        final operation = SyncOperation()
          ..operationId = 'op-payment'
          ..idempotencyKey = 'key-payment'
          ..entityType = 'payment'
          ..entityId = 'payment-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {'paymentId': 'payment-001'};

        expect(
          () => resolver.resolve(
            operation: operation,
            serverData: serverData,
            serverVersion: 1,
          ),
          throwsA(isA<ConflictResolutionException>()),
        );
      });

      test('throws exception for user entity', () async {
        final operation = SyncOperation()
          ..operationId = 'op-user'
          ..idempotencyKey = 'key-user'
          ..entityType = 'user'
          ..entityId = 'user-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {'userId': 'user-001'};

        expect(
          () => resolver.resolve(
            operation: operation,
            serverData: serverData,
            serverVersion: 1,
          ),
          throwsA(isA<ConflictResolutionException>()),
        );
      });

      test('throws exception for unknown entity type', () async {
        final operation = SyncOperation()
          ..operationId = 'op-unknown'
          ..idempotencyKey = 'key-unknown'
          ..entityType = 'unknown_entity'
          ..entityId = 'entity-001'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        final serverData = {'id': 'entity-001'};

        expect(
          () => resolver.resolve(
            operation: operation,
            serverData: serverData,
            serverVersion: 1,
          ),
          throwsA(isA<ConflictResolutionException>()),
        );
      });
    });

    group('Edge cases', () {
      test('handles numeric types correctly (int vs double)', () async {
        // Arrange: Test that quantity and price handle both int and double from server
        final listing = CropListing()
          ..listingId = 'listing-007'
          ..farmerId = 'farmer-007'
          ..cropType = 'teff'
          ..quantity = 10.0
          ..pricePerKg = 25.0
          ..location = (LatLng()
            ..latitude = 9.0
            ..longitude = 38.0)
          ..description = 'Test'
          ..locationName = 'Gondar'
          ..audioNoteRef = null
          ..status = ListingStatus.draft
          ..createdAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..updatedAt = DateTime.parse('2026-02-14T10:00:00Z')
          ..version = 1;

        await isar.writeTxn(() async {
          await isar.cropListings.put(listing);
        });

        final operation = SyncOperation()
          ..operationId = 'op-007'
          ..idempotencyKey = 'key-007'
          ..entityType = 'crop_listing'
          ..entityId = 'listing-007'
          ..action = 'update'
          ..data = '{}'
          ..status = SyncStatus.syncing
          ..retryCount = 0
          ..createdAt = DateTime.now().toUtc();

        // Server sends integers instead of doubles
        final serverData = {
          'listingId': 'listing-007',
          'farmerId': 'farmer-007',
          'cropType': 'wheat',
          'quantity': 20, // int
          'pricePerKg': 30, // int
          'location': {'latitude': 9.5, 'longitude': 38.5},
          'description': 'Updated',
          'audioNoteRef': null,
          'status': 'active',
          'createdAt': '2026-02-14T10:00:00Z',
          'updatedAt': '2026-02-14T16:00:00Z',
        };

        // Act
        await resolver.resolve(
          operation: operation,
          serverData: serverData,
          serverVersion: 2,
        );

        // Assert: Numeric conversion works
        final updated = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-007')
            .findFirst();

        expect(updated!.quantity, equals(20.0));
        expect(updated.pricePerKg, equals(30.0));
      });
    });
  });
}
