import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/services/match_event_logger.dart';

class MockSyncQueue extends Mock implements SyncQueue {}

class FakeSyncOperation extends Fake implements SyncOperation {}

void main() {
  setUpAll(() {
    registerFallbackValue(FakeSyncOperation());
  });

  late MockSyncQueue queue;
  late MatchEventLogger logger;

  BuyerIntent intent() => BuyerIntent()
    ..intentId = 'intent-1'
    ..buyerId = 'buyer-1'
    ..cropType = 'wheat'
    ..quantityNeeded = 10
    ..maxPricePerKg = 100
    ..deliveryWoreda = 'Adama'
    ..status = BuyerIntentStatus.active
    ..createdAt = DateTime.now().toUtc()
    ..updatedAt = DateTime.now().toUtc()
    ..version = 1;

  CropMatchResult result(String id, {double score = 0.9, bool sub = false}) {
    final listing = CropListing()
      ..listingId = id
      ..farmerId = 'f1'
      ..cropType = 'wheat'
      ..quantity = 10
      ..pricePerKg = 90
      ..location = (LatLng()
        ..latitude = 0
        ..longitude = 0)
      ..locationName = 'Adama'
      ..description = ''
      ..status = ListingStatus.active
      ..createdAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc()
      ..version = 1;
    return CropMatchResult(
      listing: listing,
      suggestedTrips: const [],
      estimatedTotalMin: 0,
      matchScore: score,
      isSubstitute: sub,
    );
  }

  Map<String, dynamic> capturedData() {
    final captured = verify(
      () => queue.enqueue(
        userId: any(named: 'userId'),
        entityType: 'match_event',
        entityId: any(named: 'entityId'),
        action: 'create',
        data: captureAny(named: 'data'),
        operationId: any(named: 'operationId'),
      ),
    ).captured;
    return jsonDecode(captured.single as String) as Map<String, dynamic>;
  }

  setUp(() {
    queue = MockSyncQueue();
    logger = MatchEventLogger(syncQueue: queue);
    when(
      () => queue.enqueue(
        userId: any(named: 'userId'),
        entityType: any(named: 'entityType'),
        entityId: any(named: 'entityId'),
        action: any(named: 'action'),
        data: any(named: 'data'),
        operationId: any(named: 'operationId'),
      ),
    ).thenAnswer((_) async => FakeSyncOperation());
  });

  test('logShown enqueues one event with a capped impression list', () async {
    final results = [
      for (var i = 0; i < 25; i++) result('crop-$i', score: 1 - i * 0.01),
    ];

    await logger.logShown(intent(), results);

    final data = capturedData();
    expect(data['eventType'], 'shown');
    expect(data['matchCount'], 25);
    expect(data['buyerId'], 'buyer-1');
    expect(data['policyTag'], MatchEventLogger.policyTag);
    final impressions = jsonDecode(data['impressions'] as String) as List;
    expect(
      impressions,
      hasLength(MatchEventLogger.maxLoggedImpressions),
      reason: 'the long tail of impressions is dropped',
    );
    expect((impressions.first as Map)['listingId'], 'crop-0');
    expect((impressions.first as Map)['rank'], 0);
  });

  test('logOpened records the listing and its display rank', () async {
    await logger.logOpened(intent(), result('crop-7'), 3);

    final data = capturedData();
    expect(data['eventType'], 'opened');
    expect(data['listingId'], 'crop-7');
    expect(data['rank'], 3);
  });

  test('logOrderCreated records the conversion listing', () async {
    await logger.logOrderCreated(intent(), 'crop-9');

    final data = capturedData();
    expect(data['eventType'], 'order_created');
    expect(data['listingId'], 'crop-9');
  });

  test('a queue failure is swallowed (best-effort telemetry)', () async {
    when(
      () => queue.enqueue(
        userId: any(named: 'userId'),
        entityType: any(named: 'entityType'),
        entityId: any(named: 'entityId'),
        action: any(named: 'action'),
        data: any(named: 'data'),
        operationId: any(named: 'operationId'),
      ),
    ).thenThrow(Exception('queue full'));

    // Must not throw — logging can never break the marketplace flow.
    await logger.logOpened(intent(), result('crop-1'), 0);
  });

  test('a null queue disables logging (web/tests) without error', () async {
    final noop = MatchEventLogger();
    await noop.logShown(intent(), [result('crop-1')]);
    // No queue, no throw, nothing to verify — the call is a silent no-op.
  });
}
