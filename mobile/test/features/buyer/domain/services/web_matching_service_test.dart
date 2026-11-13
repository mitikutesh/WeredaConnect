import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/buyer/data/repositories/web_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/web_matching_service.dart';

class MockApiClient extends Mock implements ApiClient {}

void main() {
  setUpAll(() => registerFallbackValue(<String, dynamic>{}));

  late MockApiClient api;
  late WebMatchingService service;

  setUp(() {
    api = MockApiClient();
    service = WebMatchingService(api: api);
  });

  BuyerIntent intent({
    String cropType = 'wheat',
    String deliveryWoreda = 'Adama',
    double maxPricePerKg = 150,
    double quantityNeeded = 5,
  }) =>
      BuyerIntent()
        ..intentId = 'i1'
        ..buyerId = 'b1'
        ..cropType = cropType
        ..quantityNeeded = quantityNeeded
        ..maxPricePerKg = maxPricePerKg
        ..deliveryWoreda = deliveryWoreda
        ..status = BuyerIntentStatus.active
        ..createdAt = DateTime.now().toUtc()
        ..updatedAt = DateTime.now().toUtc()
        ..version = 1;

  Map<String, dynamic> cropDto({
    required String cropId,
    String cropType = 'wheat',
    String woreda = 'Adama',
    double quantityKg = 10,
    double pricePerKg = 100.0,
    int? ownerReliabilityPct,
  }) =>
      {
        if (ownerReliabilityPct != null)
          'ownerReliabilityPct': ownerReliabilityPct,
        'cropId': cropId,
        'farmerId': 'f1',
        'farmerName': 'Farmer',
        'cropType': cropType,
        'woreda': woreda,
        'quantityKg': quantityKg,
        'pricePerKg': pricePerKg,
        'status': 'available',
        'createdAt': '2026-06-15T08:00:00Z',
      };

  void stubCrops(List<Map<String, dynamic>> crops) {
    when(
      () => api.get<Map<String, dynamic>>(
        '/crops',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer((_) async => {'crops': crops});
  }

  void stubNoTrips() {
    when(
      () => api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer((_) async => {'trips': <dynamic>[]});
  }

  Map<String, dynamic> tripDto({
    required String tripId,
    required String fromWoreda,
    String toWoreda = 'Addis Ababa',
    double capacity = 100,
    double pricePerQuintal = 10,
    String? departureDate,
    int? ownerReliabilityPct,
  }) =>
      {
        if (ownerReliabilityPct != null)
          'ownerReliabilityPct': ownerReliabilityPct,
        'tripId': tripId,
        'truckerId': 't1',
        'truckerName': 'Trucker',
        // ESC-48: GET /trips sends a buyer no truckerPhone.
        'fromWoreda': fromWoreda,
        'toWoreda': toWoreda,
        'departureDate': departureDate ??
            DateTime.now()
                .toUtc()
                .add(const Duration(days: 2))
                .toIso8601String(),
        'capacity': capacity,
        'pricePerQuintal': pricePerQuintal,
        'status': 'open',
        'createdAt': '2026-06-15T08:00:00Z',
      };

  void stubTrips(List<Map<String, dynamic>> trips, {int? orderLeadHours}) {
    when(
      () => api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer(
      (_) async => {
        'trips': trips,
        if (orderLeadHours != null) 'orderLeadHours': orderLeadHours,
      },
    );
  }

  Map<String, dynamic> capturedCropParams() {
    return verify(
      () => api.get<Map<String, dynamic>>(
        '/crops',
        queryParameters: captureAny(named: 'queryParameters'),
      ),
    ).captured.first as Map<String, dynamic>;
  }

  test('matches over GET /crops (not /match) and ranks by relevance', () async {
    // Exact-type wheat should outrank a barley substitute.
    stubCrops([
      cropDto(cropId: 'barley-1', cropType: 'barley'),
      cropDto(cropId: 'wheat-1'),
    ]);
    stubNoTrips();

    final results = await service.matchListingsForIntent(intent());

    expect(results, hasLength(2));
    expect(results[0].listing.listingId, 'wheat-1');
    expect(results[0].isSubstitute, isFalse);
    expect(results[1].listing.listingId, 'barley-1');
    expect(results[1].isSubstitute, isTrue);
    // Score is computed on-device, so it is a real 0..1 relevance value.
    expect(results[0].matchScore, greaterThan(results[1].matchScore));

    // Uses the browse endpoint; never the (undeployed) /match endpoint.
    verify(
      () => api.get<Map<String, dynamic>>(
        '/crops',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).called(1);
    verifyNever(
      () => api.get<Map<String, dynamic>>(
        '/match',
        queryParameters: any(named: 'queryParameters'),
      ),
    );
  });

  test('requests only active (available) listings', () async {
    stubCrops([cropDto(cropId: 'wheat-1')]);
    stubNoTrips();

    await service.matchListingsForIntent(intent());

    expect(capturedCropParams()['status'], 'available');
  });

  // FUP-149: the server leaves deleted listings out of `GET /crops`
  // (crop-listing-api 1.10.0); one that still comes is never a candidate.
  test('a deleted row is never a candidate', () async {
    stubCrops([
      cropDto(cropId: 'wheat-1'),
      {...cropDto(cropId: 'gone-1'), 'status': 'deleted'},
      {...cropDto(cropId: 'gone-2'), 'deletedAt': '2026-11-12T08:00:00Z'},
    ]);
    stubNoTrips();

    final results = await service.matchListingsForIntent(intent());

    expect(results.map((r) => r.listing.listingId), ['wheat-1']);
  });

  test('drops listings outside the price near-miss band', () async {
    // Budget 100; +15% band = 115. The 200/kg listing is excluded.
    stubCrops([
      cropDto(cropId: 'cheap', pricePerKg: 90),
      cropDto(cropId: 'too-expensive', pricePerKg: 200),
    ]);
    stubNoTrips();

    final results = await service.matchListingsForIntent(
      intent(maxPricePerKg: 100),
    );

    expect(results.map((r) => r.listing.listingId), ['cheap']);
  });

  test('empty candidate set returns nothing and skips the trips fetch',
      () async {
    stubCrops([]);

    final results = await service.matchListingsForIntent(intent());

    expect(results, isEmpty);
    verifyNever(
      () => api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: any(named: 'queryParameters'),
      ),
    );
  });

  test('suggests trips across spelling variants of a woreda (SIM-52)',
      () async {
    // Same scenario as the Isar MatchingService test: picker spelling on the
    // crop and intent, typed route with a region suffix on the trip.
    stubCrops([cropDto(cropId: 'wheat-1', woreda: 'Ādama')]);
    stubTrips([
      tripDto(
        tripId: 'trip-typed',
        fromWoreda: 'Adama, Oromia',
        toWoreda: 'addis ababa, Addis Ababa',
      ),
      tripDto(tripId: 'trip-other', fromWoreda: 'Mekelle'),
      tripDto(tripId: 'trip-substring', fromWoreda: 'Gadama'),
    ]);

    final results = await service.matchListingsForIntent(
      intent(deliveryWoreda: 'Addis Ababa'),
    );

    expect(results, hasLength(1));
    expect(
      results.single.suggestedTrips.map((t) => t.tripId),
      ['trip-typed'],
      reason: 'same place across spellings; raw substring is not a match',
    );
    // ESC-48: no number reaches a buyer before an order; the field stays empty.
    expect(results.single.suggestedTrips.single.truckerPhone, isEmpty);
  });

  test('FUP-85: a trip departing today is still suggested later the same day',
      () async {
    // Parity with the Isar MatchingService: a trip leaving today stores its
    // departureDate as the instant of Ethiopia local midnight (UTC+3), already
    // in the past by mid-morning. It must stay eligible until Ethiopia
    // midnight; a trip that left yesterday is dropped.
    final nowEat = DateTime.now().toUtc().add(const Duration(hours: 3));
    final todayStart = DateTime.utc(nowEat.year, nowEat.month, nowEat.day)
        .subtract(const Duration(hours: 3));
    final yesterdayStart = todayStart.subtract(const Duration(days: 1));

    stubCrops([cropDto(cropId: 'wheat-1')]);
    stubTrips([
      tripDto(
        tripId: 'trip-today',
        fromWoreda: 'Adama',
        departureDate: todayStart.toIso8601String(),
      ),
      tripDto(
        tripId: 'trip-yesterday',
        fromWoreda: 'Adama',
        departureDate: yesterdayStart.toIso8601String(),
      ),
    ]);

    final results = await service.matchListingsForIntent(
      intent(deliveryWoreda: 'Addis Ababa'),
    );

    expect(
      results.single.suggestedTrips.map((t) => t.tripId),
      ['trip-today'],
      reason: 'today stays eligible all day; a past day is dropped',
    );
  });

  group('FUP-122: the commitments order lead', () {
    // One fixed clock for the service and the trips, so the 24 hour boundary
    // is exact (the server refuses departureDate < now + 24h).
    final now = DateTime.now().toUtc();
    final nowEat = now.add(const Duration(hours: 3));
    final todayStart = DateTime.utc(nowEat.year, nowEat.month, nowEat.day)
        .subtract(const Duration(hours: 3));
    const allTrips = ['trip-today', 'trip-23h', 'trip-24h', 'trip-30h'];
    const pastTheLead = ['trip-24h', 'trip-30h'];

    // Cheapest first, so the expected order is the order of departure.
    List<Map<String, dynamic>> leadTrips() => [
          tripDto(
            tripId: 'trip-today',
            fromWoreda: 'Adama',
            pricePerQuintal: 5,
            departureDate: todayStart.toIso8601String(),
          ),
          tripDto(
            tripId: 'trip-23h',
            fromWoreda: 'Adama',
            pricePerQuintal: 6,
            departureDate: now.add(const Duration(hours: 23)).toIso8601String(),
          ),
          tripDto(
            tripId: 'trip-24h',
            fromWoreda: 'Adama',
            pricePerQuintal: 7,
            departureDate: now.add(const Duration(hours: 24)).toIso8601String(),
          ),
          tripDto(
            tripId: 'trip-30h',
            fromWoreda: 'Adama',
            pricePerQuintal: 8,
            departureDate: now.add(const Duration(hours: 30)).toIso8601String(),
          ),
        ];

    late WebMatchingService clocked;

    setUp(() {
      clocked = WebMatchingService(api: api, clock: () => now);
      stubCrops([cropDto(cropId: 'wheat-1')]);
    });

    Future<List<String>> suggestedIds() async {
      final results = await clocked.matchListingsForIntent(
        intent(deliveryWoreda: 'Addis Ababa'),
      );
      return results.single.suggestedTrips.map((t) => t.tripId).toList();
    }

    test('on (24): a trip leaving in under 24 hours is not suggested',
        () async {
      stubTrips(leadTrips(), orderLeadHours: 24);

      expect(
        await suggestedIds(),
        pastTheLead,
        reason: 'today and +23h are refused by the order create; '
            '+24h exactly is still taken',
      );
    });

    test('on: findTripsForListing drops the same trips', () async {
      stubTrips(leadTrips(), orderLeadHours: 24);
      final listing = (await clocked.matchListingsForIntent(
        intent(deliveryWoreda: 'Addis Ababa'),
      ))
          .single
          .listing;

      final trips =
          await clocked.findTripsForListing(listing, 'Addis Ababa', 5);

      expect(trips.map((t) => t.tripId), pastTheLead);
    });

    test('off (0): FUP-85 holds, a trip today is still suggested', () async {
      stubTrips(leadTrips(), orderLeadHours: 0);

      expect(await suggestedIds(), allTrips);
    });

    test('a server without the field: FUP-85 holds', () async {
      stubTrips(leadTrips());

      expect(await suggestedIds(), allTrips);
    });
  });

  test('ESC-15: a booked trip is suggested only with room for the intent',
      () async {
    stubCrops([cropDto(cropId: 'wheat-1')]);
    stubTrips([
      tripDto(tripId: 'trip-almost-full', fromWoreda: 'Adama')
        ..['bookedQuintals'] = 98
        ..['remainingQuintals'] = 2,
      tripDto(tripId: 'trip-room', fromWoreda: 'Adama')
        ..['bookedQuintals'] = 50
        ..['remainingQuintals'] = 50,
      tripDto(tripId: 'trip-unbooked', fromWoreda: 'Adama'),
    ]);

    final results = await service.matchListingsForIntent(
      intent(deliveryWoreda: 'Addis Ababa'),
    );

    final trips = results.single.suggestedTrips;
    expect(trips.map((t) => t.tripId), ['trip-room', 'trip-unbooked']);
    expect(trips.first.remainingQuintals, 50);
    expect(trips.last.remainingQuintals, isNull);
  });

  test('refreshMatchCounts persists the eligible candidate count', () async {
    // 3 crops available; only 2 are wheat/barley candidates within budget.
    stubCrops([
      cropDto(cropId: 'wheat-1'),
      cropDto(cropId: 'barley-1', cropType: 'barley'),
      cropDto(cropId: 'coffee-1', cropType: 'coffee'),
    ]);
    stubNoTrips();

    final repo = WebBuyerIntentRepository();
    final created = await repo.createIntent(
      buyerId: 'b1',
      cropType: 'wheat',
      quantityNeeded: 5,
      maxPricePerKg: 150,
      deliveryWoreda: 'Adama',
    );

    await service.refreshMatchCounts('b1', repo);

    final persisted = await repo.getIntentById(created.intentId);
    expect(persisted?.matchCount, 2);
  });

  test('estimated total charges commission on the crop only (SIM-53)',
      () async {
    // 5 quintals at 100 ETB/kg on a 100 ETB/quintal trip: crop 50000,
    // transport 500, commission 5% of the crop (2500). The server total is
    // 53000; commission on crop plus transport would give 53025.
    stubCrops([cropDto(cropId: 'wheat-1')]);
    when(
      () => api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer(
      (_) async => {
        'trips': [
          {
            'tripId': 't1',
            'truckerId': 'tr1',
            'fromWoreda': 'Adama',
            'toWoreda': 'Adama',
            'departureDate': DateTime.now()
                .toUtc()
                .add(const Duration(days: 2))
                .toIso8601String(),
            'capacity': 10,
            'pricePerQuintal': 100,
            'status': 'open',
          },
        ],
      },
    );

    final results = await service.matchListingsForIntent(intent());

    expect(results, hasLength(1));
    expect(results.single.suggestedTrips, hasLength(1));
    expect(results.single.estimatedTotalMin, 53000);
  });

  group('ESC-30: reliability from the API rows', () {
    test('a more reliable farmer outranks an equal listing', () async {
      stubCrops([
        cropDto(cropId: 'crop-flaky', ownerReliabilityPct: 20),
        cropDto(cropId: 'crop-unknown'),
        cropDto(cropId: 'crop-proven', ownerReliabilityPct: 100),
      ]);
      stubNoTrips();

      final results = await service.matchListingsForIntent(intent());

      expect(
        results.map((r) => r.listing.listingId),
        ['crop-proven', 'crop-unknown', 'crop-flaky'],
      );
      expect(results.first.listing.ownerReliabilityPct, 100);
      expect(results[1].listing.ownerReliabilityPct, isNull);
      // 0.10 * (1.0 - 0.7) and 0.10 * (0.7 - 0.2), as on mobile.
      expect(
        results[0].matchScore - results[1].matchScore,
        closeTo(0.03, 1e-9),
      );
      expect(
        results[1].matchScore - results[2].matchScore,
        closeTo(0.05, 1e-9),
      );
    });

    test(
        'the server sends no reliability (flag off): equal scores, and the '
        'old farmerReliability is ignored', () async {
      stubCrops([
        cropDto(cropId: 'crop-pricey'),
        {...cropDto(cropId: 'crop-cheap', pricePerKg: 90)},
        {...cropDto(cropId: 'crop-legacy'), 'farmerReliability': 1.0},
      ]);
      stubNoTrips();

      final results = await service.matchListingsForIntent(intent());

      expect(results, hasLength(3));
      expect(results.first.listing.listingId, 'crop-cheap');
      expect(
        results.map((r) => r.matchScore),
        everyElement(closeTo(results.first.matchScore, 1e-9)),
      );
    });

    // One departure for all, so equal trips fall to the trip id (MATCH-01).
    final goldenDeparture =
        DateTime.now().toUtc().add(const Duration(days: 2)).toIso8601String();

    List<Map<String, dynamic>> goldenTrips() => [
          for (final (id, price, pct) in const <(String, double, int?)>[
            ('pricey-proven', 15, 100),
            ('mid-unknown', 12, null),
            ('mid-low', 12, 40),
            ('mid-proven', 12, 95),
            ('mid-seventy', 12, 70),
            ('cheap-flaky', 10, 20),
          ])
            tripDto(
              tripId: id,
              fromWoreda: 'Adama',
              pricePerQuintal: price,
              ownerReliabilityPct: pct,
              departureDate: goldenDeparture,
            ),
        ];

    test('trip suggestions: price first, then the more reliable trucker',
        () async {
      // The golden vector of rankTripSuggestions, the same as on mobile.
      stubCrops([cropDto(cropId: 'wheat-1')]);
      stubTrips(goldenTrips());

      final results = await service.matchListingsForIntent(
        intent(deliveryWoreda: 'Addis Ababa'),
      );
      final suggested =
          results.single.suggestedTrips.map((t) => t.tripId).toList();

      expect(suggested, [
        'cheap-flaky',
        'mid-proven',
        'mid-seventy',
        'mid-unknown',
        'mid-low',
        'pricey-proven',
      ]);

      final direct = await service.findTripsForListing(
        results.single.listing,
        'Addis Ababa',
        5,
      );
      expect(direct.map((t) => t.tripId), suggested);
    });

    test(
        'no reliability on any trip: price, then the earlier departure, then '
        'the trip id', () async {
      // MATCH-01: the order of the API pages no longer decides a tie; the
      // same vector as on mobile.
      final soon = DateTime.now().toUtc().add(const Duration(days: 2));
      final later = soon.add(const Duration(days: 1)).toIso8601String();
      stubCrops([cropDto(cropId: 'wheat-1')]);
      stubTrips([
        tripDto(
          tripId: 'b',
          fromWoreda: 'Adama',
          pricePerQuintal: 12,
          departureDate: soon.toIso8601String(),
        ),
        tripDto(tripId: 'c', fromWoreda: 'Adama', departureDate: later),
        tripDto(
          tripId: 'a',
          fromWoreda: 'Adama',
          pricePerQuintal: 12,
          departureDate: soon.toIso8601String(),
        ),
        tripDto(
          tripId: 'd',
          fromWoreda: 'Adama',
          departureDate: soon.toIso8601String(),
        ),
      ]);

      final results = await service.matchListingsForIntent(
        intent(deliveryWoreda: 'Addis Ababa'),
      );

      expect(
        results.single.suggestedTrips.map((t) => t.tripId),
        ['d', 'c', 'a', 'b'],
      );
    });
  });

  group('MATCH-01: trips by centroid distance', () {
    // The same scenario as the Isar MatchingService test of this name, so
    // mobile and web suggest the same trips for the same intent.
    final now = DateTime.now().toUtc();
    final departs = now.add(const Duration(days: 3)).toIso8601String();

    late WebMatchingService clocked;

    setUp(() => clocked = WebMatchingService(api: api, clock: () => now));

    Map<String, dynamic> trip(
      String id,
      String from,
      String to,
      double price, {
      int? pct,
      double? remaining,
      DateTime? departure,
    }) =>
        tripDto(
          tripId: id,
          fromWoreda: from,
          toWoreda: to,
          pricePerQuintal: price,
          ownerReliabilityPct: pct,
          departureDate: departure?.toIso8601String() ?? departs,
        )..addAll({if (remaining != null) 'remainingQuintals': remaining});

    List<Map<String, dynamic>> scenario() => [
          trip('exact', 'Hawassa', 'Addis Ababa', 12),
          trip('spelling', 'Hawassa', 'Addis Abeba', 12),
          trip('adjacent', 'Shashemene', 'Addis Ababa', 12, pct: 100),
          trip('fallback', 'Hawassa Zuria', 'Addis Ababa', 12),
          trip('far', 'Jimma', 'Addis Ababa', 9),
          trip('full', 'Hawassa', 'Addis Ababa', 9, remaining: 0),
          trip(
            'late',
            'Hawassa',
            'Addis Ababa',
            9,
            departure: now.add(const Duration(days: 10)),
          ),
        ];

    const expected = ['exact', 'spelling', 'adjacent', 'fallback'];

    test(
        'an adjacent woreda appears below the exact route; far, full and late '
        'trips are dropped', () async {
      stubCrops([cropDto(cropId: 'crop-hawassa', woreda: 'Hawassa')]);
      stubTrips(scenario());

      final results = await clocked.matchListingsForIntent(
        intent(deliveryWoreda: 'Addis Ababa')
          ..neededByDate = now.add(const Duration(days: 5)),
      );

      expect(results, hasLength(1));
      expect(results.single.suggestedTrips.map((t) => t.tripId), expected);
      // The estimated total uses the first suggested trip: crop 50000,
      // commission 2500, transport 5 quintals at 12. Same as mobile.
      expect(results.single.estimatedTotalMin, 52560);

      final direct = await clocked.findTripsForListing(
        results.single.listing,
        'Addis Ababa',
        5,
        neededByDate: now.add(const Duration(days: 5)),
      );
      expect(direct.map((t) => t.tripId), expected);
    });

    test('room within the 0.01 quintal tolerance is enough, as on mobile',
        () async {
      // The server's order create accepts an order up to 0.01 quintal over
      // the room left, so the trip is suggested on web too (it was a strict
      // comparison on web before MATCH-01).
      stubCrops([cropDto(cropId: 'crop-hawassa', woreda: 'Hawassa')]);
      stubTrips([
        trip('fits', 'Hawassa', 'Addis Ababa', 12, remaining: 4.995),
        trip('short', 'Hawassa', 'Addis Ababa', 12, remaining: 4.98),
      ]);

      final results = await clocked.matchListingsForIntent(
        intent(deliveryWoreda: 'Addis Ababa'),
      );

      expect(results.single.suggestedTrips.map((t) => t.tripId), ['fits']);
    });

    test('a trip running the other way is not suggested, even when cheapest',
        () async {
      // The same vector as the Isar MatchingService test of this name.
      stubCrops([cropDto(cropId: 'crop-hawassa', woreda: 'Hawassa')]);
      stubTrips([
        trip('forward', 'Hawassa', 'Shashemene', 12),
        trip('reverse', 'Shashemene', 'Hawassa', 9),
      ]);

      final results = await clocked.matchListingsForIntent(
        intent(deliveryWoreda: 'Shashemene'),
      );

      expect(results.single.suggestedTrips.map((t) => t.tripId), ['forward']);
      expect(results.single.estimatedTotalMin, 52560);
    });
  });
}
