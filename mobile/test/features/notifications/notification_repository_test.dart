import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/database/schemas/app_notification.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/notifications/data/notification_repository.dart';

class MockApiClient extends Mock implements ApiClient {}

Map<String, dynamic> _row(String id, String createdAt) => {
      'notificationId': id,
      'type': 'order_confirmed',
      'entity': 'order',
      'entityId': 'ord-1',
      'orderId': 'ord-1',
      'params': <String, String>{},
      'createdAt': createdAt,
    };

/// ESC-21: the mobile inbox keeps its entries in Isar (offline) and pulls
/// only what is new.
void main() {
  late Isar isar;
  late Directory tempDir;
  late MockApiClient api;
  late NotificationRepository repo;
  late List<Map<String, dynamic>?> queries;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    SharedPreferences.setMockInitialValues({});
    tempDir = Directory.systemTemp.createTempSync('notification_repo_test_');
    isar = await Isar.open(
      [AppNotificationSchema],
      directory: tempDir.path,
      name: 'notification_repo_${DateTime.now().microsecondsSinceEpoch}',
    );
    api = MockApiClient();
    repo = NotificationRepository(isar: isar, api: api);
    queries = [];
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  void answer(List<Map<String, dynamic>> pages) {
    var call = 0;
    when(
      () => api.get<Map<String, dynamic>>(
        '/notifications',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer((invocation) async {
      queries.add(
        invocation.namedArguments[#queryParameters] as Map<String, dynamic>?,
      );
      return pages[call++];
    });
  }

  test('pulls, drops ids it has, keeps the read state, stores the cursor',
      () async {
    answer([
      {
        'notifications': [_row('a', '2026-10-26T08:00:00Z')],
        'cursor': 'c1',
      },
      {
        'notifications': [
          _row('a', '2026-10-26T08:00:00Z'),
          _row('b', '2026-10-26T09:00:00Z'),
        ],
        'cursor': 'c2',
      },
    ]);

    await repo.pull('user-1');
    await repo.markAllRead('user-1');
    // Within the pull interval only a push forces the next pull.
    await repo.pull('user-1');
    expect(queries, hasLength(1));
    await repo.pull('user-1', force: true);

    expect(queries.first!.containsKey('since'), isFalse);
    expect(queries.last!['since'], 'c1');
    final recent = await repo.recent('user-1');
    expect(recent.map((n) => n.notificationId), ['b', 'a']);
    expect(recent.map((n) => n.read), [false, true]);
    expect(await repo.unreadCount('user-1'), 1);
    final prefs = await SharedPreferences.getInstance();
    expect(prefs.getString(NotificationRepository.cursorKey('user-1')), 'c2');
  });

  test('two users of one device keep their own entries with the same id',
      () async {
    answer([
      {
        'notifications': [_row('same', '2026-10-26T08:00:00Z')],
        'cursor': 'c1',
      },
      {
        'notifications': [_row('same', '2026-10-26T08:00:00Z')],
        'cursor': 'c1',
      },
    ]);

    await repo.pull('buyer-1');
    await repo.pull('farmer-1');
    await repo.markAllRead('buyer-1');

    expect(await repo.unreadCount('buyer-1'), 0);
    expect(await repo.unreadCount('farmer-1'), 1);
    expect(await isar.appNotifications.count(), 2);
  });

  test('with no entries on the device the cursor is ignored', () async {
    SharedPreferences.setMockInitialValues({
      NotificationRepository.cursorKey('user-1'): 'stale',
    });
    answer([
      {
        'notifications': [_row('a', '2026-10-26T08:00:00Z')],
        'cursor': 'c1',
      },
    ]);

    await repo.pull('user-1');

    expect(queries.single!.containsKey('since'), isFalse);
    expect(await repo.unreadCount('user-1'), 1);
  });

  test('keeps the newest entries only', () async {
    final rows = [
      for (var i = 0; i < NotificationRepository.maxKept + 5; i++)
        _row(
          'n$i',
          DateTime.utc(2026, 10).add(Duration(minutes: i)).toIso8601String(),
        ),
    ];
    answer([
      {'notifications': rows, 'cursor': 'c1'},
    ]);

    await repo.pull('user-1');

    expect(await isar.appNotifications.count(), NotificationRepository.maxKept);
    final oldest = (await repo.recent('user-1', limit: 1000)).last;
    expect(oldest.notificationId, 'n5');
  });

  test('an offline pull throws and keeps what the device has', () async {
    when(
      () => api.get<Map<String, dynamic>>(
        '/notifications',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenThrow(Exception('offline'));

    await expectLater(repo.pull('user-1'), throwsException);
    expect(await repo.recent('user-1'), isEmpty);
    // The failure does not throttle the retry.
    await expectLater(repo.pull('user-1'), throwsException);
    verify(
      () => api.get<Map<String, dynamic>>(
        '/notifications',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).called(2);
  });
}
