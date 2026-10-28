import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/location_ping.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/live_trip/data/web_location_ping_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/base_location_ping_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// LT-08: web has no queue, so the check-in goes to `/sync` at once and the
/// answer is settled there.
void main() {
  late _MockApiClient api;
  late WebLocationPingRepository repository;
  late List<Map<String, dynamic>> sent;
  late List<Object> replies;
  const trucker = 'trucker-1';

  setUp(() {
    api = _MockApiClient();
    repository = WebLocationPingRepository(api: api);
    sent = [];
    replies = [];
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      final body = Map<String, dynamic>.from(
        invocation.namedArguments[#data] as Map,
      );
      sent.add(body);
      final reply = replies.isEmpty ? 'success' : replies.removeAt(0);
      if (reply is Exception) throw reply;
      final ops = (body['operations'] as List).cast<Map<String, dynamic>>();
      return {
        'results': [
          for (final op in ops)
            {
              'operationId': op['operationId'],
              if (reply is Map) ...reply else 'status': reply,
            },
        ],
      };
    });
  });

  final base = DateTime.utc(2026, 10, 28, 6);

  LocationPing fix(DateTime at) => LocationPing()
    ..pingId = locationPingId('trip-1', at)
    ..tripId = 'trip-1'
    ..truckerId = trucker
    ..kind = PingKind.fix
    ..source = CheckInSource.manual
    ..capturedAt = at
    ..latitude = 8.75
    ..longitude = 38.98
    ..accuracyM = 30;

  test('a ping is posted at once with the contract shape', () async {
    final ping = fix(base);

    final result = await repository.record(ping, userId: trucker);

    expect(result, PingSendResult.sent);
    final op = (sent.single['operations'] as List).single as Map;
    expect(op['entityType'], 'location_ping');
    expect(op['entityId'], ping.pingId);
    expect(op['action'], 'create');
    expect(op['idempotencyKey'], locationPingIdempotencyKey(trucker, ping));
    expect(op['data'], locationPingData(ping));
    expect(await repository.unsentCount('trip-1', userId: trucker), 0);
  });

  test('deferred waits and goes again with the same key', () async {
    replies.add({
      'status': 'deferred',
      'message': 'LOCATION_NOT_ACTIVE: wait.',
    });
    expect(
      await repository.record(fix(base), userId: trucker),
      PingSendResult.queued,
    );
    expect(await repository.unsentCount('trip-1', userId: trucker), 1);

    expect(await repository.flush(userId: trucker), isEmpty);

    final first = (sent[0]['operations'] as List).single as Map;
    final second = (sent[1]['operations'] as List).single as Map;
    expect(second['operationId'], first['operationId']);
    expect(second['idempotencyKey'], first['idempotencyKey']);
    expect(await repository.unsentCount('trip-1', userId: trucker), 0);
  });

  test('no network: it waits for the next try', () async {
    replies.add(const NetworkException());
    expect(
      await repository.record(fix(base), userId: trucker),
      PingSendResult.queued,
    );
    expect(await repository.unsentCount('trip-1', userId: trucker), 1);
  });

  test('LOCATION_SHARING_ENDED ends sharing for the trip', () async {
    replies.add({
      'status': 'error',
      'message': 'LOCATION_SHARING_ENDED: done.',
    });
    expect(
      await repository.record(fix(base), userId: trucker),
      PingSendResult.sharingEnded,
    );

    replies
      ..add({'status': 'deferred', 'message': 'LOCATION_NOT_ACTIVE: wait.'})
      ..add({'status': 'error', 'message': 'LOCATION_SHARING_ENDED: done.'});
    await repository.record(
      fix(base.add(const Duration(hours: 1))),
      userId: trucker,
    );
    expect(await repository.flush(userId: trucker), {'trip-1'});
  });

  test('other LOCATION_* codes drop the ping; an unprefixed error waits',
      () async {
    replies.add({'status': 'error', 'message': 'LOCATION_STALE: old.'});
    expect(
      await repository.record(fix(base), userId: trucker),
      PingSendResult.dropped,
    );
    expect(await repository.unsentCount('trip-1', userId: trucker), 0);

    replies.add({'status': 'error', 'message': 'Read failed.'});
    expect(
      await repository.record(
        fix(base.add(const Duration(minutes: 1))),
        userId: trucker,
      ),
      PingSendResult.queued,
    );
    expect(await repository.unsentCount('trip-1', userId: trucker), 1);
  });

  test('waiting pings are coalesced like the mobile queue', () async {
    for (var i = 0; i < 25; i++) {
      replies.add(const NetworkException());
      await repository.record(
        fix(base.add(Duration(minutes: 2 * i))),
        userId: trucker,
      );
    }
    expect(await repository.unsentCount('trip-1', userId: trucker), 1);
    expect(await repository.pingsFor('trip-1', userId: trucker), hasLength(1));
  });

  test('the log answers the latest fix and drops a trip on request', () async {
    await repository.record(fix(base), userId: trucker);
    expect(
      (await repository.latestFix('trip-1', userId: trucker))?.capturedAt,
      base,
    );
    replies.add(const NetworkException());
    await repository.record(
      fix(base.add(const Duration(hours: 1))),
      userId: trucker,
    );

    expect(
      await repository.dropUnsent(
        'trip-1',
        userId: trucker,
        capturedAfter: base.add(const Duration(hours: 1)),
      ),
      0,
    );
    expect(await repository.dropUnsent('trip-1', userId: trucker), 1);
    expect(await repository.pingsFor('trip-1', userId: trucker), hasLength(1));
  });
}
