import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/buyer_intent.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/buyer/data/repositories/buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/new_matches.dart';

class MockSyncQueue extends Mock implements SyncQueue {}

/// Verifies the count-delta "new matches" snapshot semantics end-to-end
/// against a real Isar instance.
void main() {
  group('BuyerIntentRepository new-match snapshot', () {
    late Isar isar;
    late Directory tempDir;
    late BuyerIntentRepository repo;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('buyer_intent_repo_');
      isar = await Isar.open(
        [BuyerIntentSchema],
        directory: tempDir.path,
        name: 'repo_${DateTime.now().microsecondsSinceEpoch}',
      );
      repo = BuyerIntentRepository(isar: isar);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    Future<BuyerIntent> seedIntent() => repo.createIntent(
          buyerId: 'b1',
          cropType: 'wheat',
          quantityNeeded: 5,
          maxPricePerKg: 100,
          deliveryWoreda: 'Adama',
        );

    test('updateLastViewed snapshots matchCount and clears the badge',
        () async {
      final intent = await seedIntent();

      // 3 matches appear, not yet viewed → all 3 are new.
      await repo.updateMatchCount(intent.intentId, 3, DateTime.now().toUtc());
      var fresh = await repo.getIntentById(intent.intentId);
      expect(newMatchCount(fresh!), 3);

      // Buyer views → snapshot clears the badge.
      await repo.updateLastViewed(intent.intentId);
      fresh = await repo.getIntentById(intent.intentId);
      expect(fresh!.lastSeenMatchCount, 3);
      expect(newMatchCount(fresh), 0);

      // A later re-sync that adds 2 more → exactly 2 new.
      await repo.updateMatchCount(intent.intentId, 5, DateTime.now().toUtc());
      fresh = await repo.getIntentById(intent.intentId);
      expect(newMatchCount(fresh!), 2);
    });

    test('re-sync with an unchanged count produces no new-match badge', () async {
      // This is the original bug: the time-based badge flagged "new" on every
      // re-sync. The count-delta badge must not.
      final intent = await seedIntent();
      await repo.updateMatchCount(intent.intentId, 4, DateTime.now().toUtc());
      await repo.updateLastViewed(intent.intentId);

      await repo.updateMatchCount(intent.intentId, 4, DateTime.now().toUtc());
      final fresh = await repo.getIntentById(intent.intentId);
      expect(hasNewMatches(fresh!), isFalse);
    });

    test('a count drop after viewing does not flag new matches', () async {
      final intent = await seedIntent();
      await repo.updateMatchCount(intent.intentId, 6, DateTime.now().toUtc());
      await repo.updateLastViewed(intent.intentId);

      await repo.updateMatchCount(intent.intentId, 2, DateTime.now().toUtc());
      final fresh = await repo.getIntentById(intent.intentId);
      expect(newMatchCount(fresh!), 0);
    });

    test('createIntent enqueues a buyer_intent create op when a queue is set',
        () async {
      final mockQueue = MockSyncQueue();
      when(
        () => mockQueue.enqueue(
          userId: any(named: 'userId'),
          entityType: any(named: 'entityType'),
          entityId: any(named: 'entityId'),
          action: any(named: 'action'),
          data: any(named: 'data'),
          operationId: any(named: 'operationId'),
        ),
      ).thenAnswer((_) async => SyncOperation());

      final syncingRepo = BuyerIntentRepository(isar: isar, syncQueue: mockQueue);
      await syncingRepo.createIntent(
        buyerId: 'b1',
        cropType: 'wheat',
        quantityNeeded: 5,
        maxPricePerKg: 100,
        deliveryWoreda: 'Adama',
      );

      verify(
        () => mockQueue.enqueue(
          userId: 'b1',
          entityType: 'buyer_intent',
          entityId: any(named: 'entityId'),
          action: 'create',
          data: any(named: 'data'),
          operationId: any(named: 'operationId'),
        ),
      ).called(1);
    });
  });
}
