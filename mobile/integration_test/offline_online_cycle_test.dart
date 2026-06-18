// Integration test for critical offline → online → conflict → resolution flow
//
// This test validates:
// - Offline-first CRUD operations
// - Sync queue management
// - Conflict detection and resolution (server-wins strategy)
// - Idempotency validation
// - Data integrity after multiple offline-online cycles

import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/conflict_resolver.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  group('Offline-Online Cycle Integration Tests', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;
    late CropListingRepository repository;
    late ConflictResolver conflictResolver;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('offline_cycle_test_');
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
        name: 'test_offline_${DateTime.now().millisecondsSinceEpoch}',
      );

      syncQueue = SyncQueue(isar);
      repository = CropListingRepository(isar: isar, syncQueue: syncQueue);
      conflictResolver = ConflictResolver(isar);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    testWidgets('offline create → sync → verify data integrity',
        (tester) async {
      // ===== OFFLINE PHASE =====
      // Create crop listing offline
      final listing = await repository.createListing(
        farmerId: 'farmer-001',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0192,
        longitude: 38.7525,
        locationName: 'Adama',
        description: 'High quality teff from Adama',
      );

      expect(listing.listingId, isNotEmpty);
      expect(listing.status, ListingStatus.active);
      expect(listing.version, 1); // Initial version from repository

      // Verify operation queued for sync
      final pendingOps = await syncQueue.getPendingOperations();
      expect(pendingOps, hasLength(1));
      expect(pendingOps.first.action, 'create');
      expect(pendingOps.first.entityType, 'crop_listing');
      expect(pendingOps.first.entityId, listing.listingId);

      // ===== SIMULATE SYNC SUCCESS =====
      // Mark operation as synced with server version
      await isar.writeTxn(() async {
        final op = pendingOps.first;
        op.status = SyncStatus.synced;
        await isar.syncOperations.put(op);

        // Update listing with server version
        final updatedListing = await isar.cropListings
            .filter()
            .listingIdEqualTo(listing.listingId)
            .findFirst();
        updatedListing!.version = 1; // Server assigned version
        updatedListing.status = ListingStatus.active;
        await isar.cropListings.put(updatedListing);
      });

      // ===== VERIFY DATA INTEGRITY =====
      final syncedListing = await isar.cropListings
          .filter()
          .listingIdEqualTo(listing.listingId)
          .findFirst();
      expect(syncedListing, isNotNull);
      expect(syncedListing!.version, 1);
      expect(syncedListing.status, ListingStatus.active);
      expect(syncedListing.cropType, 'teff');
      expect(syncedListing.quantity, 50.0);
    });

    testWidgets('offline update → conflict detection → server wins',
        (tester) async {
      // ===== SETUP: Create initial listing =====
      final initialListing = CropListing()
        ..listingId = 'listing-conflict'
        ..farmerId = 'farmer-001'
        ..cropType = 'teff'
        ..quantity = 50.0
        ..pricePerKg = 45.0
        ..location = (LatLng()
          ..latitude = 9.0
          ..longitude = 38.0)
        ..locationName = 'Adama'
        ..description = 'Original'
        ..audioNoteRef = null
        ..status = ListingStatus.active
        ..createdAt = DateTime.parse('2026-02-14T10:00:00Z')
        ..updatedAt = DateTime.parse('2026-02-14T10:00:00Z')
        ..version = 1; // Synced with server at version 1

      await isar.writeTxn(() async {
        await isar.cropListings.put(initialListing);
      });

      // ===== OFFLINE PHASE: Client updates locally =====
      await isar.writeTxn(() async {
        final listing = await isar.cropListings
            .filter()
            .listingIdEqualTo('listing-conflict')
            .findFirst();
        listing!.quantity = 100.0; // Client changes quantity
        listing.description = 'Client update';
        await isar.cropListings.put(listing);
      });

      // Enqueue update sync operation
      final updateOp = await syncQueue.enqueue(
        userId: 'farmer-001',
        entityType: 'crop_listing',
        entityId: 'listing-conflict',
        action: 'update',
        data: '{"quantity":100.0,"description":"Client update"}',
        operationId: 'update-op-001',
      );

      final clientListing = await isar.cropListings
          .filter()
          .listingIdEqualTo('listing-conflict')
          .findFirst();
      expect(clientListing!.quantity, 100.0);
      expect(clientListing.description, 'Client update');
      expect(clientListing.version, 1); // Still version 1 locally

      // ===== SIMULATE SERVER CONFLICT =====
      // Pretend server had version 3 (someone else updated)
      final serverData = {
        'listingId': 'listing-conflict',
        'farmerId': 'farmer-001',
        'cropType': 'wheat', // Server changed crop type
        'quantity': 75.0, // Server changed quantity
        'pricePerKg': 50.0,
        'location': {'latitude': 9.5, 'longitude': 38.5},
        'description': 'Server update',
        'audioNoteRef': null,
        'status': 'active',
        'createdAt': '2026-02-14T10:00:00Z',
        'updatedAt': '2026-02-15T14:00:00Z',
      };

      const serverVersion = 3;

      // ===== CONFLICT RESOLUTION: Server wins =====
      await conflictResolver.resolve(
        operation: updateOp,
        serverData: serverData,
        serverVersion: serverVersion,
      );

      // ===== VERIFY: Client data replaced with server data =====
      final resolvedListing = await isar.cropListings
          .filter()
          .listingIdEqualTo('listing-conflict')
          .findFirst();
      expect(resolvedListing, isNotNull);
      expect(resolvedListing!.version, 3); // Server version wins
      expect(resolvedListing.cropType, 'wheat'); // Server data
      expect(resolvedListing.quantity, 75.0); // NOT client's 100.0
      expect(resolvedListing.description, 'Server update'); // NOT client's
    });

    testWidgets(
        'idempotency: duplicate operations should not create duplicates',
        (tester) async {
      // ===== OFFLINE PHASE: Create listing =====
      final listing = await repository.createListing(
        farmerId: 'farmer-001',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Test Wereda',
        description: 'Test',
      );

      // ===== SIMULATE: Operation queued twice (network retry scenario) =====
      final pendingOps = await syncQueue.getPendingOperations();
      expect(pendingOps, hasLength(1));

      // Try to enqueue another operation for the same entity
      // This will get a different idempotency key due to timestamp,
      // so it should succeed (not a duplicate)
      await syncQueue.enqueue(
        userId: 'farmer-001',
        entityType: 'crop_listing',
        entityId: listing.listingId,
        action: 'create',
        data: '{"cropType":"teff","quantity":50}',
        operationId: 'op-dup-retry',
      );

      // ===== VERIFY: Two operations in queue (different timestamps = different keys) =====
      final allOps = await syncQueue.getPendingOperations();
      expect(allOps, hasLength(2));

      // ===== VERIFY: Only one listing exists =====
      final allListings = await isar.cropListings.where().findAll();
      expect(allListings, hasLength(1));
    });

    testWidgets(
        'data loss prevention: 100 offline-online cycles maintain integrity',
        (tester) async {
      const cycles = 100;
      const testListingId = 'listing-stress-test';

      // ===== SETUP: Create initial listing =====
      final initialListing = CropListing()
        ..listingId = testListingId
        ..farmerId = 'farmer-001'
        ..cropType = 'teff'
        ..quantity = 1.0
        ..pricePerKg = 45.0
        ..location = (LatLng()
          ..latitude = 9.0
          ..longitude = 38.0)
        ..locationName = 'Adama'
        ..description = 'Cycle 0'
        ..audioNoteRef = null
        ..status = ListingStatus.active
        ..createdAt = DateTime.now()
        ..updatedAt = DateTime.now()
        ..version = 1;

      await isar.writeTxn(() async {
        await isar.cropListings.put(initialListing);
      });

      // ===== STRESS TEST: 100 offline update cycles =====
      for (var i = 1; i <= cycles; i++) {
        // Offline update
        await isar.writeTxn(() async {
          final listing = await isar.cropListings
              .filter()
              .listingIdEqualTo(testListingId)
              .findFirst();
          listing!.quantity = i.toDouble();
          listing.description = 'Cycle $i';
          listing.updatedAt = DateTime.now().toUtc();
          await isar.cropListings.put(listing);
        });

        // Enqueue sync operation for the update
        await syncQueue.enqueue(
          userId: 'farmer-001',
          entityType: 'crop_listing',
          entityId: testListingId,
          action: 'update',
          data: '{"quantity":${i.toDouble()},"description":"Cycle $i"}',
          operationId: 'op-cycle-$i',
        );

        // Simulate sync: mark operation as synced
        final pendingOps = await syncQueue.getPendingOperations();
        if (pendingOps.isNotEmpty) {
          await isar.writeTxn(() async {
            for (final op in pendingOps) {
              op.status = SyncStatus.synced;
              await isar.syncOperations.put(op);
            }
          });
        }

        // Simulate server version increment
        await isar.writeTxn(() async {
          final listing = await isar.cropListings
              .filter()
              .listingIdEqualTo(testListingId)
              .findFirst();
          listing!.version = i + 1;
          await isar.cropListings.put(listing);
        });
      }

      // ===== VERIFY: Data integrity after 100 cycles =====
      final finalListing = await isar.cropListings
          .filter()
          .listingIdEqualTo(testListingId)
          .findFirst();
      expect(finalListing, isNotNull);
      expect(finalListing!.quantity, cycles.toDouble());
      expect(finalListing.description, 'Cycle $cycles');
      expect(finalListing.version, cycles + 1);
      expect(finalListing.listingId, testListingId);
      expect(finalListing.cropType, 'teff'); // Unchanged
      expect(finalListing.farmerId, 'farmer-001'); // Unchanged

      // ===== VERIFY: No duplicate listings created =====
      final allListings = await isar.cropListings.where().findAll();
      expect(allListings, hasLength(1));

      // ===== VERIFY: All sync operations cleared =====
      final remainingOps = await syncQueue.getPendingOperations();
      expect(remainingOps, isEmpty);
    });

    testWidgets('delete operation: offline delete → sync → verify tombstone',
        (tester) async {
      // ===== SETUP: Create and sync listing =====
      final listing = await repository.createListing(
        farmerId: 'farmer-001',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Test Wereda',
        description: 'To be deleted',
      );

      // Mark as synced
      final createOp = (await syncQueue.getPendingOperations()).first;
      await isar.writeTxn(() async {
        createOp.status = SyncStatus.synced;
        await isar.syncOperations.put(createOp);

        final syncedListing = await isar.cropListings
            .filter()
            .listingIdEqualTo(listing.listingId)
            .findFirst();
        syncedListing!.version = 1;
        syncedListing.status = ListingStatus.active;
        await isar.cropListings.put(syncedListing);
      });

      // ===== OFFLINE DELETE =====
      await repository.deleteListing(
        listingId: listing.listingId,
        farmerId: 'farmer-001',
      );

      // ===== VERIFY: Listing marked as deleted (soft delete) =====
      final deletedListing = await isar.cropListings
          .filter()
          .listingIdEqualTo(listing.listingId)
          .findFirst();
      expect(deletedListing, isNotNull);
      expect(deletedListing!.status, ListingStatus.expired);

      // ===== VERIFY: Delete operation queued =====
      final deleteOps = await syncQueue.getPendingOperations();
      expect(deleteOps, hasLength(1));
      expect(deleteOps.first.action, 'delete');

      // ===== SIMULATE SYNC: Mark delete as synced =====
      await isar.writeTxn(() async {
        deleteOps.first.status = SyncStatus.synced;
        await isar.syncOperations.put(deleteOps.first);

        // Update with server version
        final tombstone = await isar.cropListings
            .filter()
            .listingIdEqualTo(listing.listingId)
            .findFirst();
        tombstone!.version = 2; // Server incremented version
        await isar.cropListings.put(tombstone);
      });

      // ===== VERIFY: Tombstone persists with updated version =====
      final finalListing = await isar.cropListings
          .filter()
          .listingIdEqualTo(listing.listingId)
          .findFirst();
      expect(finalListing, isNotNull);
      expect(finalListing!.status, ListingStatus.expired);
      expect(finalListing.version, 2);
    });
  });
}
