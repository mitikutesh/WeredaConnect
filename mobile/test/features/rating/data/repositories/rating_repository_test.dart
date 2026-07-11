import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/rating.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/rating/data/repositories/rating_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

void main() {
  group('RatingRepository', () {
    late Isar isar;
    late Directory tempDir;
    late SyncQueue syncQueue;
    late RatingRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('rating_repo_test_');
      isar = await Isar.open(
        [RatingSchema, SyncOperationSchema],
        directory: tempDir.path,
        name: 'test_rating_repo_${DateTime.now().millisecondsSinceEpoch}',
      );
      syncQueue = SyncQueue(isar);
      // No ApiClient → aggregate reads use the local fallback.
      repository = RatingRepository(isar: isar, syncQueue: syncQueue);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    test('createRating writes locally with a deterministic id', () async {
      final rating = await repository.createRating(
        orderId: 'order-1',
        raterId: 'buyer-1',
        rateeId: 'farmer-1',
        rateeRole: 'farmer',
        stars: 5,
        comment: 'Excellent teff',
      );

      expect(rating.ratingId, 'order-1:farmer-1');
      expect(rating.stars, 5);
      expect(rating.comment, 'Excellent teff');

      final fromDb = await isar.ratings
          .filter()
          .ratingIdEqualTo('order-1:farmer-1')
          .findFirst();
      expect(fromDb, isNotNull);
      expect(fromDb!.rateeRole, 'farmer');
    });

    test('createRating enqueues a rating create sync operation', () async {
      await repository.createRating(
        orderId: 'order-1',
        raterId: 'buyer-1',
        rateeId: 'trucker-1',
        rateeRole: 'trucker',
        stars: 4,
      );

      final pending = await syncQueue.getPendingOperations();
      expect(pending.length, 1);
      expect(pending.first.entityType, 'rating');
      expect(pending.first.action, 'create');
      expect(pending.first.entityId, 'order-1:trucker-1');
    });

    test('blank comment is stored as null', () async {
      final rating = await repository.createRating(
        orderId: 'order-2',
        raterId: 'buyer-1',
        rateeId: 'farmer-1',
        rateeRole: 'farmer',
        stars: 3,
        comment: '   ',
      );

      expect(rating.comment, isNull);
    });

    test('re-rating the same pair upserts (no duplicate local row)', () async {
      await repository.createRating(
        orderId: 'order-1',
        raterId: 'buyer-1',
        rateeId: 'farmer-1',
        rateeRole: 'farmer',
        stars: 2,
      );
      await repository.createRating(
        orderId: 'order-1',
        raterId: 'buyer-1',
        rateeId: 'farmer-1',
        rateeRole: 'farmer',
        stars: 5,
      );

      final all = await isar.ratings
          .filter()
          .ratingIdEqualTo('order-1:farmer-1')
          .findAll();
      expect(all.length, 1);
      expect(all.first.stars, 5);
    });

    test('hasRated reflects the local store', () async {
      expect(
        await repository.hasRated(orderId: 'order-9', rateeId: 'farmer-1'),
        isFalse,
      );

      await repository.createRating(
        orderId: 'order-9',
        raterId: 'buyer-1',
        rateeId: 'farmer-1',
        rateeRole: 'farmer',
        stars: 5,
      );

      expect(
        await repository.hasRated(orderId: 'order-9', rateeId: 'farmer-1'),
        isTrue,
      );
    });

    test(
        'getRatingSummary returns empty when offline (public aggregate is not '
        'derivable from this device\'s own ratings)', () async {
      // These are ratings AUTHORED on this device. Averaging them would present
      // the current user's own rating as the ratee's public score, so the offline
      // path must return empty rather than a misleading aggregate.
      await repository.createRating(
        orderId: 'order-1',
        raterId: 'buyer-1',
        rateeId: 'farmer-1',
        rateeRole: 'farmer',
        stars: 4,
      );
      await repository.createRating(
        orderId: 'order-2',
        raterId: 'buyer-1',
        rateeId: 'farmer-1',
        rateeRole: 'farmer',
        stars: 2,
      );

      final summary = await repository.getRatingSummary('farmer-1');
      expect(summary.count, 0);
      expect(summary.average, 0);
    });

    test('getRatingSummary is empty for an unrated user', () async {
      final summary = await repository.getRatingSummary('nobody');
      expect(summary.count, 0);
      expect(summary.average, 0);
    });

    group('hasRated server lookup (cross-device gate)', () {
      test('returns true when the server has the rating by exact id', () async {
        final api = _MockApiClient();
        final repo = RatingRepository(
          isar: isar,
          syncQueue: syncQueue,
          apiClient: api,
        );
        // Exact-id lookup: no page-2 miss (the bug the paged rateeId scan had).
        when(() => api.get<Map<String, dynamic>>('/ratings/order-x:farmer-1'))
            .thenAnswer(
          (_) async => {
            'rating': {'ratingId': 'order-x:farmer-1'},
          },
        );

        final result =
            await repo.hasRated(orderId: 'order-x', rateeId: 'farmer-1');
        expect(result, isTrue);
      });

      test('falls back to local (false) when the server 404s / errors',
          () async {
        final api = _MockApiClient();
        final repo = RatingRepository(
          isar: isar,
          syncQueue: syncQueue,
          apiClient: api,
        );
        when(() => api.get<Map<String, dynamic>>(any()))
            .thenThrow(Exception('404'));

        final result =
            await repo.hasRated(orderId: 'order-z', rateeId: 'farmer-1');
        expect(result, isFalse);
      });
    });
  });
}
