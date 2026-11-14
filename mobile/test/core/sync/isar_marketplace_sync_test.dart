import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/isar_marketplace_sync.dart';
import 'package:wereda_connect/core/sync/order_lead_store.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/trip_listing/data/repositories/trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';

import '../../helpers/wait_for.dart';

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

Map<String, dynamic> _deliveryJson({
  String deliveryId = 'del-1',
  String status = 'pickedUp',
  int version = 2,
  String? deliveryType,
}) =>
    {
      if (deliveryType != null) 'deliveryType': deliveryType,
      'deliveryId': deliveryId,
      'tripId': 'trip-1',
      'listingId': 'crop-1',
      'farmerId': 'farmer-1',
      'farmerName': 'Abebe',
      'truckerId': 'trucker-1',
      'truckerName': 'Geleta',
      'buyerId': 'buyer-1',
      'buyerName': 'Mekonnen',
      'qrToken': '',
      'status': status,
      'createdAt': '2026-06-18T09:42:06.000Z',
      'updatedAt': '2026-06-18T10:00:00.000Z',
      'version': version,
    };

Delivery _localDelivery({
  String deliveryId = 'del-1',
  DeliveryStatus status = DeliveryStatus.pending,
  int version = 1,
}) =>
    Delivery()
      ..deliveryId = deliveryId
      ..tripId = 'trip-1'
      ..listingId = 'crop-1'
      ..farmerId = 'farmer-1'
      ..farmerName = 'Abebe'
      ..truckerId = 'trucker-1'
      ..truckerName = 'Geleta'
      ..buyerId = 'buyer-1'
      ..buyerName = 'Mekonnen'
      ..qrToken = ''
      ..status = status
      ..createdAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc()
      ..version = version;

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
        [CropListingSchema, TripSchema, DeliverySchema, SyncOperationSchema],
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
      List<Map<String, dynamic>> deliveries = const [],
      int? orderLeadHours,
    }) {
      when(
        () => api.get<Map<String, dynamic>>(
          any(),
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((inv) async {
        final path = inv.positionalArguments.first as String;
        if (path.contains('/crops')) return {'crops': crops};
        if (path.contains('/trips')) {
          return {
            'trips': trips,
            if (orderLeadHours != null) 'orderLeadHours': orderLeadHours,
          };
        }
        if (path.contains('/deliveries')) return {'deliveries': deliveries};
        return <String, dynamic>{};
      });
    }

    IsarMarketplaceSync buildSync({
      Future<String?> Function()? currentUserId,
      DateTime Function()? clock,
    }) =>
        IsarMarketplaceSync(
          api: api,
          isar: isar,
          getCurrentUserId: currentUserId,
          clock: clock,
        );

    test(
        'pullCrops stores crops with quantity (quintals) taken verbatim from '
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

    test(
        'ESC-30: the owner reliability of crops and trips is stored for the '
        'offline matcher, and cleared when the server stops sending it',
        () async {
      stubApi(
        crops: [
          {..._cropJson(), 'ownerReliabilityPct': 83},
          _cropJson(cropId: 'crop-2'),
        ],
        trips: [
          {..._tripJson(), 'ownerReliabilityPct': 91},
          _tripJson(tripId: 'trip-2'),
        ],
      );
      await buildSync().pullAll();

      Future<Map<String, int?>> cropPcts() async => {
            for (final c in await isar.cropListings.where().findAll())
              c.listingId: c.ownerReliabilityPct,
          };
      Future<Map<String, int?>> tripPcts() async => {
            for (final t in await isar.trips.where().findAll())
              t.tripId: t.ownerReliabilityPct,
          };
      expect(await cropPcts(), {'crop-1': 83, 'crop-2': null});
      expect(await tripPcts(), {'trip-1': 91, 'trip-2': null});

      // The flag goes off: the next pull carries no owner field.
      stubApi(crops: [_cropJson()], trips: [_tripJson()]);
      await buildSync().pullAll();

      expect((await cropPcts())['crop-1'], isNull);
      expect((await tripPcts())['trip-1'], isNull);
    });

    test(
        'ESC-31: the rest of the owner reputation copy is stored on crops '
        'and trips, and cleared when the server stops sending it', () async {
      const owner = {
        'ownerTier': 'low',
        'ownerEffectiveRating': 4.2,
        'ownerRatingCount': 12,
        'ownerReliabilityPct': 83,
        'ownerReputationAt': '2026-11-07T09:00:00.000Z',
      };
      stubApi(
        crops: [
          {..._cropJson(), ...owner},
          _cropJson(cropId: 'crop-2'),
        ],
        trips: [
          {..._tripJson(), ...owner},
          _tripJson(tripId: 'trip-2'),
        ],
      );
      await buildSync().pullAll();

      final crops = {
        for (final c in await isar.cropListings.where().findAll())
          c.listingId: c,
      };
      final trips = {
        for (final t in await isar.trips.where().findAll()) t.tripId: t,
      };
      for (final (tier, rating, count, at) in [
        (
          crops['crop-1']!.ownerTier,
          crops['crop-1']!.ownerEffectiveRating,
          crops['crop-1']!.ownerRatingCount,
          crops['crop-1']!.ownerReputationAt,
        ),
        (
          trips['trip-1']!.ownerTier,
          trips['trip-1']!.ownerEffectiveRating,
          trips['trip-1']!.ownerRatingCount,
          trips['trip-1']!.ownerReputationAt,
        ),
      ]) {
        expect(tier, 'low');
        expect(rating, 4.2);
        expect(count, 12);
        expect(at?.toUtc(), DateTime.utc(2026, 11, 7, 9));
      }
      // A row the server sent without a copy has none.
      expect(crops['crop-2']!.ownerTier, isNull);
      expect(crops['crop-2']!.ownerEffectiveRating, isNull);
      expect(crops['crop-2']!.ownerRatingCount, isNull);
      expect(crops['crop-2']!.ownerReputationAt, isNull);
      expect(trips['trip-2']!.ownerTier, isNull);
      expect(trips['trip-2']!.ownerReputationAt, isNull);

      // The flag goes off: the next pull carries no owner field.
      stubApi(crops: [_cropJson()], trips: [_tripJson()]);
      await buildSync().pullAll();

      final crop = (await isar.cropListings.where().findAll())
          .firstWhere((c) => c.listingId == 'crop-1');
      final trip = (await isar.trips.where().findAll())
          .firstWhere((t) => t.tripId == 'trip-1');
      expect(crop.ownerTier, isNull);
      expect(crop.ownerEffectiveRating, isNull);
      expect(crop.ownerRatingCount, isNull);
      expect(crop.ownerReputationAt, isNull);
      expect(trip.ownerTier, isNull);
      expect(trip.ownerEffectiveRating, isNull);
    });

    group('FUP-122: the order lead from GET /trips', () {
      setUp(() => SharedPreferences.setMockInitialValues(<String, Object>{}));

      test('pullTrips keeps the lead the server reported', () async {
        stubApi(trips: [_tripJson()], orderLeadHours: 24);

        await buildSync().pullTrips();

        expect(await OrderLeadStore().read(), 24);
        expect(await isar.trips.where().findAll(), hasLength(1));
      });

      test('a lead of 0 (commitments off) is kept as 0', () async {
        await OrderLeadStore().write(24);
        stubApi(trips: [_tripJson()], orderLeadHours: 0);

        await buildSync().pullTrips();

        expect(await OrderLeadStore().read(), 0);
      });

      test('a server that sends no lead forgets the stored one', () async {
        await OrderLeadStore().write(24);
        stubApi(trips: [_tripJson()]);

        await buildSync().pullTrips();

        expect(await OrderLeadStore().read(), isNull);
      });

      test('a failed pull leaves the stored lead alone (offline)', () async {
        await OrderLeadStore().write(24);
        when(
          () => api.get<Map<String, dynamic>>(
            any(),
            queryParameters: any(named: 'queryParameters'),
          ),
        ).thenThrow(Exception('offline'));

        await expectLater(buildSync().pullTrips(), throwsException);

        expect(await OrderLeadStore().read(), 24);
      });
    });

    test(
        'pullCrops replaces a previously-cached marketplace crop that is no '
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

    test(
        'skips the stale-delete entirely when the current user is unknown '
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

    test(
        'never deletes a crop that still has an un-synced local write '
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

    // FUP-149: the crop parse read a status the app has no name for as
    // `active`. The server leaves deleted rows out of its lists
    // (crop-listing-api 1.10.0); a pull that still carries one removes the
    // listing instead of showing it as on the market.
    group('FUP-149: a deleted row in the crop pull', () {
      CropListing cached(String listingId, String farmerId) => CropListing()
        ..listingId = listingId
        ..farmerId = farmerId
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
        ..version = 1;

      for (final (name, deletion) in [
        (
          'stamp and status',
          {'status': 'deleted', 'deletedAt': '2026-11-12T08:00:00.000Z'},
        ),
        ('status only', <String, dynamic>{'status': 'deleted'}),
        ('stamp only', {'deletedAt': '2026-11-12T08:00:00.000Z'}),
      ]) {
        test("the buyer's browse drops it ($name)", () async {
          await isar.writeTxn(
            () => isar.cropListings.put(cached('gone', 'farmer-1')),
          );
          stubApi(
            crops: [
              {..._cropJson(cropId: 'gone'), ...deletion},
              _cropJson(cropId: 'live'),
            ],
          );

          await buildSync(currentUserId: () async => 'buyer').pullCrops();

          final stored = await isar.cropListings.where().findAll();
          expect(stored.map((c) => c.listingId), ['live']);
          expect(stored.single.status, ListingStatus.active);
        });
      }

      test("the farmer's own copy goes too (the stale delete keeps it)",
          () async {
        await isar.writeTxn(() async {
          await isar.cropListings.put(cached('mine-gone', 'me'));
          await isar.cropListings.put(cached('mine-kept', 'me'));
        });
        stubApi(
          crops: [
            {
              ..._cropJson(cropId: 'mine-gone', farmerId: 'me'),
              'status': 'deleted',
            },
          ],
        );

        await buildSync(currentUserId: () async => 'me').pullCrops();

        final ids =
            (await isar.cropListings.where().findAll()).map((c) => c.listingId);
        expect(ids, ['mine-kept']);
      });

      test('the user unknown: still dropped, nothing else is touched',
          () async {
        await isar.writeTxn(() async {
          await isar.cropListings.put(cached('gone', 'farmer-1'));
          await isar.cropListings.put(cached('kept', 'farmer-2'));
        });
        stubApi(
          crops: [
            {..._cropJson(cropId: 'gone'), 'status': 'deleted'},
          ],
        );

        await buildSync().pullCrops();

        final ids =
            (await isar.cropListings.where().findAll()).map((c) => c.listingId);
        expect(ids, ['kept']);
      });
    });

    test('pullAll fetches both crops and trips', () async {
      stubApi(crops: [_cropJson()], trips: [_tripJson()]);

      await buildSync().pullAll();

      expect(await isar.cropListings.count(), 1);
      expect(await isar.trips.count(), 1);
    });

    test('pullDeliveries stores the buyer\'s deliveries from the API',
        () async {
      stubApi(deliveries: [_deliveryJson()]);

      await buildSync(currentUserId: () async => 'buyer-1').pullDeliveries();

      final stored = await isar.deliverys.where().findAll();
      expect(stored, hasLength(1));
      expect(stored.single.deliveryId, 'del-1');
      expect(stored.single.status, DeliveryStatus.pickedUp);
    });

    test(
        'pullDeliveries parses a delivery an admin dispute disposition closed '
        '(FUP-16): the extra verdict keys are ignored', () async {
      stubApi(
        deliveries: [
          {
            ..._deliveryJson(status: 'cancelled', version: 3),
            'cancelReason': 'dispute_buyer_wins',
            'cancelledAt': '2026-10-22T10:00:00.000Z',
            'disputeResolution': 'buyer_wins',
            'disputeResolvedAt': '2026-10-22T10:00:00.000Z',
          },
          {
            ..._deliveryJson(
              deliveryId: 'del-2',
              status: 'disputed',
              version: 3,
            ),
            'confirmedAt': '2026-10-22T10:00:00.000Z',
            'disputeResolution': 'buyer_wins',
            'disputeResolvedAt': '2026-10-22T10:00:00.000Z',
          },
        ],
      );

      await buildSync(currentUserId: () async => 'buyer-1').pullDeliveries();

      final stored = {
        for (final d in await isar.deliverys.where().findAll()) d.deliveryId: d,
      };
      expect(stored['del-1']!.status, DeliveryStatus.cancelled);
      expect(stored['del-2']!.status, DeliveryStatus.disputed);
      // Isar hands DateTimes back in local time: compare the instant.
      expect(
        stored['del-2']!.confirmedAt!.toUtc(),
        DateTime.utc(2026, 10, 22, 10),
      );
    });

    test('pullDeliveries keeps a self-pickup delivery\'s type', () async {
      stubApi(
        deliveries: [
          _deliveryJson(status: 'pending', deliveryType: 'self_pickup'),
        ],
      );

      await buildSync(currentUserId: () async => 'buyer-1').pullDeliveries();

      final stored = await isar.deliverys.where().findAll();
      expect(stored.single.deliveryType, 'self_pickup');
    });

    test('pullDeliveries scopes by farmerId (a farmer is not the buyer key)',
        () async {
      stubApi(deliveries: [_deliveryJson()]);

      await buildSync(currentUserId: () async => 'farmer-1').pullDeliveries();

      // A farmer never authors a delivery and is not the ParticipantIndex key,
      // so the pull must query the farmer GSI (?farmerId=), not only ?mine=true.
      final queries = verify(
        () => api.get<Map<String, dynamic>>(
          any(),
          queryParameters: captureAny(named: 'queryParameters'),
        ),
      ).captured.cast<Map<String, dynamic>>();
      expect(
        queries.any((q) => q['farmerId'] == 'farmer-1'),
        isTrue,
        reason: 'farmer deliveries are only reachable via ?farmerId=',
      );
    });

    test('pullDeliveries scopes by truckerId (trucker cross-device pull)',
        () async {
      stubApi(deliveries: [_deliveryJson()]);

      await buildSync(currentUserId: () async => 'trucker-1').pullDeliveries();

      // A trucker is neither the buyer key nor the farmer, so the pull must also
      // query the trucker GSI (?truckerId=, SIM-16).
      final queries = verify(
        () => api.get<Map<String, dynamic>>(
          any(),
          queryParameters: captureAny(named: 'queryParameters'),
        ),
      ).captured.cast<Map<String, dynamic>>();
      expect(
        queries.any((q) => q['truckerId'] == 'trucker-1'),
        isTrue,
        reason: 'trucker deliveries are reachable via ?truckerId=',
      );
    });

    test(
        'pullDeliveries upserts a newer server status onto an existing '
        'local delivery (no duplicate)', () async {
      // Local: pending, version 1 (helper defaults).
      await isar.writeTxn(() async {
        await isar.deliverys.put(_localDelivery());
      });

      // Server: newer view — pickedUp, version 2 (helper defaults).
      stubApi(deliveries: [_deliveryJson()]);
      await buildSync(currentUserId: () async => 'buyer-1').pullDeliveries();

      final stored = await isar.deliverys.where().findAll();
      expect(
        stored,
        hasLength(1),
        reason: 'upsert by deliveryId, not duplicate',
      );
      expect(stored.single.status, DeliveryStatus.pickedUp);
      expect(stored.single.version, 2);
    });

    // FRD-12: the trucker's pickup photo waits in the queue (its own
    // delivery_photo op), so the server has no key yet; the pull keeps the
    // phone's file path so the photo still shows. A server key wins.
    test(
        'pullDeliveries keeps this phone\'s photo path until the server '
        'holds the photo', () async {
      const local = '/data/user/0/app/files/photos/del-1/pickup-1.webp';
      await isar.writeTxn(() async {
        await isar.deliverys.put(
          _localDelivery(status: DeliveryStatus.pickedUp, version: 2)
            ..pickupPhotoRef = local,
        );
      });

      stubApi(deliveries: [_deliveryJson()]);
      final sync = buildSync(currentUserId: () async => 'trucker-1');
      await sync.pullDeliveries();
      expect((await isar.deliverys.where().findFirst())!.pickupPhotoRef, local);

      const key = 'uploads/photos/del-1/pickup-1700000000000.webp';
      stubApi(
        deliveries: [
          {..._deliveryJson(), 'pickupPhotoRef': key},
        ],
      );
      await buildSync(currentUserId: () async => 'trucker-1').pullDeliveries();
      expect((await isar.deliverys.where().findFirst())!.pickupPhotoRef, key);
    });

    // FUP-336 (ESC-36): the trucker's arrival photo is kept the same way,
    // and the pull reads the system's confirm for a silent buyer.
    test(
        'pullDeliveries keeps the arrival photo path and reads '
        'autoConfirmedAt', () async {
      const local = '/data/user/0/app/files/photos/del-1/arrival-1.webp';
      await isar.writeTxn(() async {
        await isar.deliverys.put(
          _localDelivery(status: DeliveryStatus.pickedUp, version: 2)
            ..arrivalPhotoRef = local,
        );
      });

      stubApi(deliveries: [_deliveryJson()]);
      await buildSync(currentUserId: () async => 'trucker-1').pullDeliveries();
      var stored = (await isar.deliverys.where().findFirst())!;
      expect(stored.arrivalPhotoRef, local);
      expect(stored.autoConfirmedAt, isNull);

      const key = 'uploads/photos/del-1/arrival-1700000000000.webp';
      stubApi(
        deliveries: [
          {
            ..._deliveryJson(),
            'arrivalPhotoRef': key,
            'autoConfirmedAt': '2026-11-10T08:01:00Z',
          },
        ],
      );
      await buildSync(currentUserId: () async => 'trucker-1').pullDeliveries();
      stored = (await isar.deliverys.where().findFirst())!;
      expect(stored.arrivalPhotoRef, key);
      expect(stored.autoConfirmedAt!.toUtc(), DateTime.utc(2026, 11, 10, 8, 1));
    });

    test(
        'pullDeliveries never clobbers a delivery with an un-synced local '
        'write (offline buyer confirmation is preserved)', () async {
      await isar.writeTxn(() async {
        // Buyer confirmed delivery offline (version bumped locally, not synced).
        await isar.deliverys
            .put(_localDelivery(status: DeliveryStatus.delivered, version: 3));
        await isar.syncOperations.put(
          SyncOperation()
            ..operationId = 'op-d1'
            ..idempotencyKey = 'buyer-1:delivery:del-1:update:1'
            ..entityType = 'delivery'
            ..entityId = 'del-1'
            ..action = 'update'
            ..data = '{}'
            ..status = SyncStatus.pending
            ..retryCount = 0
            ..createdAt = DateTime.now(),
        );
      });

      // Server still shows the older pre-confirmation view (pickedUp, v2).
      stubApi(deliveries: [_deliveryJson()]);
      await buildSync(currentUserId: () async => 'buyer-1').pullDeliveries();

      final stored = await isar.deliverys.where().findAll();
      expect(
        stored.single.status,
        DeliveryStatus.delivered,
        reason: 'pending local confirmation must survive the pull',
      );
      expect(stored.single.version, 3);
    });

    // FUP-374: the server moves a trucker's trip without an upload from the
    // phone (matched at a funding, or open / matched again when an admin's
    // no-show re-open revives a trip the order cron closed). The trucker's
    // own-trip pull brings that status to the phone.
    group('FUP-374: pullOwnTrips', () {
      Map<String, dynamic> serverTrip({
        String tripId = 'trip-1',
        String truckerId = 'trucker-1',
        String status = 'open',
        int version = 1,
      }) =>
          {
            ..._tripJson(tripId: tripId, truckerId: truckerId),
            'status': status,
            'version': version,
          };

      /// The phone's copy of trip-1.
      Future<void> storeTrip({
        required TripStatus status,
        required int version,
        String? toPlaceId,
      }) =>
          isar.writeTxn(
            () => isar.trips.put(
              Trip()
                ..tripId = 'trip-1'
                ..truckerId = 'trucker-1'
                ..truckerName = 'Geleta'
                ..truckerPhone = '+251931831670'
                ..fromWoreda = 'Assosa'
                ..toWoreda = 'AddisAbeba'
                ..toPlaceId = toPlaceId
                ..departureDate = DateTime.utc(2026, 6, 20, 21)
                ..capacity = 100
                ..pricePerQuintal = 10
                ..status = status
                ..matchedListings = <String>[]
                ..createdAt = DateTime.utc(2026, 6, 18)
                ..updatedAt = DateTime.utc(2026, 6, 18)
                ..version = version,
            ),
          );

      Future<Trip> storedTrip() async =>
          (await isar.trips.filter().tripIdEqualTo('trip-1').findFirst())!;

      /// The one update of trip-1 in the queue, written again on each call
      /// (same id) with [status].
      Future<void> queueTripUpdate({
        required SyncStatus status,
        int retryCount = 0,
      }) =>
          isar.writeTxn(
            () => isar.syncOperations.put(
              SyncOperation()
                ..id = 1
                ..operationId = 'op-t1'
                ..idempotencyKey = 'trucker-1:trip:trip-1:update:1'
                ..entityType = 'trip'
                ..entityId = 'trip-1'
                ..action = 'update'
                ..data = '{"status":"cancelled","version":3}'
                ..status = status
                ..retryCount = retryCount
                ..createdAt = DateTime.now(),
            ),
          );

      test(
          'reads every page of the trucker\'s own trips by truckerId, with no '
          'status filter', () async {
        final requests = <Map<String, dynamic>>[];
        when(
          () => api.get<Map<String, dynamic>>(
            any(),
            queryParameters: any(named: 'queryParameters'),
          ),
        ).thenAnswer((inv) async {
          final params = Map<String, dynamic>.from(
            inv.namedArguments[#queryParameters] as Map,
          );
          requests.add(params);
          return params['nextToken'] == null
              ? {
                  'trips': [serverTrip(status: 'matched', version: 2)],
                  'nextToken': 'page-2',
                }
              : {
                  'trips': [
                    serverTrip(
                      tripId: 'trip-2',
                      status: 'inTransit',
                      version: 3,
                    ),
                  ],
                };
        });

        await buildSync().pullOwnTrips(truckerId: 'trucker-1');

        verify(
          () => api.get<Map<String, dynamic>>(
            '/trips',
            queryParameters: any(named: 'queryParameters'),
          ),
        ).called(2);
        expect(requests.first, {'truckerId': 'trucker-1', 'limit': '50'});
        expect(requests.last['nextToken'], 'page-2');
        expect(requests.any((r) => r.containsKey('status')), isFalse);
        expect(
          {
            for (final t in await isar.trips.where().findAll())
              t.tripId: t.status,
          },
          {'trip-1': TripStatus.matched, 'trip-2': TripStatus.inTransit},
        );
      });

      test(
          'a trip stored as cancelled from a conflict answer comes back as '
          'matched with the server\'s newer version, is the card\'s trip and '
          'can start', () async {
        // The order cron closed the trip (expired, v3); a conflict answer
        // told the phone.
        await storeTrip(status: TripStatus.cancelled, version: 3);
        final repository =
            TripListingRepository(isar: isar, syncQueue: SyncQueue(isar));
        expect(await repository.getMostRelevantTrip('trucker-1'), isNull);
        await expectLater(
          repository.startTrip(tripId: 'trip-1', truckerId: 'trucker-1'),
          throwsA(isA<TripValidationException>()),
        );

        // An admin's no-show re-open opened it again, full: matched, v4.
        stubApi(trips: [serverTrip(status: 'matched', version: 4)]);
        await buildSync().pullOwnTrips(truckerId: 'trucker-1');

        final trip = await storedTrip();
        expect(trip.status, TripStatus.matched);
        expect(trip.version, 4);
        expect(
          (await repository.getMostRelevantTrip('trucker-1'))?.status,
          TripStatus.matched,
        );

        final started = await repository.startTrip(
          tripId: 'trip-1',
          truckerId: 'trucker-1',
        );
        expect(started.status, TripStatus.inTransit);
        final op = (await isar.syncOperations.where().findAll()).single;
        expect(jsonDecode(op.data), {'status': 'inTransit', 'version': 4});
      });

      test('a trip re-opened as open replaces the stored cancelled row',
          () async {
        await storeTrip(status: TripStatus.cancelled, version: 3);
        stubApi(trips: [serverTrip(version: 4)]);

        await buildSync().pullOwnTrips(truckerId: 'trucker-1');

        final trip = await storedTrip();
        expect(trip.status, TripStatus.open);
        expect(trip.version, 4);
      });

      test(
          'a server copy older than the stored row is skipped (the trucker '
          'index lags a start just synced)', () async {
        await storeTrip(status: TripStatus.inTransit, version: 5);
        stubApi(trips: [serverTrip(status: 'matched', version: 4)]);

        await buildSync().pullOwnTrips(truckerId: 'trucker-1');

        final trip = await storedTrip();
        expect(trip.status, TripStatus.inTransit);
        expect(trip.version, 5);
      });

      test(
          'a trip upload still on its way keeps the row; a refused one does not',
          () async {
        // The trucker cancelled offline: the update waits in the queue.
        await storeTrip(status: TripStatus.cancelled, version: 4);
        await queueTripUpdate(status: SyncStatus.pending);
        stubApi(trips: [serverTrip(status: 'matched', version: 4)]);

        await buildSync().pullOwnTrips(truckerId: 'trucker-1');
        expect((await storedTrip()).status, TripStatus.cancelled);

        // Refused for good: the server's copy wins.
        await queueTripUpdate(
          status: SyncStatus.failed,
          retryCount: SyncQueue.maxRetries + 1,
        );
        await buildSync().pullOwnTrips(truckerId: 'trucker-1');
        expect((await storedTrip()).status, TripStatus.matched);
      });

      test(
          'keeps the place ids only this phone has, and never stores another '
          'trucker\'s trip', () async {
        await storeTrip(
          status: TripStatus.open,
          version: 1,
          toPlaceId: 'addis-ababa',
        );
        stubApi(
          trips: [
            serverTrip(status: 'matched', version: 2),
            serverTrip(tripId: 'trip-x', truckerId: 'trucker-2'),
          ],
        );

        await buildSync().pullOwnTrips(truckerId: 'trucker-1');

        final trip = await storedTrip();
        expect(trip.status, TripStatus.matched);
        expect(trip.toPlaceId, 'addis-ababa');
        expect(
          await isar.trips.filter().tripIdEqualTo('trip-x').findFirst(),
          isNull,
        );
      });

      test('is throttled for the same trucker, not for another account',
          () async {
        stubApi(trips: [serverTrip()]);
        final sync = buildSync();

        await sync.pullOwnTrips(truckerId: 'trucker-1');
        await sync.pullOwnTrips(truckerId: 'trucker-1');
        verify(
          () => api.get<Map<String, dynamic>>(
            '/trips',
            queryParameters: any(named: 'queryParameters'),
          ),
        ).called(1);

        await sync.pullOwnTrips(truckerId: 'trucker-2');
        verify(
          () => api.get<Map<String, dynamic>>(
            '/trips',
            queryParameters: any(named: 'queryParameters'),
          ),
        ).called(1);
      });

      // FUP-385: the pull ran every 30 seconds on the trucker's home and
      // read the whole history each time.
      group('FUP-385: the cost of the pull', () {
        var now = DateTime.utc(2026, 11, 13, 9);
        DateTime clock() => now;
        late List<Map<String, dynamic>> requests;

        setUp(() {
          now = DateTime.utc(2026, 11, 13, 9);
          requests = [];
        });

        /// The server's answer: [trips] (newest created first) in pages of
        /// 50, the next page's start as the token.
        void stubPages(List<Map<String, dynamic>> Function() trips) {
          when(
            () => api.get<Map<String, dynamic>>(
              any(),
              queryParameters: any(named: 'queryParameters'),
            ),
          ).thenAnswer((inv) async {
            final params = Map<String, dynamic>.from(
              inv.namedArguments[#queryParameters] as Map,
            );
            requests.add(params);
            final all = trips();
            final start = int.parse(params['nextToken'] as String? ?? '0');
            final end = start + 50 < all.length ? start + 50 : all.length;
            return {
              'trips': all.sublist(start, end),
              if (end < all.length) 'nextToken': '$end',
            };
          });
        }

        /// The trucker's trip number [i] of a long history, newest first:
        /// created in August and September, delivered weeks ago.
        Map<String, dynamic> historyTrip(
          int i, {
          String status = 'delivered',
          int version = 3,
          DateTime? departure,
        }) {
          final created =
              DateTime.utc(2026, 9, 30).subtract(Duration(hours: i));
          return {
            ...serverTrip(tripId: 'trip-$i', status: status, version: version),
            'createdAt': created.toIso8601String(),
            'updatedAt': created.toIso8601String(),
            'departureDate': (departure ?? created.add(const Duration(days: 2)))
                .toIso8601String(),
          };
        }

        Future<TripStatus?> statusOf(String tripId) async =>
            (await isar.trips.filter().tripIdEqualTo(tripId).findFirst())
                ?.status;

        test(
            'a trucker with 150 trips reads every page once, then one page '
            'a pull, while the newest trip moves', () async {
          var newestStatus = 'open';
          var newestUpdated = now;
          final history = [
            for (var i = 1; i < 150; i++) historyTrip(i),
          ];
          stubPages(
            () => [
              {
                ...serverTrip(tripId: 'trip-0', status: newestStatus),
                'createdAt': '2026-11-12T08:00:00.000Z',
                'updatedAt': newestUpdated.toIso8601String(),
                'departureDate': '2026-11-20T21:00:00.000Z',
              },
              ...history,
            ],
          );
          // An open trip the order cron closed after it left (never listed
          // again, FUP-384) does not keep the paging going.
          await isar.writeTxn(
            () => isar.trips.put(
              Trip()
                ..tripId = 'trip-gone'
                ..truckerId = 'trucker-1'
                ..truckerName = 'Geleta'
                ..truckerPhone = '+251931831670'
                ..fromWoreda = 'Assosa'
                ..toWoreda = 'AddisAbeba'
                ..departureDate = DateTime.utc(2026, 8, 10)
                ..capacity = 100
                ..pricePerQuintal = 10
                ..status = TripStatus.open
                ..matchedListings = <String>[]
                ..createdAt = DateTime.utc(2026, 8)
                ..updatedAt = DateTime.utc(2026, 8)
                ..version = 1,
            ),
          );
          final sync = buildSync(clock: clock);

          expect(await sync.pullOwnTrips(truckerId: 'trucker-1'), isTrue);
          expect(requests, hasLength(3));
          expect(
            await isar.trips.filter().truckerIdEqualTo('trucker-1').count(),
            151,
          );

          // The next pull: the first page ends on a trip the phone holds as
          // delivered weeks ago.
          requests.clear();
          now = now.add(const Duration(seconds: 31));
          expect(await sync.pullOwnTrips(truckerId: 'trucker-1'), isFalse);
          expect(requests, hasLength(1));
          expect(requests.single.containsKey('nextToken'), isFalse);

          // A funding filled the newest trip: still one page, and the card
          // is told to read again.
          requests.clear();
          newestStatus = 'matched';
          newestUpdated = now;
          now = now.add(const Duration(seconds: 31));
          expect(await sync.pullOwnTrips(truckerId: 'trucker-1'), isTrue);
          expect(requests, hasLength(1));
          expect(await statusOf('trip-0'), TripStatus.matched);

          // A new run of the app reads every page once again.
          requests.clear();
          await buildSync(clock: clock).pullOwnTrips(truckerId: 'trucker-1');
          expect(requests, hasLength(3));
        });

        test(
            'a trip the phone holds as cancelled that an admin re-opened on '
            'a later page is still read', () async {
          // trip-120 left two days ago, booked (posted weeks ahead).
          var trip120 = historyTrip(
            120,
            status: 'matched',
            departure: now.subtract(const Duration(days: 2)),
          );
          stubPages(
            () => [
              for (var i = 0; i < 150; i++)
                if (i == 120) trip120 else historyTrip(i),
            ],
          );
          final sync = buildSync(clock: clock);
          await sync.pullOwnTrips(truckerId: 'trucker-1');

          // The phone learned of the cron's close from a conflict answer...
          await isar.writeTxn(() async {
            final row = (await isar.trips
                .filter()
                .tripIdEqualTo('trip-120')
                .findFirst())!
              ..status = TripStatus.cancelled
              ..version = 4;
            await isar.trips.put(row);
          });
          // ...and an admin's no-show re-open opened the trip again.
          trip120 = historyTrip(
            120,
            status: 'matched',
            version: 5,
            departure: now.subtract(const Duration(days: 2)),
          );

          requests.clear();
          now = now.add(const Duration(seconds: 31));
          expect(await sync.pullOwnTrips(truckerId: 'trucker-1'), isTrue);

          expect(requests, hasLength(3));
          final row = (await isar.trips
              .filter()
              .tripIdEqualTo('trip-120')
              .findFirst())!;
          expect(row.status, TripStatus.matched);
          expect(row.version, 5);
        });

        test(
            'a trip the phone holds as in transit that left weeks ago, on a '
            'later page, is read until the server finishes it', () async {
          // trip-120 left 30 days ago and is still on the road.
          var trip120 = historyTrip(
            120,
            status: 'inTransit',
            departure: now.subtract(const Duration(days: 30)),
          );
          stubPages(
            () => [
              for (var i = 0; i < 150; i++)
                if (i == 120) trip120 else historyTrip(i),
            ],
          );
          final sync = buildSync(clock: clock);
          await sync.pullOwnTrips(truckerId: 'trucker-1');
          expect(await statusOf('trip-120'), TripStatus.inTransit);

          // The buyer's late confirm finished it on the server.
          trip120 = historyTrip(
            120,
            version: 4,
            departure: now.subtract(const Duration(days: 30)),
          );
          requests.clear();
          now = now.add(const Duration(seconds: 31));
          expect(await sync.pullOwnTrips(truckerId: 'trucker-1'), isTrue);
          expect(requests, hasLength(3));
          expect(await statusOf('trip-120'), TripStatus.delivered);

          // Delivered, it no longer keeps the paging going.
          requests.clear();
          now = now.add(const Duration(seconds: 31));
          await sync.pullOwnTrips(truckerId: 'trucker-1');
          expect(requests, hasLength(1));
        });

        test(
            'the first page is read again when its last trip left within '
            'the last 14 days', () async {
          stubPages(
            () => [
              for (var i = 0; i < 60; i++)
                historyTrip(
                  i,
                  departure:
                      i == 49 ? now.subtract(const Duration(days: 13)) : null,
                ),
            ],
          );
          final sync = buildSync(clock: clock);
          await sync.pullOwnTrips(truckerId: 'trucker-1');

          requests.clear();
          now = now.add(const Duration(seconds: 31));
          await sync.pullOwnTrips(truckerId: 'trucker-1');

          expect(requests, hasLength(2));
        });

        test(
            'a load that comes while the pull is on its way waits for it '
            'and makes no request of its own', () async {
          final answer = Completer<Map<String, dynamic>>();
          when(
            () => api.get<Map<String, dynamic>>(
              any(),
              queryParameters: any(named: 'queryParameters'),
            ),
          ).thenAnswer((_) => answer.future);
          final sync = buildSync(clock: clock);

          final first = sync.pullOwnTrips(truckerId: 'trucker-1');
          final second = sync.pullOwnTrips(truckerId: 'trucker-1');
          answer.complete({
            'trips': [serverTrip(status: 'matched', version: 2)],
          });

          expect(await first, isTrue);
          expect(await second, isTrue);
          verify(
            () => api.get<Map<String, dynamic>>(
              '/trips',
              queryParameters: any(named: 'queryParameters'),
            ),
          ).called(1);
          expect(await statusOf('trip-1'), TripStatus.matched);
        });

        test('a failed pull is not repeated within 30 seconds', () async {
          when(
            () => api.get<Map<String, dynamic>>(
              any(),
              queryParameters: any(named: 'queryParameters'),
            ),
          ).thenThrow(Exception('offline'));
          final sync = buildSync(clock: clock);

          await expectLater(
            sync.pullOwnTrips(truckerId: 'trucker-1'),
            throwsException,
          );
          expect(await sync.pullOwnTrips(truckerId: 'trucker-1'), isFalse);
          verify(
            () => api.get<Map<String, dynamic>>(
              '/trips',
              queryParameters: any(named: 'queryParameters'),
            ),
          ).called(1);

          now = now.add(const Duration(seconds: 31));
          await expectLater(
            sync.pullOwnTrips(truckerId: 'trucker-1'),
            throwsException,
          );
          verify(
            () => api.get<Map<String, dynamic>>(
              '/trips',
              queryParameters: any(named: 'queryParameters'),
            ),
          ).called(1);
        });

        test(
            'keeps the stored trucker name and phone when the server row '
            'has none', () async {
          await storeTrip(status: TripStatus.open, version: 1);
          stubApi(
            trips: [
              serverTrip(status: 'matched', version: 2)
                ..remove('truckerName')
                ..remove('truckerPhone'),
            ],
          );

          await buildSync().pullOwnTrips(truckerId: 'trucker-1');

          final trip = await storedTrip();
          expect(trip.status, TripStatus.matched);
          expect(trip.truckerName, 'Geleta');
          expect(trip.truckerPhone, '+251931831670');
        });
      });

      group('the trucker\'s list (LoadTrips)', () {
        TripListingBloc buildBloc() => TripListingBloc(
              repository:
                  TripListingRepository(isar: isar, syncQueue: SyncQueue(isar)),
              marketplaceSync: buildSync(),
            );

        test('shows the revived trip as matched', () async {
          await storeTrip(status: TripStatus.cancelled, version: 3);
          stubApi(trips: [serverTrip(status: 'matched', version: 4)]);
          final bloc = buildBloc();
          addTearDown(bloc.close);

          bloc.add(const LoadTrips(truckerId: 'trucker-1'));
          await waitFor(() => bloc.state is TripsLoaded);

          final trips = (bloc.state as TripsLoaded).trips;
          expect(trips.single.status, TripStatus.matched);
          expect(trips.single.version, 4);
        });

        test('offline, shows the stored trips', () async {
          await storeTrip(status: TripStatus.cancelled, version: 3);
          when(
            () => api.get<Map<String, dynamic>>(
              any(),
              queryParameters: any(named: 'queryParameters'),
            ),
          ).thenThrow(Exception('offline'));
          final bloc = buildBloc();
          addTearDown(bloc.close);

          bloc.add(const LoadTrips(truckerId: 'trucker-1'));
          await waitFor(() => bloc.state is TripsLoaded);

          expect(
            (bloc.state as TripsLoaded).trips.single.status,
            TripStatus.cancelled,
          );
        });
      });
    });
  });
}
