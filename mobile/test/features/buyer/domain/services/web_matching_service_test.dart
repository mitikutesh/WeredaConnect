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
  }) =>
      {
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
  }) =>
      {
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
}
