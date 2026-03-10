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
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';

void main() {
  group('CropListingRepository', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;
    late CropListingRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('crop_repo_test_');
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
        name: 'test_crop_repo_${DateTime.now().millisecondsSinceEpoch}',
      );
      syncQueue = SyncQueue(isar);
      repository = CropListingRepository(
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

    test('createListing saves to Isar with correct fields', () async {
      final listing = await repository.createListing(
        farmerId: 'farmer-001',
        farmerName: 'Abebe Kebede',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0192,
        longitude: 38.7525,
        locationName: 'Ada',
        description: 'High quality teff',
      );

      expect(listing.farmerId, 'farmer-001');
      expect(listing.farmerName, 'Abebe Kebede');
      expect(listing.cropType, 'teff');
      expect(listing.quantity, 50.0);
      expect(listing.pricePerKg, 45.0);
      expect(listing.location.latitude, 9.0192);
      expect(listing.location.longitude, 38.7525);
      expect(listing.description, 'High quality teff');
      expect(listing.status, ListingStatus.active);
      expect(listing.version, 1);
      expect(listing.listingId, isNotEmpty);

      // Verify persisted in Isar
      final fromDb = await isar.cropListings
          .filter()
          .listingIdEqualTo(listing.listingId)
          .findFirst();
      expect(fromDb, isNotNull);
      expect(fromDb!.cropType, 'teff');
      expect(fromDb.farmerName, 'Abebe Kebede');
    });

    test('createListing defaults farmerName to empty string when omitted',
        () async {
      final listing = await repository.createListing(
        farmerId: 'farmer-002',
        cropType: 'wheat',
        quantity: 30.0,
        pricePerKg: 35.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Arsi',
        description: 'Good wheat',
      );

      expect(listing.farmerName, '');
    });

    test('createListing enqueues sync operation', () async {
      await repository.createListing(
        farmerId: 'farmer-001',
        cropType: 'wheat',
        quantity: 30.0,
        pricePerKg: 35.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Arsi',
        description: 'Good wheat',
      );

      final pendingCount = await syncQueue.getPendingCount();
      expect(pendingCount, 1);

      final pending = await syncQueue.getPendingOperations();
      expect(pending.first.entityType, 'crop_listing');
      expect(pending.first.action, 'create');
    });

    test('getListingsByFarmer returns only that farmer listings', () async {
      await repository.createListing(
        farmerId: 'farmer-001',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Ada',
        description: 'Teff',
      );
      await repository.createListing(
        farmerId: 'farmer-002',
        cropType: 'wheat',
        quantity: 30.0,
        pricePerKg: 35.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Arsi',
        description: 'Wheat',
      );
      await repository.createListing(
        farmerId: 'farmer-001',
        cropType: 'barley',
        quantity: 20.0,
        pricePerKg: 25.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Bale',
        description: 'Barley',
      );

      final listings = await repository.getListingsByFarmer('farmer-001');
      expect(listings.length, 2);
      expect(
        listings.every((l) => l.farmerId == 'farmer-001'),
        isTrue,
      );
    });

    test('getActiveListings with filter returns filtered results', () async {
      await repository.createListing(
        farmerId: 'farmer-001',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Ada',
        description: 'Teff',
      );
      await repository.createListing(
        farmerId: 'farmer-001',
        cropType: 'wheat',
        quantity: 30.0,
        pricePerKg: 35.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Arsi',
        description: 'Wheat',
      );

      final allActive = await repository.getActiveListings();
      expect(allActive.length, 2);

      final teffOnly =
          await repository.getActiveListings(cropTypeFilter: 'teff');
      expect(teffOnly.length, 1);
      expect(teffOnly.first.cropType, 'teff');
    });

    test('deleteListing marks as expired and enqueues sync', () async {
      final listing = await repository.createListing(
        farmerId: 'farmer-001',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Ada',
        description: 'Teff',
      );

      await repository.deleteListing(
        listingId: listing.listingId,
        farmerId: 'farmer-001',
      );

      final fromDb = await isar.cropListings
          .filter()
          .listingIdEqualTo(listing.listingId)
          .findFirst();
      expect(fromDb!.status, ListingStatus.expired);

      // Should have 2 sync ops: create + delete
      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 2);
      expect(pending.last.action, 'delete');
    });
  });
}
