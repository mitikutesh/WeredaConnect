import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/notifications/data/notification_api.dart';
import 'package:wereda_connect/features/notifications/data/web_notification_repository.dart';

class MockApiClient extends Mock implements ApiClient {}

Map<String, dynamic> _row(
  String id, {
  String createdAt = '2026-10-26T08:15:02Z',
}) =>
    {
      'notificationId': id,
      'type': 'order_request',
      'entity': 'order',
      'entityId': 'ord-1',
      'orderId': 'ord-1',
      'params': {
        'buyerName': 'Abebe T.',
        'quantityKg': '500',
        'amountEtb': '42500.00',
      },
      'createdAt': createdAt,
    };

/// ESC-21: the inbox pull (notifications-api.md section 1).
void main() {
  late MockApiClient api;

  setUp(() {
    api = MockApiClient();
    SharedPreferences.setMockInitialValues({});
  });

  void answer(List<Map<String, dynamic>> pages) {
    var call = 0;
    when(
      () => api.get<Map<String, dynamic>>(
        '/notifications',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer((_) async => pages[call++]);
  }

  List<Map<String, dynamic>> sentQueries() => verify(
        () => api.get<Map<String, dynamic>>(
          '/notifications',
          queryParameters: captureAny(named: 'queryParameters'),
        ),
      ).captured.cast<Map<String, dynamic>>();

  group('NotificationApi.fetch', () {
    test('a first pull reads the newest page and stores its cursor', () async {
      answer([
        {
          'notifications': [_row('a')],
          'nextToken': 'more',
          'cursor': 'c1',
        },
      ]);

      final result = await NotificationApi(api).fetch();

      expect(result.rows.map((r) => r['notificationId']), ['a']);
      expect(result.cursor, 'c1');
      final queries = sentQueries();
      expect(queries, hasLength(1));
      expect(queries.single.containsKey('since'), isFalse);
      expect(queries.single['limit'], '50');
    });

    test(
        'an incremental pull follows nextToken with the same since and '
        'keeps the first page\'s cursor', () async {
      answer([
        {
          'notifications': [_row('a')],
          'nextToken': 't1',
          'cursor': 'c2',
        },
        {
          'notifications': [_row('b')],
          'cursor': 'c3',
        },
      ]);

      final result = await NotificationApi(api).fetch(since: 'c1');

      expect(result.rows.map((r) => r['notificationId']), ['a', 'b']);
      expect(result.cursor, 'c2');
      final queries = sentQueries();
      expect(queries.map((q) => q['since']), ['c1', 'c1']);
      expect(queries.last['nextToken'], 't1');
    });

    test('a pull that stops with pages left still moves the cursor on',
        () async {
      answer([
        for (var i = 0; i < NotificationApi.maxPages; i++)
          {
            'notifications': [_row('n$i')],
            'nextToken': 't$i',
            'cursor': 'c$i',
          },
      ]);

      final result = await NotificationApi(api).fetch(since: 'old');

      expect(result.rows, hasLength(NotificationApi.maxPages));
      // The first page's: re-reading from 'old' would fetch the same newest
      // pages on every pull.
      expect(result.cursor, 'c0');
    });
  });

  group('parseNotification', () {
    test('reads a row and keeps its params as plain strings', () {
      final entry = parseNotification(_row('a'), 'user-1')!;
      expect(entry.notificationId, 'a');
      expect(entry.userId, 'user-1');
      expect(entry.type, 'order_request');
      expect(entry.orderId, 'ord-1');
      expect(entry.createdAt, DateTime.utc(2026, 10, 26, 8, 15, 2));
      expect(notificationParams(entry)['quantityKg'], '500');
    });

    test('drops a row without an id, type or time', () {
      for (final key in ['notificationId', 'type', 'createdAt']) {
        expect(
          parseNotification(_row('a')..remove(key), 'user-1'),
          isNull,
          reason: key,
        );
      }
    });

    test('an escrow row without an order opens nothing', () {
      final entry = parseNotification(_row('a')..remove('orderId'), 'user-1')!;
      expect(entry.orderId, isNull);
    });
  });

  group('WebNotificationRepository', () {
    test('drops ids it already has and counts the unseen', () async {
      answer([
        {
          'notifications': [
            _row('a'),
            _row('b', createdAt: '2026-10-26T09:00:00Z'),
          ],
          'cursor': 'c1',
        },
        {
          'notifications': [_row('b', createdAt: '2026-10-26T09:00:00Z')],
          'cursor': 'c2',
        },
      ]);
      final repo = WebNotificationRepository(api: api);
      final changes = <void>[];
      final sub = repo.changes.listen(changes.add);

      await repo.pull('user-1');
      await repo.pull('user-1', force: true);
      await Future<void>.delayed(Duration.zero);

      final recent = await repo.recent('user-1');
      expect(recent.map((n) => n.notificationId), ['b', 'a']);
      expect(await repo.unreadCount('user-1'), 2);
      // The second pull went from the first one's cursor and added nothing.
      expect(sentQueries().last['since'], 'c1');
      expect(changes, hasLength(1));
      await sub.cancel();
    });

    test('marking read keeps a marker per user', () async {
      answer([
        {
          'notifications': [_row('a')],
          'cursor': 'c1',
        },
      ]);
      final repo = WebNotificationRepository(api: api);
      await repo.pull('user-1');

      await repo.markAllRead('user-1');

      expect(await repo.unreadCount('user-1'), 0);
      expect((await repo.recent('user-1')).single.read, isTrue);
      final prefs = await SharedPreferences.getInstance();
      expect(
        prefs.getString(WebNotificationRepository.readMarkerKey('user-1')),
        '2026-10-26T08:15:02.000Z',
      );
      expect(await repo.unreadCount('user-2'), 0);
    });

    test('a second pull within the interval waits unless forced', () async {
      answer([
        {
          'notifications': [_row('a')],
          'cursor': 'c1',
        },
        {
          'notifications': [_row('b')],
          'cursor': 'c2',
        },
      ]);
      final repo = WebNotificationRepository(api: api);

      await repo.pull('user-1');
      await repo.pull('user-1');
      expect(sentQueries(), hasLength(1));

      await repo.pull('user-1', force: true);
      expect(await repo.unreadCount('user-1'), 2);
    });

    test(
        'a row that arrives after the bell was opened is unseen, whatever '
        'its time', () async {
      answer([
        {
          'notifications': [_row('new', createdAt: '2026-10-26T10:00:00Z')],
          'cursor': 'c1',
        },
        {
          // Written late (a retried stream record), for an earlier change.
          'notifications': [_row('late', createdAt: '2026-10-26T09:00:00Z')],
          'cursor': 'c2',
        },
      ]);
      final repo = WebNotificationRepository(api: api);
      await repo.pull('user-1');
      await repo.markAllRead('user-1');

      await repo.pull('user-1', force: true);

      expect(await repo.unreadCount('user-1'), 1);
      final late = (await repo.recent('user-1'))
          .firstWhere((n) => n.notificationId == 'late');
      expect(late.read, isFalse);
      await repo.markAllRead('user-1');
      expect(await repo.unreadCount('user-1'), 0);
    });

    test('a failed pull does not hold the next one back', () async {
      var calls = 0;
      when(
        () => api.get<Map<String, dynamic>>(
          '/notifications',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((_) async {
        if (calls++ == 0) throw Exception('offline');
        return {
          'notifications': [_row('a')],
          'cursor': 'c1',
        };
      });
      final repo = WebNotificationRepository(api: api);

      await expectLater(repo.pull('user-1'), throwsException);
      await repo.pull('user-1');

      expect(await repo.unreadCount('user-1'), 1);
    });

    test('no user, no pull', () async {
      await WebNotificationRepository(api: api).pull('');
      verifyNever(
        () => api.get<Map<String, dynamic>>(
          any(),
          queryParameters: any(named: 'queryParameters'),
        ),
      );
    });
  });
}
