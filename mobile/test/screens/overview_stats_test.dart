import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import '../helpers/crop_photo_fixtures.dart';

/// Tests that verify overview stats (listing count, etc.) stay in sync
/// with the actual data in Isar.
///
/// Regression test: farmer creates listings but overview shows 0.
void main() {
  group('Overview stats - Isar query consistency', () {
    late Isar isar;
    late Directory tempDir;
    late CropListingRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('overview_stats_test_');
      isar = await Isar.open(
        [
          UserSchema,
          CropListingSchema,
          TripSchema,
          DeliverySchema,
          PaymentSchema,
          SyncOperationSchema,
          OrderSchema,
        ],
        directory: tempDir.path,
        name: 'test_stats_${DateTime.now().millisecondsSinceEpoch}',
      );
      repository = CropListingRepository(
        photoFiles: tempCropPhotoFileStore(),
        isar: isar,
        syncQueue: SyncQueue(isar),
      );
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test(
        'farmer listing count matches after creation '
        '(overview stats use same query as My Listings)', () async {
      const farmerId = 'farmer-overview-test';

      // Initially 0
      final countBefore =
          await isar.cropListings.filter().farmerIdEqualTo(farmerId).count();
      expect(countBefore, 0);

      // Create 3 listings
      for (final crop in ['teff', 'wheat', 'barley']) {
        await repository.createListing(
          photos: testCropPhotos(),
          farmerId: farmerId,
          cropType: crop,
          quantity: 50.0,
          pricePerKg: 45.0,
          latitude: 9.0,
          longitude: 38.0,
          locationName: 'Debre Birhan',
          description: 'Test $crop',
        );
      }

      // Overview stats query (filter-based — must match)
      final overviewCount =
          await isar.cropListings.filter().farmerIdEqualTo(farmerId).count();
      expect(overviewCount, 3, reason: 'Overview stats should show 3 listings');

      // My Listings query (also filter-based)
      final myListings = await repository.getListingsByFarmer(farmerId);
      expect(
        myListings.length,
        3,
        reason: 'My Listings should show 3 listings',
      );

      // Both must agree
      expect(
        overviewCount,
        myListings.length,
        reason: 'Overview count must equal My Listings count',
      );
    });

    test(
        'farmer listing count updates after delete '
        '(expired listings still counted by farmer query)', () async {
      const farmerId = 'farmer-delete-test';

      final listing = await repository.createListing(
        photos: testCropPhotos(),
        farmerId: farmerId,
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Ada',
        description: 'Teff',
      );

      await repository.createListing(
        photos: testCropPhotos(),
        farmerId: farmerId,
        cropType: 'wheat',
        quantity: 30.0,
        pricePerKg: 35.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Arsi',
        description: 'Wheat',
      );

      // Delete one
      await repository.deleteListing(
        listingId: listing.listingId,
        farmerId: farmerId,
      );

      // Overview counts all (including expired) via filter query
      final overviewCount =
          await isar.cropListings.filter().farmerIdEqualTo(farmerId).count();
      expect(overviewCount, 2, reason: 'Overview counts all farmer listings');

      // My Listings also returns all
      final myListings = await repository.getListingsByFarmer(farmerId);
      expect(myListings.length, 2);

      expect(overviewCount, myListings.length);
    });

    test('buyer sees active listings from all farmers', () async {
      // Create listings from two farmers
      await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-a',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Ada',
        description: 'Teff',
      );
      await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-b',
        cropType: 'wheat',
        quantity: 30.0,
        pricePerKg: 35.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Arsi',
        description: 'Wheat',
      );

      // Buyer overview stats query: count active listings
      final buyerListingCount = await isar.cropListings
          .filter()
          .statusEqualTo(ListingStatus.active)
          .count();
      expect(
        buyerListingCount,
        2,
        reason: 'Buyer should see all active listings',
      );

      // Buyer browse query (via repository)
      final browseListings = await repository.getActiveListings();
      expect(
        browseListings.length,
        2,
        reason: 'Browse should show all active listings',
      );

      expect(
        buyerListingCount,
        browseListings.length,
        reason: 'Buyer overview count must equal browse count',
      );
    });

    test('different farmers listings are isolated in overview', () async {
      await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-x',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Ada',
        description: 'Teff from X',
      );
      await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-y',
        cropType: 'wheat',
        quantity: 30.0,
        pricePerKg: 35.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Arsi',
        description: 'Wheat from Y',
      );
      await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-x',
        cropType: 'barley',
        quantity: 20.0,
        pricePerKg: 25.0,
        latitude: 9.0,
        longitude: 38.0,
        locationName: 'Bale',
        description: 'Barley from X',
      );

      // Farmer X overview
      final xCount =
          await isar.cropListings.filter().farmerIdEqualTo('farmer-x').count();
      expect(xCount, 2, reason: 'Farmer X should see 2 listings');

      // Farmer Y overview
      final yCount =
          await isar.cropListings.filter().farmerIdEqualTo('farmer-y').count();
      expect(yCount, 1, reason: 'Farmer Y should see 1 listing');

      // Verify via repository
      final xListings = await repository.getListingsByFarmer('farmer-x');
      final yListings = await repository.getListingsByFarmer('farmer-y');
      expect(xCount, xListings.length);
      expect(yCount, yListings.length);
    });

    test('overview order count matches Isar for farmer', () async {
      const farmerId = 'farmer-order-test';

      // Create some orders with different statuses
      await isar.writeTxn(() async {
        final now = DateTime.now().toUtc();
        var i = 0;
        for (final status in [
          OrderStatus.funded,
          OrderStatus.funded,
          OrderStatus.delivered,
          OrderStatus.created,
        ]) {
          i++;
          final order = Order()
            ..orderId = 'order-${now.millisecondsSinceEpoch}-$i'
            ..farmerId = farmerId
            ..buyerId = 'buyer-001'
            ..buyerName = 'Test Buyer'
            ..farmerName = 'Test Farmer'
            ..listingId = 'listing-001'
            ..cropType = 'teff'
            ..quantity = 10.0
            ..pricePerUnit = 45.0
            ..totalPrice = 450.0
            ..status = status
            ..createdAt = now
            ..version = 1;
          await isar.orders.put(order);
        }
      });

      // Overview stats query: funded orders only
      final pendingOrderCount = await isar.orders
          .filter()
          .farmerIdEqualTo(farmerId)
          .statusEqualTo(OrderStatus.funded)
          .count();
      expect(
        pendingOrderCount,
        2,
        reason: 'Farmer should see 2 funded (pending) orders',
      );
    });
  });
}
