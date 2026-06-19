import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/isar_marketplace_sync.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// One crop as returned by `GET /crops` — note the server key is `quantityKg`
/// but the value is the quintal figure the farmer entered (no conversion).
Map<String, dynamic> _cropJson({
  String cropId = 'crop-1',
  String farmerId = 'farmer-1',
  String cropType = 'wheat',
  num quantityKg = 10,
  num pricePerKg = 100,
  String woreda = 'Assosa',
}) =>
    {
      'cropId': cropId,
      'farmerId': farmerId,
      'farmerName': 'Mitiku',
      'cropType': cropType,
      'woreda': woreda,
      'quantityKg': quantityKg,
      'pricePerKg': pricePerKg,
      'status': 'available',
      'createdAt': '2026-06-18T09:42:06.000Z',
      'updatedAt': '2026-06-18T09:42:06.000Z',
      'version': 1,
      'description': 'Test',
    };

Map<String, dynamic> _tripJson({
  String tripId = 'trip-1',
  String truckerId = 'trucker-1',
  String fromWoreda = 'Assosa',
  String toWoreda = 'AddisAbeba',
  num capacity = 100,
}) =>
    {
      'tripId': tripId,
      'truckerId': truckerId,
      'truckerName': 'Geleta',
      'truckerPhone': '+251931831670',
      'vehiclePlate': '3-AAA-12345',
      'fromWoreda': fromWoreda,
      'toWoreda': toWoreda,
      'departureDate': '2026-06-20T21:00:00.000Z',
      'capacity': capacity,
      'pricePerQuintal': 10,
      'status': 'open',
      'matchedListings': <String>[],
      'createdAt': '2026-06-18T11:33:54.000Z',
      'updatedAt': '2026-06-18T11:33:54.000Z',
      'version': 1,
    };

