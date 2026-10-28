import 'package:flutter/foundation.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/location_ping.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/location_ping_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/base_location_ping_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';

/// The trucker's check-ins on web (LT-08). Web has no sync queue and no
/// background, so each ping is posted to `/sync` at once, while the page is
/// open. A ping the server defers (`LOCATION_NOT_ACTIVE`) or that cannot be
/// sent (no network, an unprefixed error) waits in memory and goes with the
/// next one or the next [flush]; the same coalescing rule as the mobile queue
/// keeps that list short. Reloading the page drops what waits: the web
/// fallback is best effort, the QR scans remain the evidence.
class WebLocationPingRepository extends BaseLocationPingRepository {
  WebLocationPingRepository({required ApiClient api}) : _api = api;

  final ApiClient _api;
  static const _uuid = Uuid();

  /// Every ping recorded on this page, oldest first.
  final List<LocationPing> _rows = [];

  /// Pings not stored by the server yet, by ping id, oldest first.
  final Map<String, _Unsent> _unsent = {};

  @override
  Future<PingSendResult> record(
    LocationPing ping, {
    required String userId,
  }) async {
    _rows
      ..removeWhere((r) => r.pingId == ping.pingId)
      ..add(ping);
    _unsent[ping.pingId] = _Unsent(
      ping: ping,
      operationId: _uuid.v4(),
      idempotencyKey: locationPingIdempotencyKey(userId, ping),
    );
    _coalesce();
    final outcome = await _send(userId);
    return outcome.results[ping.pingId] ?? PingSendResult.queued;
  }

  @override
  Future<Set<String>> flush({required String userId}) async {
    if (_unsent.isEmpty) return const {};
    return (await _send(userId)).endedTrips;
  }

  @override
  Future<LocationPing?> latestFix(String tripId, {required String userId}) =>
      Future.value(
        _mine(tripId, userId).where((p) => p.kind == PingKind.fix).lastOrNull,
      );

  @override
  Future<LocationPing?> latest(String tripId, {required String userId}) =>
      Future.value(_mine(tripId, userId).lastOrNull);

  @override
  Future<List<LocationPing>> pingsFor(
    String tripId, {
    required String userId,
  }) =>
      Future.value(_mine(tripId, userId).toList());

  @override
  Future<int> unsentCount(String tripId, {required String userId}) =>
      Future.value(
        _unsent.values
            .where((u) => u.ping.tripId == tripId && u.ping.truckerId == userId)
            .length,
      );

  @override
  Future<int> dropUnsent(String tripId, {required String userId}) async {
    final ids = [
      for (final u in _unsent.values)
        if (u.ping.tripId == tripId && u.ping.truckerId == userId)
          u.ping.pingId,
    ];
    ids.forEach(_unsent.remove);
    _rows.removeWhere((r) => ids.contains(r.pingId));
    return ids.length;
  }

  @override
  Future<void> pruneBefore(DateTime cutoff) async {
    _rows.removeWhere((r) => r.capturedAt.isBefore(cutoff));
  }

  Iterable<LocationPing> _mine(String tripId, String userId) {
    final rows = _rows
        .where((r) => r.tripId == tripId && r.truckerId == userId)
        .toList()
      ..sort((a, b) => a.capturedAt.compareTo(b.capturedAt));
    return rows;
  }

  void _coalesce() {
    final drop = locationPingsToCoalesce([
      for (final u in _unsent.values)
        UnsentLocationPing(
          key: u.ping.pingId,
          tripId: u.ping.tripId,
          kind: u.ping.kind,
          source: u.ping.source,
          capturedAt: u.ping.capturedAt,
        ),
    ]);
    drop.forEach(_unsent.remove);
    _rows.removeWhere((r) => drop.contains(r.pingId));
  }

  /// Post every waiting ping of [userId] in one `/sync` request and settle
  /// each answer (`sync-protocol.md` Location pings).
  Future<_SendOutcome> _send(String userId) async {
    final batch = [
      for (final u in _unsent.values)
        if (u.ping.truckerId == userId) u,
    ];
    if (batch.isEmpty) return const _SendOutcome({}, {});

    final Map<String, dynamic> response;
    try {
      response = await _api.post<Map<String, dynamic>>(
        '/sync',
        data: {
          'syncBatchId': _uuid.v4(),
          'clientId': 'web',
          'operations': [
            for (final u in batch)
              {
                'operationId': u.operationId,
                'idempotencyKey': u.idempotencyKey,
                'entityType': locationPingEntityType,
                'entityId': u.ping.pingId,
                'action': 'create',
                'data': locationPingData(u.ping),
                'timestamp': u.ping.capturedAt.toUtc().toIso8601String(),
              },
          ],
        },
      );
    } on Object catch (e) {
      // No network or a server error: they wait for the next try.
      debugPrint('[LiveTrip] check-ins not sent: $e');
      return const _SendOutcome({}, {});
    }

    final byOperation = {for (final u in batch) u.operationId: u};
    final results = <String, PingSendResult>{};
    final ended = <String>{};
    final answers = response['results'];
    if (answers is List) {
      for (final answer in answers) {
        if (answer is! Map) continue;
        final unsent = byOperation[answer['operationId']];
        if (unsent == null) continue;
        final pingId = unsent.ping.pingId;
        final status = answer['status'];
        final message =
            answer['message'] is String ? answer['message'] as String : null;
        if (status == 'success' || status == 'duplicate') {
          _unsent.remove(pingId);
          results[pingId] = PingSendResult.sent;
        } else if (status == 'deferred') {
          results[pingId] = PingSendResult.queued;
        } else if (isLocationRejectionMessage(message)) {
          _unsent.remove(pingId);
          if (isLocationSharingEndedMessage(message)) {
            ended.add(unsent.ping.tripId);
            results[pingId] = PingSendResult.sharingEnded;
          } else {
            results[pingId] = PingSendResult.dropped;
          }
        } else {
          // An unprefixed error (a failed read on the server): try again.
          results[pingId] = PingSendResult.queued;
        }
      }
    }
    return _SendOutcome(results, ended);
  }
}

class _Unsent {
  const _Unsent({
    required this.ping,
    required this.operationId,
    required this.idempotencyKey,
  });

  final LocationPing ping;

  /// Kept for every try, so a resend is an idempotent repeat.
  final String operationId;
  final String idempotencyKey;
}

class _SendOutcome {
  const _SendOutcome(this.results, this.endedTrips);

  final Map<String, PingSendResult> results;
  final Set<String> endedTrips;
}
