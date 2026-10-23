import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/network/data_usage_tracker.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  var clock = DateTime(2026, 9, 1, 10);
  DataUsageTracker newTracker() => DataUsageTracker(now: () => clock);

  setUp(() {
    clock = DateTime(2026, 9, 1, 10);
    SharedPreferences.setMockInitialValues({});
  });

  group('DataUsageTracker', () {
    test('drops traffic while nobody is signed in', () async {
      final tracker = newTracker();
      await tracker.initialize();

      tracker
        ..addDownloaded(100)
        ..addUploaded(50);

      expect(tracker.totalBytes, 0);
      expect(tracker.trackingSince, isNull);
    });

    test('a new account starts at zero, tracking from sign-in', () async {
      final tracker = newTracker();
      await tracker.initialize();

      tracker.setUser('user-a');

      expect(tracker.totalBytes, 0);
      expect(tracker.trackingSince, DateTime(2026, 9, 1, 10));
    });

    test('keeps each account separate on a shared device', () async {
      final tracker = newTracker();
      await tracker.initialize();

      tracker
        ..setUser('user-a')
        ..addDownloaded(1000)
        ..addUploaded(200);
      clock = DateTime(2026, 9, 5);
      tracker.setUser('user-b');

      expect(tracker.totalBytes, 0);
      expect(tracker.trackingSince, DateTime(2026, 9, 5));

      tracker
        ..addDownloaded(10)
        ..setUser('user-a');

      expect(tracker.bytesDownloaded, 1000);
      expect(tracker.bytesUploaded, 200);
      expect(tracker.trackingSince, DateTime(2026, 9, 1, 10));
    });

    test('persists per-user counters across restarts', () async {
      final first = newTracker();
      await first.initialize();
      first
        ..setUser('user-a')
        ..addDownloaded(300)
        ..addUploaded(40);
      // Let the fire-and-forget writes settle.
      await Future<void>.delayed(Duration.zero);

      clock = DateTime(2026, 10, 2);
      final second = newTracker();
      await second.initialize();
      second.setUser('user-a');

      expect(second.bytesDownloaded, 300);
      expect(second.bytesUploaded, 40);
      expect(second.trackingSince, DateTime(2026, 9, 1, 10));
    });

    test('reset zeroes counters and restarts tracking from the reset time',
        () async {
      final tracker = newTracker();
      await tracker.initialize();
      tracker
        ..setUser('user-a')
        ..addDownloaded(500);

      clock = DateTime(2026, 9, 20, 15);
      await tracker.reset();

      expect(tracker.totalBytes, 0);
      expect(tracker.trackingSince, DateTime(2026, 9, 20, 15));

      final reloaded = newTracker();
      await reloaded.initialize();
      reloaded.setUser('user-a');
      expect(reloaded.trackingSince, DateTime(2026, 9, 20, 15));
    });

    test('reset leaves other accounts untouched', () async {
      final tracker = newTracker();
      await tracker.initialize();
      tracker
        ..setUser('user-b')
        ..addDownloaded(70)
        ..setUser('user-a')
        ..addDownloaded(500);
      await tracker.reset();

      tracker.setUser('user-b');
      expect(tracker.bytesDownloaded, 70);
    });

    test('discards the legacy device-wide counter', () async {
      SharedPreferences.setMockInitialValues({
        'data_downloaded_bytes': 648600,
        'data_uploaded_bytes': 59000,
        'data_tracking_since': DateTime(2026, 7, 7).millisecondsSinceEpoch,
      });
      final tracker = newTracker();
      await tracker.initialize();
      tracker.setUser('brand-new');

      expect(tracker.totalBytes, 0);
      expect(tracker.trackingSince, DateTime(2026, 9, 1, 10));
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getInt('data_downloaded_bytes'), isNull);
      expect(prefs.getInt('data_tracking_since'), isNull);
    });

    test('does not store the raw user id in preference keys', () async {
      final tracker = newTracker();
      await tracker.initialize();
      tracker
        ..setUser('cognito-sub-123')
        ..addDownloaded(10);
      await Future<void>.delayed(Duration.zero);

      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getKeys(), isNotEmpty);
      expect(
        prefs.getKeys().any((k) => k.contains('cognito-sub-123')),
        isFalse,
      );
    });

    test('signing out clears the displayed counters', () async {
      final tracker = newTracker();
      await tracker.initialize();
      tracker
        ..setUser('user-a')
        ..addDownloaded(500)
        ..setUser(null);

      expect(tracker.totalBytes, 0);
      expect(tracker.trackingSince, isNull);
    });

    // ACC-03: a deleted account's counters leave the device.
    test('forgetCurrentUser removes only that account\'s counters', () async {
      final tracker = newTracker();
      await tracker.initialize();
      tracker
        ..setUser('user-b')
        ..addDownloaded(70)
        ..setUser('user-a')
        ..addDownloaded(500)
        ..addUploaded(20);
      await Future<void>.delayed(Duration.zero);
      final prefs = await SharedPreferences.getInstance();
      final before = prefs.getKeys().length;

      await tracker.forgetCurrentUser();
      tracker.addDownloaded(5);
      await Future<void>.delayed(Duration.zero);

      expect(tracker.userId, isNull);
      expect(tracker.totalBytes, 0);
      expect(prefs.getKeys(), hasLength(before - 3));
      tracker.setUser('user-b');
      expect(tracker.bytesDownloaded, 70);
      tracker.setUser('user-a');
      expect(tracker.totalBytes, 0, reason: 'starts over like a new account');
    });
  });

  // ACC-03: an account deleted after it signed out.
  test('forgetStoredUser removes only that account\'s stored counters',
      () async {
    final tracker = newTracker();
    await tracker.initialize();
    tracker
      ..setUser('user-a')
      ..addDownloaded(500)
      ..setUser('user-b')
      ..addDownloaded(70);
    await Future<void>.delayed(Duration.zero);

    await DataUsageTracker.forgetStoredUser('user-a');

    expect(tracker.bytesDownloaded, 70, reason: 'user-b is still counted');
    tracker.setUser('user-a');
    expect(tracker.totalBytes, 0);
  });

  group('byte estimates', () {
    test('estimateBodyBytes handles strings, bytes, JSON and null', () {
      expect(estimateBodyBytes(null), 0);
      expect(estimateBodyBytes('héllo'), 6);
      expect(estimateBodyBytes(<int>[1, 2, 3]), 3);
      expect(estimateBodyBytes({'a': 1}), '{"a":1}'.length);
    });

    test('estimateHeaderBytes counts name, value and CRLF per value', () {
      expect(
        estimateHeaderBytes({
          'a': 'bc',
          'x': ['1', '2'],
        }),
        'a: bc\r\n'.length + 'x: 1\r\n'.length * 2,
      );
    });

    test('estimateRequestBytes includes request line, headers and body', () {
      final options = RequestOptions(
        path: '/sync',
        baseUrl: 'https://api.test',
        method: 'POST',
        queryParameters: {'since': '5'},
        headers: {'Authorization': 'Bearer token'},
        data: {'k': 'v'},
      );
      expect(
        estimateRequestBytes(options),
        'POST /sync?since=5 HTTP/1.1\r\n'.length +
            'Host: api.test\r\n'.length +
            'Authorization: Bearer token\r\n'.length +
            '{"k":"v"}'.length,
      );
    });
  });
}