void main() {
  group('IsarMarketplaceSync', () {
    late Isar isar;
    late Directory tempDir;
    late _MockApiClient api;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('marketplace_sync_test_');
      isar = await Isar.open(
        [CropListingSchema, TripSchema, SyncOperationSchema],
        directory: tempDir.path,
        name: 'test_marketplace_${DateTime.now().microsecondsSinceEpoch}',
      );
      api = _MockApiClient();
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    /// Routes `GET /crops` and `GET /trips` to the supplied page payloads.
    void stubApi({
      List<Map<String, dynamic>> crops = const [],
      List<Map<String, dynamic>> trips = const [],
    }) {
      when(
        () => api.get<Map<String, dynamic>>(
          any(),
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((inv) async {
        final path = inv.positionalArguments.first as String;
        if (path.contains('/crops')) return {'crops': crops};
        if (path.contains('/trips')) return {'trips': trips};
        return <String, dynamic>{};
      });
    }

    IsarMarketplaceSync buildSync({Future<String?> Function()? currentUserId}) =>
        IsarMarketplaceSync(
          api: api,
          isar: isar,
          getCurrentUserId: currentUserId,
        );

    test('pullCrops stores crops with quantity (quintals) taken verbatim from '
        'quantityKg and status mapped available -> active', () async {
      stubApi(crops: [_cropJson()]); // defaults: quantityKg 10, pricePerKg 100

      await buildSync().pullCrops();

      final stored = await isar.cropListings.where().findAll();
      expect(stored, hasLength(1));
      final crop = stored.single;
      expect(crop.listingId, 'crop-1'); // from cropId
      expect(crop.cropType, 'wheat');
      expect(crop.quantity, 10); // NOT divided by 100
      expect(crop.pricePerKg, 100);
      expect(crop.locationName, 'Assosa'); // from woreda
      expect(crop.status, ListingStatus.active);
    });

    test('pullTrips stores open trips parsed from the API', () async {
      stubApi(trips: [_tripJson()]);

      await buildSync().pullTrips();

      final stored = await isar.trips.where().findAll();
      expect(stored, hasLength(1));
      expect(stored.single.tripId, 'trip-1');
      expect(stored.single.fromWoreda, 'Assosa');
      expect(stored.single.toWoreda, 'AddisAbeba');
      expect(stored.single.status, TripStatus.open);
    });

    test('pullCrops replaces a previously-cached marketplace crop that is no '
        'longer returned (sold/withdrawn crops disappear)', () async {
      // Seed a stale marketplace crop from a prior pull.
      await isar.writeTxn(() async {
        await isar.cropListings.put(
          CropListing()
            ..listingId = 'old-crop'
            ..farmerId = 'other-farmer'
            ..cropType = 'teff'
            ..quantity = 5
            ..pricePerKg = 40
            ..location = (LatLng()
              ..latitude = 0
              ..longitude = 0)
            ..locationName = 'Adama'
            ..description = ''
            ..status = ListingStatus.active
            ..createdAt = DateTime.now()
            ..updatedAt = DateTime.now()
            ..version = 1,
        );
      });

      stubApi(crops: [_cropJson(cropId: 'new-crop')]);
      await buildSync(currentUserId: () async => 'buyer').pullCrops();

      final ids =
          (await isar.cropListings.where().findAll()).map((c) => c.listingId);
      expect(ids, contains('new-crop'));
      expect(ids, isNot(contains('old-crop')));
    });

    test('skips the stale-delete entirely when the current user is unknown '
        '(no unscoped delete-everything)', () async {
      await isar.writeTxn(() async {
        await isar.cropListings.put(
          CropListing()
            ..listingId = 'kept-crop'
            ..farmerId = 'other-farmer'
            ..cropType = 'teff'
            ..quantity = 5
            ..pricePerKg = 40
            ..location = (LatLng()
              ..latitude = 0
              ..longitude = 0)
            ..locationName = 'Adama'
            ..description = ''
            ..status = ListingStatus.active
            ..createdAt = DateTime.now()
            ..updatedAt = DateTime.now()
            ..version = 1,
        );
      });

      stubApi(crops: [_cropJson(cropId: 'new-crop')]);
      await buildSync().pullCrops(); // getCurrentUserId == null

      final ids =
          (await isar.cropListings.where().findAll()).map((c) => c.listingId);
      expect(ids, containsAll(<String>['kept-crop', 'new-crop']));
    });

    test('never deletes a crop that still has an un-synced local write '
        '(shared-device offline data is not lost)', () async {
      await isar.writeTxn(() async {
        await isar.cropListings.put(
          CropListing()
            ..listingId = 'unsynced-crop'
            ..farmerId = 'previous-farmer'
            ..cropType = 'barley'
            ..quantity = 8
            ..pricePerKg = 35
            ..location = (LatLng()
              ..latitude = 0
              ..longitude = 0)
            ..locationName = 'Holeta'
            ..description = ''
            ..status = ListingStatus.active
            ..createdAt = DateTime.now()
            ..updatedAt = DateTime.now()
            ..version = 1,
        );
        // A queued, not-yet-synced upload for that crop.
        await isar.syncOperations.put(
          SyncOperation()
            ..operationId = 'op-1'
            ..idempotencyKey = 'crop_listing:unsynced-crop:create:1'
            ..entityType = 'crop_listing'
            ..entityId = 'unsynced-crop'
            ..action = 'create'
            ..data = '{}'
            ..status = SyncStatus.pending
            ..retryCount = 0
            ..createdAt = DateTime.now(),
        );
      });

      stubApi(crops: [_cropJson(cropId: 'new-crop')]);
      await buildSync(currentUserId: () async => 'buyer').pullCrops();

      final ids =
          (await isar.cropListings.where().findAll()).map((c) => c.listingId);
      expect(
        ids,
        contains('unsynced-crop'),
        reason: 'pending upload must survive the marketplace refresh',
      );
      expect(ids, contains('new-crop'));
    });

    test('throttles back-to-back pulls within the refresh interval', () async {
      stubApi(crops: [_cropJson(cropId: 'first')]);
      final sync = buildSync(currentUserId: () async => 'buyer');
      await sync.pullCrops();
      expect(await isar.cropListings.count(), 1);

      // A second pull moments later must be skipped (no second fetch/rewrite).
      stubApi(crops: [_cropJson(cropId: 'a'), _cropJson(cropId: 'b')]);
      await sync.pullCrops();
      expect(
        await isar.cropListings.count(),
        1,
        reason: 'second pull within 30s should be throttled',
      );
    });

    test('pullCrops never deletes the current user\'s own listings', () async {
      await isar.writeTxn(() async {
        await isar.cropListings.put(
          CropListing()
            ..listingId = 'mine'
            ..farmerId = 'me'
            ..cropType = 'maize'
            ..quantity = 3
            ..pricePerKg = 30
            ..location = (LatLng()
              ..latitude = 0
              ..longitude = 0)
            ..locationName = 'Bishoftu'
            ..description = ''
            ..status = ListingStatus.active
            ..createdAt = DateTime.now()
            ..updatedAt = DateTime.now()
            ..version = 1,
        );
      });

      stubApi(crops: [_cropJson(cropId: 'someone-else')]);
      await buildSync(currentUserId: () async => 'me').pullCrops();

      final ids =
          (await isar.cropListings.where().findAll()).map((c) => c.listingId);
      expect(ids, containsAll(<String>['mine', 'someone-else']));
    });

    test('pullAll fetches both crops and trips', () async {
      stubApi(crops: [_cropJson()], trips: [_tripJson()]);

      await buildSync().pullAll();

      expect(await isar.cropListings.count(), 1);
      expect(await isar.trips.count(), 1);
    });
  });
}
