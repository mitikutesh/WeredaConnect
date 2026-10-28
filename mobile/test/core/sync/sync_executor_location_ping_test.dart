import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

class _FakeConnectivitySource implements ConnectivitySource {
  // Closed in tearDown.
  // ignore: close_sinks
  final controller = StreamController<List<ConnectivityResult>>.broadcast();

  @override
  Stream<List<ConnectivityResult>> get onConnectivityChanged =>
      controller.stream;

  @override
  Future<List<ConnectivityResult>> checkConnectivity() async =>
      [ConnectivityResult.wifi];
}

/// LT-08: how the sync executor sends check-ins and settles the server's
/// answers (live-trip-api.md section 1, sync-protocol.md Location pings).
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late _FakeConnectivitySource source;
  late Dio dio;
  late DioAdapter adapter;
  late ConnectivityMonitor monitor;
  late SecurityEvents events;
  late List<SecurityEvent> emitted;
  late StreamSubscription<SecurityEvent> sub;
  late List<String> endedTrips;
  late List<String> refusedPickups;
  late List<SyncOperation> reverts;
  late SyncExecutor executor;
  late List<Map<String, dynamic>> requests;
  late Map<String, Map<String, Object>> answers;

  const userId = '3f6c1a52-8f4e-4f7b-9a3c-2d1e5b7c9a10';
  const tripId = '9b2d4e6f-1a3c-4b5d-8e7f-0a1b2c3d4e5f';

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('sync_ping_test_');
    isar = await Isar.open(
      [SyncOperationSchema],
      directory: tempDir.path,
      name: 'sync_ping_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    source = _FakeConnectivitySource();
    dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
    adapter = DioAdapter(dio: dio);
    monitor = ConnectivityMonitor(source: source, dio: dio);
    events = SecurityEvents.forTesting();
    emitted = [];
    sub = events.events.listen(emitted.add);
    endedTrips = [];
    refusedPickups = [];
    reverts = [];
    requests = [];
    answers = {};
    executor = SyncExecutor(
      syncQueue: queue,
      connectivityMonitor: monitor,
      dio: dio,
      clientId: 'mobile-1793090520000',
      securityEvents: events,
      onPermanentRejection: (op, _) async => reverts.add(op),
      onLocationSharingEnded: (trip) async => endedTrips.add(trip),
      onPickupRejected: (delivery) async => refusedPickups.add(delivery),
    );
    adapter.onPost(
      '/sync',
      (server) => server.replyCallback(200, (options) {
        final body = Map<String, dynamic>.from(options.data as Map);
        requests.add(body);
        final ops = (body['operations'] as List).cast<Map<String, dynamic>>();
        return {
          'syncBatchId': body['syncBatchId'],
          'results': [
            for (final op in ops)
              {
                'operationId': op['operationId'],
                ...answers[op['operationId']] ?? {'status': 'success'},
              },
          ],
        };
      }),
      data: Matchers.any,
    );
  });

  tearDown(() async {
    await sub.cancel();
    executor.dispose();
    monitor.dispose();
    await source.controller.close();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  var at = DateTime.utc(2026, 10, 28, 8, 42, 0, 123);

  // A fix as the check-in engine queues it (live-trip-api.md section 1):
  // coordinates at 5 decimals, the accuracy in whole metres.
  Future<SyncOperation> enqueuePing(
    String operationId, {
    String trip = tripId,
  }) {
    at = at.add(const Duration(minutes: 1));
    return queue.enqueue(
      userId: userId,
      entityType: 'location_ping',
      entityId: '$trip#${at.millisecondsSinceEpoch}',
      action: 'create',
      data: jsonEncode({
        'tripId': trip,
        'kind': 'fix',
        'capturedAt': at.toIso8601String(),
        'source': 'hourly',
        'latitude': 8.75231,
        'longitude': 38.97851,
        'accuracyM': 35,
      }),
      operationId: operationId,
      keyTimestamp: at,
    );
  }

  Future<SyncOperation> enqueueDelivery(
    String operationId, {
    String status = 'pickedUp',
  }) =>
      queue.enqueue(
        userId: userId,
        entityType: 'delivery',
        entityId: 'delivery-$operationId',
        action: 'update',
        data: jsonEncode({'status': status, 'version': 1}),
        operationId: operationId,
      );

  List<SyncOperationRejected> announced() =>
      emitted.whereType<SyncOperationRejected>().toList();

  test('success and duplicate are synced', () async {
    await enqueuePing('op-1');
    await enqueuePing('op-2');
    answers['op-2'] = {'status': 'duplicate'};

    final result = await executor.executeBatch();

    expect(result.succeeded, 2);
    expect((await queue.getOperation('op-1'))!.status, SyncStatus.synced);
    expect((await queue.getOperation('op-2'))!.status, SyncStatus.synced);
  });

  test('LOCATION_NOT_ACTIVE is deferred: re-queued without a retry', () async {
    await enqueuePing('op-1');
    answers['op-1'] = {
      'status': 'deferred',
      'message': 'LOCATION_NOT_ACTIVE: No delivery on the road yet.',
    };

    final result = await executor.executeBatch();

    expect(result.deferred, 1);
    expect(result.failed, 0);
    final op = (await queue.getOperation('op-1'))!;
    expect(op.status, SyncStatus.pending);
    expect(op.retryCount, 0);
    expect(endedTrips, isEmpty);
  });

  for (final code in [
    'LOCATION_VALIDATION',
    'LOCATION_NOT_TRUCKER',
    'LOCATION_RATE_LIMITED',
    'LOCATION_STALE',
    'LOCATION_SHARING_ENDED',
  ]) {
    test('$code is permanent, never resent and never announced', () async {
      await enqueuePing('op-1');
      answers['op-1'] = {'status': 'error', 'message': '$code: refused.'};

      final result = await executor.executeBatch();

      expect(result.failed, 1);
      final op = (await queue.getOperation('op-1'))!;
      expect(SyncQueue.isRejected(op), isTrue);
      expect(await queue.retryFailed(), 0);
      expect(await queue.getFailedCount(), 0);
      expect(announced(), isEmpty);
      expect(reverts, isEmpty);
      expect(
        endedTrips,
        code == 'LOCATION_SHARING_ENDED' ? [tripId] : isEmpty,
      );
    });
  }

  test('LOCATION_SHARING_ENDED stops sharing once per trip', () async {
    await enqueuePing('op-1');
    await enqueuePing('op-2');
    await enqueuePing('op-3', trip: 'trip-other');
    for (final id in ['op-1', 'op-2', 'op-3']) {
      answers[id] = {
        'status': 'error',
        'message': 'LOCATION_SHARING_ENDED: Every delivery left the road.',
      };
    }

    await executor.executeBatch();

    expect(endedTrips, [tripId, 'trip-other']);
  });

  test('an unprefixed error is retried', () async {
    await enqueuePing('op-1');
    answers['op-1'] = {'status': 'error', 'message': 'Internal error.'};

    await executor.executeBatch();

    final op = (await queue.getOperation('op-1'))!;
    expect(op.status, SyncStatus.pending);
    expect(op.retryCount, 1);
    expect(SyncQueue.isRejected(op), isFalse);
  });

  test('check-ins go after every other operation in a batch', () async {
    await enqueuePing('ping-1');
    await enqueueDelivery('pickup-1');
    await enqueuePing('ping-2');

    await executor.executeBatch();

    final sent = (requests.single['operations'] as List)
        .map((op) => (op as Map)['operationId'])
        .toList();
    expect(sent, ['pickup-1', 'ping-1', 'ping-2']);
  });

  test('deferred check-ins never hold back other writes', () async {
    for (var i = 0; i < SyncExecutor.batchSize; i++) {
      await enqueuePing('ping-$i');
      answers['ping-$i'] = {
        'status': 'deferred',
        'message': 'LOCATION_NOT_ACTIVE: wait.',
      };
    }
    await enqueueDelivery('later', status: 'inTransit');

    final result = await executor.executeBatch();

    final sent = (requests.single['operations'] as List)
        .map((op) => (op as Map)['operationId'])
        .toList();
    expect(sent.first, 'later');
    expect(sent, hasLength(SyncExecutor.batchSize));
    expect(result.succeeded, 1);
    expect((await queue.getOperation('later'))!.status, SyncStatus.synced);
  });

  test('a pickup refused for good is reported with its delivery', () async {
    await enqueueDelivery('pickup-1');
    await enqueueDelivery('confirm-1', status: 'delivered');
    answers['pickup-1'] = {
      'status': 'error',
      'message': 'FRAUD_VELOCITY_EXCEEDED: too many.',
    };
    answers['confirm-1'] = {
      'status': 'error',
      'message': 'FRAUD_VELOCITY_EXCEEDED: too many.',
    };

    await executor.executeBatch();

    expect(refusedPickups, ['delivery-pickup-1']);
  });

  test('a whole batch refused (400) also reports the pickup', () async {
    final failing = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
    DioAdapter(dio: failing).onPost(
      '/sync',
      (server) => server.reply(400, {'error': 'bad'}),
      data: Matchers.any,
    );
    final refusing = SyncExecutor(
      syncQueue: queue,
      connectivityMonitor: monitor,
      dio: failing,
      clientId: 'test',
      securityEvents: events,
      onPickupRejected: (delivery) async => refusedPickups.add(delivery),
    );
    addTearDown(refusing.dispose);
    await enqueueDelivery('pickup-1');

    await refusing.executeBatch();

    expect(refusedPickups, ['delivery-pickup-1']);
  });

  // The data cost line of live-trip.md section 5.3.
  test('one check-in costs about 600 bytes of request body', () async {
    await enqueuePing('7c1e2f3a-4b5c-4d6e-8f70-1a2b3c4d5e6f');

    await executor.executeBatch();

    final body = requests.single;
    final alone = utf8.encode(jsonEncode(body)).length;
    final operation =
        utf8.encode(jsonEncode((body['operations'] as List).single)).length;
    debugPrint('[LT-08] one ping: $alone B alone, $operation B per operation');
    expect(alone, inInclusiveRange(500, 700));
    expect(operation, inInclusiveRange(400, 600));
  });
}
