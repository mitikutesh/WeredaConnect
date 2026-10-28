import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/location_ping.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/live_trip/data/location_ping_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/base_location_ping_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';

/// LT-08: check-ins on mobile are an Isar row plus a `location_ping` create
/// in the sync queue.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late LocationPingRepository repository;
  const trucker = 'trucker-1';

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('location_ping_repo_test_');
    isar = await Isar.open(
      [LocationPingSchema, SyncOperationSchema],
      directory: tempDir.path,
      name: 'location_ping_repo_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    repository = LocationPingRepository(isar: isar, syncQueue: queue);
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  final base = DateTime.utc(2026, 10, 28, 6);

  LocationPing fix(DateTime at, {String trip = 'trip-1', String? source}) =>
      LocationPing()
        ..pingId = locationPingId(trip, at)
        ..tripId = trip
        ..truckerId = trucker
        ..kind = PingKind.fix
        ..source = source ?? CheckInSource.hourly
        ..capturedAt = at
        ..latitude = 8.75
        ..longitude = 38.98
        ..accuracyM = 30;

  test('record writes the row and queues a location_ping create', () async {
    final ping = fix(base);

    final result = await repository.record(ping, userId: trucker);

    expect(result, PingSendResult.queued);
    final ops = await queue.getPendingOperations();
    expect(ops, hasLength(1));
    final op = ops.single;
    expect(op.entityType, 'location_ping');
    expect(op.entityId, ping.pingId);
    expect(op.action, 'create');
    expect(
      op.idempotencyKey,
      '$trucker:location_ping:${ping.pingId}:create:'
      '${base.millisecondsSinceEpoch}',
    );
    expect(jsonDecode(op.data), locationPingData(ping));
    expect(
      (await repository.latestFix('trip-1', userId: trucker))?.pingId,
      ping.pingId,
    );
    expect(await repository.unsentCount('trip-1', userId: trucker), 1);
  });

  test('the same capture time is not queued twice', () async {
    await repository.record(fix(base), userId: trucker);
    final again = await repository.record(fix(base), userId: trucker);

    expect(again, PingSendResult.queued);
    expect(await queue.getPendingOperations(), hasLength(1));
    expect(await isar.locationPings.count(), 1);
  });

  test('coalesced pings leave the log too', () async {
    for (var i = 0; i < 25; i++) {
      await repository.record(
        fix(base.add(Duration(minutes: 2 * i))),
        userId: trucker,
      );
    }

    // 25 fixes in one clock hour: one stays.
    expect(await queue.pendingLocationPingCount(), 1);
    final rows = await repository.pingsFor('trip-1', userId: trucker);
    expect(rows, hasLength(1));
    // Isar reads DateTimes back in local time.
    expect(
      rows.single.capturedAt.toUtc(),
      base.add(const Duration(minutes: 48)),
    );
  });

  test('dropUnsent removes the trip\'s waiting pings and rows', () async {
    await repository.record(fix(base), userId: trucker);
    await repository.record(fix(base, trip: 'trip-2'), userId: trucker);

    expect(await repository.dropUnsent('trip-1', userId: trucker), 1);

    expect(await repository.pingsFor('trip-1', userId: trucker), isEmpty);
    expect(await repository.unsentCount('trip-2', userId: trucker), 1);
  });

  test('latest, latestFix and pruning', () async {
    await repository.record(fix(base), userId: trucker);
    final pause = LocationPing()
      ..pingId = locationPingId('trip-1', base.add(const Duration(hours: 1)))
      ..tripId = 'trip-1'
      ..truckerId = trucker
      ..kind = PingKind.paused
      ..capturedAt = base.add(const Duration(hours: 1))
      ..pausedUntil = base.add(const Duration(hours: 2));
    await repository.record(pause, userId: trucker);

    expect(
      (await repository.latest('trip-1', userId: trucker))?.kind,
      PingKind.paused,
    );
    expect(
      (await repository.latestFix('trip-1', userId: trucker))?.kind,
      PingKind.fix,
    );
    expect(await repository.latest('trip-1', userId: 'someone-else'), isNull);

    await repository.pruneBefore(base.add(const Duration(minutes: 30)));
    expect(await repository.latestFix('trip-1', userId: trucker), isNull);
    expect(await repository.flush(userId: trucker), isEmpty);
  });
}
