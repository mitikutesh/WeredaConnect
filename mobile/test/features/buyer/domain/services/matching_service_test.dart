import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/buyer_intent.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/features/buyer/data/repositories/buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/matching_service.dart';

/// Direct tests for the Isar-backed on-device [MatchingService] — the trip
/// filtering, capacity boundary, tie-break ordering, and count/results
/// consistency that are NOT covered by the shared scorer tests.
void main() {
  group('MatchingService (Isar-backed)', () {
    late Isar isar;
    late Directory tempDir;
    late MatchingService service;
    late BuyerIntentRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('matching_service_');
      isar = await Isar.open(
        [BuyerIntentSchema, CropListingSchema, TripSchema],
        directory: tempDir.path,
        name: 'match_svc_${DateTime.now().microsecondsSinceEpoch}',
      );
      service = MatchingService(isar: isar);
      repository = BuyerIntentRepository(isar: isar);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    Future<void> putCrop({
      required String id,
      String cropType = 'wheat',
      double quantity = 10,
      double pricePerKg = 100,
      String locationName = 'Assosa',
    }) async {
      await isar.writeTxn(() async {
        await isar.cropListings.putByListingId(
          CropListing()
            ..listingId = id
            ..farmerId = 'farmer-1'
            ..farmerName = 'Farmer'
            ..cropType = cropType
            ..quantity = quantity
            ..pricePerKg = pricePerKg
            ..location = (LatLng()
              ..latitude = 0
              ..longitude = 0)
            ..locationName = locationName
            ..description = ''
            ..status = ListingStatus.active
            ..createdAt = DateTime.now().toUtc()
            ..updatedAt = DateTime.now().toUtc()
            ..version = 1,
        );
      });
    }

    Future<void> putTrip({
      required String id,
      required String fromWoreda,
      String toWoreda = 'AddisAbeba',
      double capacity = 100,
      double pricePerQuintal = 10,
      Duration departsIn = const Duration(days: 2),
      DateTime? departureDate,
    }) async {
      await isar.writeTxn(() async {
        await isar.trips.putByTripId(
          Trip()
            ..tripId = id
            ..truckerId = 'trucker-1'
            ..truckerName = 'Trucker'
            ..truckerPhone = '+251900000000'
            ..fromWoreda = fromWoreda
            ..toWoreda = toWoreda
            ..departureDate =
                departureDate ?? DateTime.now().toUtc().add(departsIn)
            ..capacity = capacity
            ..pricePerQuintal = pricePerQuintal
            ..status = TripStatus.open
            ..matchedListings = <String>[]
            ..createdAt = DateTime.now().toUtc()
            ..updatedAt = DateTime.now().toUtc()
            ..version = 1,
        );
      });
    }

    BuyerIntent buildIntent({
      String cropType = 'wheat',
      double quantityNeeded = 5,
      double maxPricePerKg = 150,
      String deliveryWoreda = 'AddisAbeba',
      DateTime? neededByDate,
    }) {
      return BuyerIntent()
        ..intentId = 'i1'
        ..buyerId = 'buyer-1'
        ..cropType = cropType
        ..quantityNeeded = quantityNeeded
        ..maxPricePerKg = maxPricePerKg
        ..deliveryWoreda = deliveryWoreda
        ..neededByDate = neededByDate
        ..status = BuyerIntentStatus.active
        ..createdAt = DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = 1;
    }

    test('a crop with a blank location does not leak onto every open trip',
        () async {
      // Regression: an empty needle in `String.contains('')` is always true,
      // which would match the crop to every open trip.
      await putCrop(id: 'crop-blank', locationName: '');
      await putTrip(id: 'trip-1', fromWoreda: 'Assosa');

      final results = await service.matchListingsForIntent(buildIntent());

      expect(results, hasLength(1), reason: 'the crop is still a candidate');
      expect(
        results.single.suggestedTrips,
        isEmpty,
        reason: 'a blank crop location must not match any trip',
      );
    });

    test('a trip with capacity exactly equal to the need is included',
        () async {
      // capacityGreaterThan(need - 0.01) → inclusive >= at the boundary.
      // Crop defaults to woreda 'Assosa' (matching the trip); need defaults to 5.
      await putCrop(id: 'crop-1');
      await putTrip(id: 'trip-eq', fromWoreda: 'Assosa', capacity: 5);

      final results = await service.matchListingsForIntent(buildIntent());

      expect(results, hasLength(1));
      expect(
        results.single.suggestedTrips,
        hasLength(1),
        reason: 'capacity == need must be accepted',
      );
    });

    test('neededByDate drops trips departing after the deadline', () async {
      await putCrop(id: 'crop-1'); // woreda 'Assosa'
      // In-time trip departs in 3 days; late trip departs in 10 days.
      await putTrip(
        id: 'trip-intime',
        fromWoreda: 'Assosa',
        departsIn: const Duration(days: 3),
      );
      await putTrip(
        id: 'trip-late',
        fromWoreda: 'Assosa',
        departsIn: const Duration(days: 10),
      );

      final results = await service.matchListingsForIntent(
        buildIntent(
          neededByDate: DateTime.now().toUtc().add(const Duration(days: 5)),
        ),
      );

      expect(results, hasLength(1));
      final tripIds =
          results.single.suggestedTrips.map((t) => t.tripId).toList();
      expect(
        tripIds,
        ['trip-intime'],
        reason: 'the trip departing after the deadline must be dropped',
      );
    });

    test('no neededByDate keeps all otherwise-matching trips', () async {
      await putCrop(id: 'crop-1');
      await putTrip(
        id: 'trip-far-out',
        fromWoreda: 'Assosa',
        departsIn: const Duration(days: 40),
      );

      final results = await service.matchListingsForIntent(buildIntent());

      expect(results.single.suggestedTrips, hasLength(1));
    });

    test('FUP-85: a trip departing today is still suggested later the same day',
        () async {
      // A trip leaving today stores its departureDate as the instant of
      // Ethiopia local midnight (UTC+3), which is already in the past by
      // mid-morning. The old instant filter dropped it; it must now stay
      // eligible until Ethiopia midnight, while yesterday's trip is dropped.
      final nowEat = DateTime.now().toUtc().add(const Duration(hours: 3));
      final todayStart = DateTime.utc(nowEat.year, nowEat.month, nowEat.day)
          .subtract(const Duration(hours: 3));
      final yesterdayStart = todayStart.subtract(const Duration(days: 1));

      await putCrop(id: 'crop-1'); // woreda 'Assosa'
      await putTrip(
        id: 'trip-today',
        fromWoreda: 'Assosa',
        departureDate: todayStart,
      );
      await putTrip(
        id: 'trip-yesterday',
        fromWoreda: 'Assosa',
        departureDate: yesterdayStart,
      );

      final trips = await service.findTripsForListing(
        (await isar.cropListings.getByListingId('crop-1'))!,
        'AddisAbeba',
        5,
      );

      expect(
        trips.map((t) => t.tripId),
        ['trip-today'],
        reason: 'today stays eligible all day; a past day is dropped',
      );
    });

    test('equal-score crops are tie-broken by cheaper crop price', () async {
      // Two identical-scoring crops (both within budget → equal price score;
      // both in woredas disjoint from the delivery woreda → equal geoNeutral).
      // The cheaper crop ranks first — the SAME tie-break as the server's
      // GET /match (score DESC, then pricePerKg ASC), so the ranking does not
      // reorder when the buyer switches between online and offline.
      await putCrop(id: 'crop-pricey', locationName: 'Gambela');
      await putCrop(id: 'crop-cheap', pricePerKg: 90);

      final results = await service.matchListingsForIntent(buildIntent());

      expect(results, hasLength(2));
      expect(
        results.first.matchScore,
        closeTo(results.last.matchScore, 1e-9),
        reason: 'both crops score identically (both within budget)',
      );
      expect(
        results.first.listing.listingId,
        'crop-cheap',
        reason: 'cheaper crop price wins the tie-break (server parity)',
      );
    });

    test('estimated total charges commission on the crop only (SIM-53)',
        () async {
      // 5 quintals at 100 ETB/kg on a 100 ETB/quintal trip: crop 50000,
      // transport 500, commission 5% of the crop (2500). The server total is
      // 53000; commission on crop plus transport would give 53025.
      await putCrop(id: 'crop-1'); // 100 ETB/kg in 'Assosa'
      await putTrip(id: 'trip-1', fromWoreda: 'Assosa', pricePerQuintal: 100);

      final results = await service.matchListingsForIntent(buildIntent());

      expect(results.single.suggestedTrips, hasLength(1));
      expect(results.single.estimatedTotalMin, 53000);

      final listing = results.single.listing;
      final trip = results.single.suggestedTrips.single;
      expect(service.computeEstimatedTotal(listing, trip, 5), 53000);
      expect(
        service.computeEstimatedTotal(listing, null, 5),
        52500,
        reason: 'no trip: crop plus 5% of the crop',
      );
    });

    test('trips are suggested across spelling variants of a woreda (SIM-52)',
        () async {
      // Crop and intent carry the picker spelling; the trucker typed the
      // route with a region suffix and a different case.
      await putCrop(id: 'crop-adama', locationName: 'Ādama');
      await putTrip(
        id: 'trip-typed',
        fromWoreda: 'Adama, Oromia',
        toWoreda: 'addis ababa, Addis Ababa',
      );
      await putTrip(id: 'trip-other', fromWoreda: 'Mekelle');

      final results = await service.matchListingsForIntent(
        buildIntent(deliveryWoreda: 'Addis Ababa'),
      );

      expect(results, hasLength(1));
      expect(
        results.single.suggestedTrips.map((t) => t.tripId),
        ['trip-typed'],
        reason: '"Adama, Oromia" names the same place as "Ādama"',
      );
    });

    test('a trip whose woreda merely contains the crop woreda is not suggested',
        () async {
      await putCrop(id: 'crop-ambo', locationName: 'Ambo');
      await putTrip(id: 'trip-gambo', fromWoreda: 'Gambo');
      await putTrip(id: 'trip-ambo-zone', fromWoreda: 'Ambo Zone');

      final trips = await service.findTripsForListing(
        (await isar.cropListings.getByListingId('crop-ambo'))!,
        'AddisAbeba',
        5,
      );

      expect(
        trips.map((t) => t.tripId),
        ['trip-ambo-zone'],
        reason: 'whole-word overlap matches, a raw substring does not',
      );
    });

    test('refreshMatchCounts persists exactly the match-list length', () async {
      // The badge count must never drift from what matchListingsForIntent
      // returns. Seed 2 candidates + 1 non-candidate (over-budget).
      await putCrop(id: 'cand-1'); // defaults to woreda 'Assosa'
      await putCrop(id: 'cand-2', locationName: 'Gambela');
      await putCrop(id: 'too-pricey', pricePerKg: 1000); // beyond +15% band

      final intent = await repository.createIntent(
        buyerId: 'buyer-1',
        cropType: 'wheat',
        quantityNeeded: 5,
        maxPricePerKg: 100,
        deliveryWoreda: 'AddisAbeba',
      );

      final matches = await service.matchListingsForIntent(intent);
      await service.refreshMatchCounts('buyer-1', repository);
      final persisted = await repository.getIntentById(intent.intentId);

      expect(matches, hasLength(2));
      expect(
        persisted?.matchCount,
        matches.length,
        reason: 'persisted badge count == visible match count',
      );
    });
  });
}
