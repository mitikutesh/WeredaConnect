import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

/// LT-08: the queue side of check-ins (`location_ping`).
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  var seq = 0;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('sync_queue_ping_test_');
    isar = await Isar.open(
      [SyncOperationSchema],
      directory: tempDir.path,
      name: 'sync_queue_ping_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<SyncOperation> ping(
    DateTime at, {
    String tripId = 'trip-1',
    String kind = 'fix',
    String source = 'hourly',
    String userId = 'trucker-1',
  }) {
    seq++;
    return queue.enqueue(
      userId: userId,
      entityType: 'location_ping',
      entityId: '$tripId#${at.millisecondsSinceEpoch}',
      action: 'create',
      data: jsonEncode({
        'tripId': tripId,
        'kind': kind,
        'capturedAt': at.toIso8601String(),
        if (kind == 'fix') 'source': source,
      }),
      operationId: 'op-$seq',
      keyTimestamp: at,
    );
  }

  final base = DateTime.utc(2026, 10, 28, 6);

  test('the idempotency key carries the capture time', () async {
    final op = await ping(base);
    expect(
      op.idempotencyKey,
      'trucker-1:location_ping:trip-1#${base.millisecondsSinceEpoch}'
      ':create:${base.millisecondsSinceEpoch}',
    );
  });

  test('coalescing waits for more than 24 unsent pings', () async {
    for (var i = 0; i < 24; i++) {
      await ping(base.add(Duration(minutes: 10 * i)));
    }
    expect(await queue.coalesceLocationPings(), isEmpty);
    expect(await queue.pendingLocationPingCount(), 24);
  });

  test('over 24: one fix per clock hour plus every event stays', () async {
    for (var i = 0; i < 30; i++) {
      await ping(base.add(Duration(minutes: 10 * i)));
    }
    await ping(base.add(const Duration(minutes: 7)), kind: 'paused');

    final removed = await queue.coalesceLocationPings();

    // 30 fixes over 5 clock hours keep 5, the pause stays.
    expect(removed, hasLength(25));
    expect(await queue.pendingLocationPingCount(), 6);
    final left = await queue.getPendingOperations(limit: 100);
    expect(
      left.where((op) => op.data.contains('"paused"')),
      hasLength(1),
    );
  });

  test('a ping on its way is never coalesced', () async {
    final first = await ping(base);
    for (var i = 1; i < 30; i++) {
      await ping(base.add(Duration(minutes: i)));
    }
    await queue.markAsSyncing(first.operationId);

    final removed = await queue.coalesceLocationPings();

    expect(removed, isNot(contains(first.entityId)));
    expect(await queue.getOperation(first.operationId), isNotNull);
  });

  test('a refused pickup drops only that trip\'s waiting pings', () async {
    await ping(base);
    final inFlight = await ping(base.add(const Duration(minutes: 1)));
    await queue.markAsSyncing(inFlight.operationId);
    await ping(base, tripId: 'trip-2');
    await queue.enqueue(
      userId: 'trucker-1',
      entityType: 'delivery',
      entityId: 'trip-1#not-a-ping',
      action: 'update',
      data: '{}',
      operationId: 'op-delivery',
    );

    final removed = await queue.dropLocationPingsForTrip('trip-1');

    expect(removed, ['trip-1#${base.millisecondsSinceEpoch}']);
    expect(await queue.pendingLocationPingCount(tripId: 'trip-1'), 1);
    expect(await queue.pendingLocationPingCount(tripId: 'trip-2'), 1);
    expect(await queue.getOperation('op-delivery'), isNotNull);
  });

  test('a refused check-in is not counted as a failed sync', () async {
    final op = await ping(base);
    await queue.markAsRejected(op.operationId);
    expect(await queue.getFailedCount(), 0);

    // One that ran out of retries still counts.
    final other = await ping(base.add(const Duration(minutes: 1)));
    for (var i = 0; i < SyncQueue.maxRetries; i++) {
      await queue.markAsFailed(other.operationId);
    }
    expect(await queue.getFailedCount(), 1);
  });

  test('pending reads can keep or leave out one entity type', () async {
    await ping(base);
    await queue.enqueue(
      userId: 'trucker-1',
      entityType: 'delivery',
      entityId: 'd-1',
      action: 'update',
      data: '{}',
      operationId: 'op-d',
    );

    final others = await queue.getPendingOperationsForUser(
      userId: 'trucker-1',
      exceptEntityType: 'location_ping',
    );
    final pings = await queue.getPendingOperationsForUser(
      userId: 'trucker-1',
      onlyEntityType: 'location_ping',
    );
    final anyone = await queue.getPendingOperationsForUser(
      userId: null,
      onlyEntityType: 'location_ping',
    );

    expect(others.map((o) => o.entityType), ['delivery']);
    expect(pings.map((o) => o.entityType), ['location_ping']);
    expect(anyone, hasLength(1));
  });
}
