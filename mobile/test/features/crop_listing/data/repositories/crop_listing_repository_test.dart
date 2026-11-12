import 'dart:convert';
import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/crop_listing/data/repositories/crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/domain/repositories/base_crop_listing_repository.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import '../../../../helpers/crop_photo_fixtures.dart';

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
        photoFiles: tempCropPhotoFileStore(),
        isar: isar,
        syncQueue: syncQueue,
      );
    });

    /// Every queued operation reached the server.
    Future<void> sentAll() async {
      for (final op in await syncQueue.getPendingOperations()) {
        await syncQueue.markAsSynced(op.operationId);
      }
    }

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('createListing saves to Isar with correct fields', () async {
      final listing = await repository.createListing(
        photos: testCropPhotos(),
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

    test('getListingById returns the listing or null when absent', () async {
      final listing = await repository.createListing(
        photos: testCropPhotos(),
        farmerId: 'farmer-001',
        cropType: 'teff',
        quantity: 50.0,
        pricePerKg: 45.0,
        latitude: 9.0192,
        longitude: 38.7525,
        locationName: 'Ada',
        description: 'High quality teff',
      );

      final found = await repository.getListingById(listing.listingId);
      expect(found, isNotNull);
      expect(found!.cropType, 'teff');

      expect(await repository.getListingById('missing-id'), isNull);
    });

    test('createListing defaults farmerName to empty string when omitted',
        () async {
      final listing = await repository.createListing(
        photos: testCropPhotos(),
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
        photos: testCropPhotos(),
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
        photos: testCropPhotos(),
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
        photos: testCropPhotos(),
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
        photos: testCropPhotos(),
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
        photos: testCropPhotos(),
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
        photos: testCropPhotos(),
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

    test(
        'getActiveListings searchQuery matches crop, farmer and place (SIM-51)',
        () async {
      Future<String> seed({
        required String cropType,
        required String farmerName,
        required String place,
        required double price,
      }) async {
        final listing = await repository.createListing(
          photos: testCropPhotos(),
          farmerId: 'farmer-${farmerName.hashCode}',
          farmerName: farmerName,
          cropType: cropType,
          quantity: 10.0,
          pricePerKg: price,
          latitude: 9.0,
          longitude: 38.0,
          locationName: place,
          description: '',
        );
        return listing.listingId;
      }

      await seed(
        cropType: 'teff',
        farmerName: 'Abebe Kebede',
        place: 'Bishoftu',
        price: 45,
      );
      await seed(
        cropType: 'wheat',
        farmerName: 'Almaz Tesfaye',
        place: 'Adama',
        price: 35,
      );
      await seed(cropType: 'maize', farmerName: '', place: 'Arsi', price: 30);
      // An expired teff listing must never show up in the marketplace.
      final expiredId = await seed(
        cropType: 'teff',
        farmerName: 'Old Seller',
        place: 'Bishoftu',
        price: 40,
      );
      await repository.deleteListing(
        listingId: expiredId,
        farmerId: 'farmer-${'Old Seller'.hashCode}',
      );

      final am = await AppLocalizations.delegate.load(const Locale('am'));
      final om = await AppLocalizations.delegate.load(const Locale('om'));

      Future<List<String>> crops(String? query) async =>
          (await repository.getActiveListings(searchQuery: query))
              .map((l) => l.cropType)
              .toList();

      // Crop: stored key, English, Amharic and Afan Oromo names.
      expect(await crops('teff'), ['teff']);
      expect(await crops('Teff'), ['teff']);
      expect(await crops(am.cropTypeTeff), ['teff']);
      expect(await crops(om.cropTypeTeff), ['teff']);
      // Farmer name, any part, any case.
      expect(await crops('abebe'), ['teff']);
      expect(await crops('KEBEDE'), ['teff']);
      // Place.
      expect(await crops('adama'), ['wheat']);
      // Every word must match some field.
      expect(await crops('teff bishoftu'), ['teff']);
      expect(await crops('teff adama'), isEmpty);
      // Blank or no search returns every active listing.
      expect((await crops('   ')).length, 3);
      expect((await crops(null)).length, 3);

      // Search combines with the crop chip and the price range.
      expect(
        await repository.getActiveListings(
          cropTypeFilter: 'wheat',
          searchQuery: 'abebe',
        ),
        isEmpty,
      );
      expect(
        await repository.getActiveListings(searchQuery: 'adama', priceMax: 30),
        isEmpty,
      );
      expect(
        (await repository.getActiveListings(searchQuery: 'adama', priceMax: 40))
            .length,
        1,
      );
    });

    test('deleteListing marks as expired and enqueues sync', () async {
      final listing = await repository.createListing(
        photos: testCropPhotos(),
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
      expect(fromDb.version, 2);

      // Should have 2 sync ops: create + delete
      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 2);
      expect(pending.last.action, 'delete');

      // The delete op must carry the version the server still holds (1, the
      // pre-increment value) — NOT the locally-bumped 2. Sending 2 fails the
      // server's optimistic-concurrency check and the delete silently reverts.
      final deleteData = jsonDecode(pending.last.data) as Map<String, dynamic>;
      expect(deleteData['version'], 1);
      expect(deleteData['status'], ListingStatus.expired.name);
    });

    group('updateListing (SIM-56)', () {
      /// A listing whose create already reached the server: nothing of it
      /// waits in the queue.
      Future<CropListing> seed() async {
        final listing = await repository.createListing(
          photos: testCropPhotos(),
          farmerId: 'farmer-001',
          cropType: 'teff',
          quantity: 50.0,
          pricePerKg: 45.0,
          latitude: 9.0,
          longitude: 38.0,
          locationName: 'Ada',
          description: 'Teff',
        );
        await sentAll();
        return listing;
      }

      Future<CropListing> stored(String listingId) async =>
          (await isar.cropListings
              .filter()
              .listingIdEqualTo(listingId)
              .findFirst())!;

      Future<List<Map<String, dynamic>>> waitingUpdates() async => [
            for (final op in await syncQueue.getPendingOperations())
              if (op.action == 'update')
                jsonDecode(op.data) as Map<String, dynamic>,
          ];

      test('saves on the phone and queues the changed fields at the form base',
          () async {
        final listing = await seed();

        final saved = await repository.updateListing(
          listing: listing,
          farmerId: 'farmer-001',
          quantity: 42.5,
          pricePerKg: 47.25,
          description: 'White teff',
        );

        expect(saved.quantity, 42.5);
        final fromDb = await stored(listing.listingId);
        expect(fromDb.quantity, 42.5);
        expect(fromDb.pricePerKg, 47.25);
        expect(fromDb.description, 'White teff');
        expect(fromDb.version, 2);
        // Untouched: the crop, the place and the status.
        expect(fromDb.cropType, 'teff');
        expect(fromDb.locationName, 'Ada');
        expect(fromDb.status, ListingStatus.active);

        expect(await waitingUpdates(), [
          {
            'listingId': listing.listingId,
            'quantity': 42.5,
            'pricePerKg': 47.25,
            'description': 'White teff',
            'version': 1,
          },
        ]);
      });

      test('a price-only edit sends the price only, never the stock', () async {
        final listing = await seed();

        await repository.updateListing(
          listing: listing,
          farmerId: 'farmer-001',
          pricePerKg: 52,
        );

        expect(await waitingUpdates(), [
          {'listingId': listing.listingId, 'pricePerKg': 52, 'version': 1},
        ]);
        final fromDb = await stored(listing.listingId);
        expect(fromDb.quantity, 50.0);
        expect(fromDb.description, 'Teff');
        expect(fromDb.pricePerKg, 52);
      });

      test('once the first edit went out, the next goes at the next version',
          () async {
        final listing = await seed();
        await repository.updateListing(
          listing: listing,
          farmerId: 'farmer-001',
          quantity: 40,
        );
        await sentAll();

        await repository.updateListing(
          listing: await stored(listing.listingId),
          farmerId: 'farmer-001',
          quantity: 30,
        );

        expect(
          (await waitingUpdates()).map((data) => data['version']),
          [2],
        );
      });

      test(
          'scenario B: a second edit is refused while the first still waits, '
          'so it cannot land after an order took stock', () async {
        // Two offline edits would queue at versions 1 and 2. An order moving
        // the server to 2 would make the first conflict and let the second,
        // at 2, write its stock over the order's.
        final listing = await seed();
        final first = await repository.updateListing(
          listing: listing,
          farmerId: 'farmer-001',
          quantity: 12,
        );

        await expectLater(
          repository.updateListing(
            listing: first,
            farmerId: 'farmer-001',
            quantity: 10,
          ),
          throwsA(isA<ListingChangeWaitingException>()),
        );

        expect(await waitingUpdates(), hasLength(1));
        expect((await stored(listing.listingId)).quantity, 12);
      });

      test(
          'a change failed out of retries still waits (a reconnect resends it)',
          () async {
        final listing = await seed();
        final first = await repository.updateListing(
          listing: listing,
          farmerId: 'farmer-001',
          quantity: 12,
        );
        final op = (await syncQueue.getPendingOperations()).single;
        for (var i = 0; i < SyncQueue.maxRetries; i++) {
          await syncQueue.markAsFailed(op.operationId);
        }

        expect(await repository.hasChangeWaiting(listing.listingId), isTrue);
        await expectLater(
          repository.updateListing(
            listing: first,
            farmerId: 'farmer-001',
            quantity: 10,
          ),
          throwsA(isA<ListingChangeWaitingException>()),
        );
      });

      test('a change the server refused for good no longer blocks an edit',
          () async {
        final listing = await seed();
        await repository.updateListing(
          listing: listing,
          farmerId: 'farmer-001',
          description: 'Call 0911223344',
        );
        final op = (await syncQueue.getPendingOperations()).single;
        await syncQueue.markAsRejected(op.operationId);

        expect(await repository.hasChangeWaiting(listing.listingId), isFalse);
        await repository.updateListing(
          listing: await stored(listing.listingId),
          farmerId: 'farmer-001',
          description: 'White teff',
        );
        expect(await waitingUpdates(), hasLength(1));
      });

      test(
          'scenario A: a copy that kept its version number but not its stock '
          'is not edited', () async {
        // The server held 10 at version 1. The farmer edits offline to 12
        // (version 2 on the phone) and the detail keeps that copy. An order
        // takes 5 (the server goes to version 2, stock 5). On reconnect the
        // edit conflicts and the server's copy (5 at version 2) replaces the
        // phone's. An edit from the kept copy (12 at version 2) must not go
        // out at version 2 and put the stock back.
        final listing = await seed();
        final kept = await repository.updateListing(
          listing: listing,
          farmerId: 'farmer-001',
          quantity: 12,
        );
        await sentAll();
        await isar.writeTxn(() async {
          final row = await stored(listing.listingId);
          row
            ..quantity = 5
            ..version = 2
            ..updatedAt = DateTime.utc(2026, 11, 12, 10);
          await isar.cropListings.put(row);
        });
        expect(kept.version, 2);

        await expectLater(
          repository.updateListing(
            listing: kept,
            farmerId: 'farmer-001',
            quantity: 10,
          ),
          throwsA(isA<SyncConflictException>()),
        );

        expect((await stored(listing.listingId)).quantity, 5);
        expect(await waitingUpdates(), isEmpty);
      });

      test('changed since the form opened is not overwritten', () async {
        // The form read version 1; the phone's copy moved on to 2 since (a
        // pull brought the server's copy after an order took stock).
        final listing = await seed();
        final formCopy = await stored(listing.listingId);
        await isar.writeTxn(() async {
          final row = await stored(listing.listingId);
          row
            ..quantity = 6
            ..version = 2;
          await isar.cropListings.put(row);
        });

        await expectLater(
          repository.updateListing(
            listing: formCopy,
            farmerId: 'farmer-001',
            pricePerKg: 60,
          ),
          throwsA(isA<SyncConflictException>()),
        );

        final fromDb = await stored(listing.listingId);
        expect(fromDb.quantity, 6);
        expect(fromDb.pricePerKg, 45.0);
        expect(fromDb.version, 2);
        expect(await waitingUpdates(), isEmpty);
      });

      test("another farmer's listing is refused and nothing changes", () async {
        final listing = await seed();

        await expectLater(
          repository.updateListing(
            listing: listing,
            farmerId: 'farmer-002',
            quantity: 1,
          ),
          throwsStateError,
        );

        final fromDb = await stored(listing.listingId);
        expect(fromDb.quantity, 50.0);
        expect(fromDb.version, 1);
        expect(await waitingUpdates(), isEmpty);
      });

      test('an edit that changes nothing is refused', () async {
        final listing = await seed();

        await expectLater(
          repository.updateListing(listing: listing, farmerId: 'farmer-001'),
          throwsArgumentError,
        );
        expect(await waitingUpdates(), isEmpty);
      });
    });
  });
}
