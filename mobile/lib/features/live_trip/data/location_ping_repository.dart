import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/database/schemas/location_ping.dart';
import 'package:wereda_connect/core/sync/location_ping_rules.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/live_trip/domain/base_location_ping_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';

/// The trucker's check-ins on mobile (LT-08): a [LocationPing] row in Isar
/// and a create-only `location_ping` operation in the sync queue, which sends
/// it when the phone has network (offline-first: it waits with its real
/// capture time). The sync executor handles the server's answers.
///
/// Mobile-only: imports Isar and the Isar-backed [SyncQueue]. Shared code
/// depends on [BaseLocationPingRepository].
class LocationPingRepository extends BaseLocationPingRepository {
  LocationPingRepository({required Isar isar, required SyncQueue syncQueue})
      : _isar = isar,
        _syncQueue = syncQueue;

  final Isar _isar;
  final SyncQueue _syncQueue;
  static const _uuid = Uuid();

  @override
  Future<PingSendResult> record(
    LocationPing ping, {
    required String userId,
  }) async {
    try {
      await _syncQueue.enqueue(
        userId: userId,
        entityType: locationPingEntityType,
        entityId: ping.pingId,
        action: 'create',
        data: jsonEncode(locationPingData(ping)),
        operationId: _uuid.v4(),
        keyTimestamp: ping.capturedAt,
      );
    } on SyncQueueFullException catch (e) {
      // A lost check-in only leaves a gap in the trail.
      debugPrint('[LiveTrip] check-in dropped, queue full: $e');
      return PingSendResult.dropped;
    } on DuplicateOperationException {
      // Already queued with the same capture time.
      return PingSendResult.queued;
    }
    await _isar.writeTxn(() => _isar.locationPings.put(ping));

    final removed = await _syncQueue.coalesceLocationPings();
    if (removed.isNotEmpty) await _deleteRows(removed);
    return PingSendResult.queued;
  }

  @override
  Future<LocationPing?> latestFix(String tripId, {required String userId}) =>
      _isar.locationPings
          .filter()
          .tripIdEqualTo(tripId)
          .truckerIdEqualTo(userId)
          .kindEqualTo(PingKind.fix)
          .sortByCapturedAtDesc()
          .findFirst();

  @override
  Future<LocationPing?> latest(String tripId, {required String userId}) =>
      _isar.locationPings
          .filter()
          .tripIdEqualTo(tripId)
          .truckerIdEqualTo(userId)
          .sortByCapturedAtDesc()
          .findFirst();

  @override
  Future<List<LocationPing>> pingsFor(
    String tripId, {
    required String userId,
  }) =>
      _isar.locationPings
          .filter()
          .tripIdEqualTo(tripId)
          .truckerIdEqualTo(userId)
          .sortByCapturedAt()
          .findAll();

  @override
  Future<int> unsentCount(String tripId, {required String userId}) =>
      _syncQueue.pendingLocationPingCount(tripId: tripId);

  @override
  Future<int> dropUnsent(String tripId, {required String userId}) async {
    final removed = await _syncQueue.dropLocationPingsForTrip(tripId);
    if (removed.isNotEmpty) await _deleteRows(removed);
    return removed.length;
  }

  /// The sync executor sends the queue.
  @override
  Future<Set<String>> flush({required String userId}) async => const {};

  @override
  Future<void> pruneBefore(DateTime cutoff) async {
    await _isar.writeTxn(
      () => _isar.locationPings.filter().capturedAtLessThan(cutoff).deleteAll(),
    );
  }

  Future<void> _deleteRows(List<String> pingIds) async {
    await _isar.writeTxn(
      () => _isar.locationPings.deleteAllByPingId(pingIds),
    );
  }
}
