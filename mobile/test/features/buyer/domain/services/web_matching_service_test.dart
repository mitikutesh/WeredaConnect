import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/network/api_client.dart';
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

  BuyerIntent intent({String deliveryWoreda = 'Adama'}) => BuyerIntent()
    ..intentId = 'i1'
    ..buyerId = 'b1'
    ..cropType = 'wheat'
    ..quantityNeeded = 5
    ..maxPricePerKg = 150
    ..deliveryWoreda = deliveryWoreda
    ..status = BuyerIntentStatus.active
    ..createdAt = DateTime.now().toUtc()
    ..updatedAt = DateTime.now().toUtc()
    ..version = 1;

  Map<String, dynamic> matchDto({
    required String cropId,
    String cropType = 'wheat',
    double score = 0.9,
    bool isSubstitute = false,
  }) => {
    'cropId': cropId,
    'farmerId': 'f1',
    'farmerName': 'Farmer',
    'cropType': cropType,
    'woreda': 'Adama',
    'quantityKg': 10,
    'pricePerKg': 100.0,
    'status': 'available',
    'createdAt': '2026-06-15T08:00:00Z',
    'latitude': 8.54,
    'longitude': 39.27,
    'score': score,
    'isSubstitute': isSubstitute,
  };

  void stubMatch(List<Map<String, dynamic>> matches) {
    when(
      () => api.get<Map<String, dynamic>>(
        '/match',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer((_) async => {'matches': matches, 'count': matches.length});
  }

  void stubNoTrips() {
    when(
      () => api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer((_) async => {'trips': <dynamic>[]});
  }

  Map<String, dynamic> capturedMatchParams() {
    return verify(
      () => api.get<Map<String, dynamic>>(
        '/match',
        queryParameters: captureAny(named: 'queryParameters'),
      ),
    ).captured.single as Map<String, dynamic>;
  }

  test('calls GET /match (not /crops) and preserves server ranking', () async {
    stubMatch([
      matchDto(cropId: 'wheat-1', score: 0.95),
      matchDto(
        cropId: 'barley-1',
        cropType: 'barley',
        score: 0.7,
        isSubstitute: true,
      ),
    ]);
    stubNoTrips();

    final results = await service.matchListingsForIntent(intent());

    expect(results, hasLength(2));
    expect(results[0].listing.listingId, 'wheat-1');
    expect(results[0].matchScore, 0.95);
    expect(results[0].isSubstitute, isFalse);
    expect(results[1].listing.listingId, 'barley-1');
    expect(results[1].isSubstitute, isTrue);

    verify(
      () => api.get<Map<String, dynamic>>(
        '/match',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).called(1);
    verifyNever(
      () => api.get<Map<String, dynamic>>(
        '/crops',
        queryParameters: any(named: 'queryParameters'),
      ),
    );
  });

  test('resolves a known delivery woreda to coordinates in the request', () async {
    stubMatch([matchDto(cropId: 'wheat-1')]);
    stubNoTrips();

    await service.matchListingsForIntent(intent());

    final params = capturedMatchParams();
    expect(params['cropType'], 'wheat');
    expect(params['latitude'], isNotNull); // Adama is in WoredaCentroids
    expect(params['longitude'], isNotNull);
  });

  test('omits coordinates for an unknown woreda (proxy fallback on server)', () async {
    stubMatch([matchDto(cropId: 'wheat-1')]);
    stubNoTrips();

    await service.matchListingsForIntent(intent(deliveryWoreda: 'Nowhereville'));

    final params = capturedMatchParams();
    expect(params.containsKey('latitude'), isFalse);
    expect(params.containsKey('longitude'), isFalse);
  });

  test('empty match result returns nothing and skips the trips fetch', () async {
    stubMatch([]);

    final results = await service.matchListingsForIntent(intent());

    expect(results, isEmpty);
    verifyNever(
      () => api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: any(named: 'queryParameters'),
      ),
    );
  });
}
