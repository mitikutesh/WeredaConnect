import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/buyer_intent.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/buyer/data/intent_wire.dart';
import 'package:wereda_connect/features/buyer/data/repositories/buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/data/repositories/web_buyer_intent_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

class _MockSyncQueue extends Mock implements SyncQueue {}

/// A `GET /intents` row (push-alerts-api.md 1.5.0).
Map<String, dynamic> _row(
  String intentId, {
  String cropType = 'teff',
  String createdAt = '2026-11-01T08:00:00.0000000Z',
}) =>
    {
      'intentId': intentId,
      'cropType': cropType,
      'quantityNeeded': 12.5,
      'maxPricePerKg': 95,
      'deliveryWoreda': 'Adama',
      'deliveryLatitude': 8.54,
      'deliveryLongitude': 39.27,
      'neededByDate': '2026-12-01T00:00:00.000Z',
      'status': 'active',
      'createdAt': createdAt,
      'updatedAt': createdAt,
      'version': 2,
    };

Map<String, dynamic> _body(List<Map<String, dynamic>> rows) =>
    {'intents': rows, 'count': rows.length};

/// SIM-56: My Requests reads the buyer's own requests back from the server.
void main() {
  group('IntentWire.parseList', () {
    test('reads every field and owns each row by the caller', () {
      final intents = IntentWire.parseList(
        _body([_row('i-1')]),
        buyerId: 'buyer-1',
      );

      expect(intents, hasLength(1));
      final intent = intents.single;
      expect(intent.intentId, 'i-1');
      expect(intent.buyerId, 'buyer-1');
      expect(intent.cropType, 'teff');
      expect(intent.quantityNeeded, 12.5);
      expect(intent.maxPricePerKg, 95);
      expect(intent.deliveryWoreda, 'Adama');
      expect(intent.neededByDate, DateTime.utc(2026, 12));
      expect(intent.status, BuyerIntentStatus.active);
      expect(intent.createdAt, DateTime.utc(2026, 11, 1, 8));
      expect(intent.version, 2);
      expect(intent.matchCount, 0);
      expect(intent.placeId, isNull);
    });

    test('skips a row without an id or a crop, and any other shape', () {
      expect(
        IntentWire.parseList(
          _body([
            _row('ok'),
            {..._row('no-crop'), 'cropType': ''},
            {..._row('x')}..remove('intentId'),
            {'not': 'a row'},
          ]),
          buyerId: 'b',
        ).map((i) => i.intentId),
        ['ok'],
      );
      expect(IntentWire.parseList(null, buyerId: 'b'), isEmpty);
      expect(IntentWire.parseList({'intents': 'x'}, buyerId: 'b'), isEmpty);
    });
  });

  group('BuyerIntentRepository.refreshFromServer (Isar)', () {
    late Isar isar;
    late Directory tempDir;
    late _MockApiClient api;
    late _MockSyncQueue queue;
    late BuyerIntentRepository repo;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('intent_read_back_');
      isar = await Isar.open(
        [BuyerIntentSchema],
        directory: tempDir.path,
        name: 'read_back_${DateTime.now().microsecondsSinceEpoch}',
      );
      api = _MockApiClient();
      queue = _MockSyncQueue();
      when(() => queue.hasWaitingOperationsFor(any(), any()))
          .thenAnswer((_) async => false);
      when(
        () => queue.enqueue(
          userId: any(named: 'userId'),
          entityType: any(named: 'entityType'),
          entityId: any(named: 'entityId'),
          action: any(named: 'action'),
          data: any(named: 'data'),
          operationId: any(named: 'operationId'),
        ),
      ).thenThrow(StateError('not used'));
      repo = BuyerIntentRepository(isar: isar, syncQueue: queue, api: api);
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
    });

    void serverHas(List<Map<String, dynamic>> rows) {
      when(() => api.get<Map<String, dynamic>>('/intents'))
          .thenAnswer((_) async => _body(rows));
    }

    test('an empty phone gets the server rows, newest first, active', () async {
      serverHas([
        _row('old', createdAt: '2026-10-01T08:00:00Z'),
        _row('new', createdAt: '2026-11-05T08:00:00Z'),
      ]);

      expect(await repo.refreshFromServer('buyer-1'), isTrue);

      final active = await repo.getActiveIntents('buyer-1');
      expect(active.map((i) => i.intentId), ['new', 'old']);
      expect(active.first.buyerId, 'buyer-1');
    });

    test('a row the phone has is never overwritten', () async {
      final local = await repo.createIntent(
        buyerId: 'buyer-1',
        cropType: 'maize',
        quantityNeeded: 3,
        maxPricePerKg: 50,
        deliveryWoreda: 'Meki',
      );
      await repo.updateMatchCount(local.intentId, 4, DateTime.now().toUtc());
      serverHas([_row(local.intentId, cropType: 'wheat')]);

      expect(await repo.refreshFromServer('buyer-1'), isFalse);

      final kept = await repo.getIntentById(local.intentId);
      expect(kept!.cropType, 'maize');
      expect(kept.matchCount, 4);
    });

    test('a request with a change not yet accepted is not brought back',
        () async {
      when(() => queue.hasWaitingOperationsFor('buyer_intent', 'deleted'))
          .thenAnswer((_) async => true);
      serverHas([_row('deleted'), _row('kept')]);

      expect(await repo.refreshFromServer('buyer-1'), isTrue);

      final ids =
          (await repo.getActiveIntents('buyer-1')).map((i) => i.intentId);
      expect(ids, ['kept']);
    });

    test('nothing local is removed when the server lacks it', () async {
      await isar.writeTxn(() async {
        await isar.buyerIntents.put(
          BuyerIntent()
            ..intentId = 'only-here'
            ..buyerId = 'buyer-1'
            ..cropType = 'wheat'
            ..quantityNeeded = 1
            ..maxPricePerKg = 1
            ..deliveryWoreda = 'Adama'
            ..status = BuyerIntentStatus.active
            ..createdAt = DateTime.now().toUtc()
            ..updatedAt = DateTime.now().toUtc()
            ..version = 1,
        );
      });
      serverHas([]);

      expect(await repo.refreshFromServer('buyer-1'), isFalse);
      expect(await repo.getActiveIntents('buyer-1'), hasLength(1));
    });

    test('a failed read throws and changes nothing', () async {
      when(() => api.get<Map<String, dynamic>>('/intents'))
          .thenThrow(Exception('offline'));

      await expectLater(repo.refreshFromServer('buyer-1'), throwsException);
      expect(await repo.getActiveIntents('buyer-1'), isEmpty);
    });

    test('without an API client there is no request', () async {
      final offline = BuyerIntentRepository(isar: isar);
      expect(await offline.refreshFromServer('buyer-1'), isFalse);
      verifyNever(() => api.get<Map<String, dynamic>>(any()));
    });
  });

  group('WebBuyerIntentRepository.refreshFromServer', () {
    late _MockApiClient api;
    late WebBuyerIntentRepository repo;

    setUp(() {
      api = _MockApiClient();
      when(
        () => api.post<dynamic>(any(), data: any<dynamic>(named: 'data')),
      ).thenAnswer((_) async => <String, dynamic>{});
      repo = WebBuyerIntentRepository(api: api);
    });

    void serverHas(List<Map<String, dynamic>> rows) {
      when(() => api.get<Map<String, dynamic>>('/intents'))
          .thenAnswer((_) async => _body(rows));
    }

    test('after a reload the list comes back from the server', () async {
      serverHas([_row('a'), _row('b', createdAt: '2026-11-09T08:00:00Z')]);

      expect(await repo.getActiveIntents('buyer-1'), isEmpty);
      expect(await repo.refreshFromServer('buyer-1'), isTrue);

      final ids =
          (await repo.getActiveIntents('buyer-1')).map((i) => i.intentId);
      expect(ids, ['b', 'a']);
    });

    test('a request deleted or fulfilled in this session does not come back',
        () async {
      serverHas([_row('a'), _row('b'), _row('c')]);
      await repo.refreshFromServer('buyer-1');

      await repo.deleteIntent('a');
      await repo.fulfillIntent('b');
      expect(await repo.refreshFromServer('buyer-1'), isFalse);

      final ids =
          (await repo.getActiveIntents('buyer-1')).map((i) => i.intentId);
      expect(ids, ['c']);
    });

    test('a request created in this session is kept as it is', () async {
      final mine = await repo.createIntent(
        buyerId: 'buyer-1',
        cropType: 'maize',
        quantityNeeded: 2,
        maxPricePerKg: 40,
        deliveryWoreda: 'Meki',
      );
      serverHas([_row(mine.intentId, cropType: 'wheat')]);

      expect(await repo.refreshFromServer('buyer-1'), isFalse);
      expect((await repo.getIntentById(mine.intentId))!.cropType, 'maize');
    });
  });
}
