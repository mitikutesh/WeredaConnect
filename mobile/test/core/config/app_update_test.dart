import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/config/app_update.dart';

/// A browser that records what [reloadLatestApp] asked of it.
class _FakeHost implements AppReloadHost {
  _FakeHost({
    this.online = true,
    this.workerUpdated = true,
    this.codeFetched = true,
  });

  @override
  final bool online;
  final bool workerUpdated;
  final bool codeFetched;
  final calls = <String>[];

  @override
  Future<bool> updateServiceWorker() async {
    calls.add('worker');
    return workerUpdated;
  }

  @override
  Future<bool> refetchAppCode() async {
    calls.add('code');
    return codeFetched;
  }

  @override
  void reload() => calls.add('reload');
}

/// The application id the Android build declares.
String _androidApplicationId() {
  for (final name in ['build.gradle', 'build.gradle.kts']) {
    final file = File('android/app/$name');
    if (!file.existsSync()) continue;
    final match = RegExp(r'''applicationId\s*=?\s*["']([^"']+)["']''')
        .firstMatch(file.readAsStringSync());
    if (match != null) return match.group(1)!;
  }
  throw StateError('No applicationId in android/app/build.gradle(.kts)');
}

/// FUP-75: how the web banner's "Reload page" gets the latest app, and
/// where the phone's "Update" goes.
void main() {
  group('reloadLatestApp', () {
    test('offline: nothing is asked of the browser', () async {
      final host = _FakeHost(online: false);

      expect(await reloadLatestApp(host), isFalse);
      expect(host.calls, isEmpty);
    });

    test('the worker first, then the code, then the reload', () async {
      final host = _FakeHost();

      expect(await reloadLatestApp(host), isTrue);
      expect(host.calls, ['worker', 'code', 'reload']);
    });

    test('a worker update that failed or did not finish: no reload', () async {
      final host = _FakeHost(workerUpdated: false);

      expect(await reloadLatestApp(host), isFalse);
      expect(host.calls, ['worker']);
    });

    test('the code could not be fetched: no reload', () async {
      final host = _FakeHost(codeFetched: false);

      expect(await reloadLatestApp(host), isFalse);
      expect(host.calls, ['worker', 'code']);
    });
  });

  test('off the web there is no page to reload', () async {
    expect(await reloadLatestWebApp(), isFalse);
  });

  test('the store page is the Android build\'s own app on Play', () {
    final id = _androidApplicationId();

    expect(AppStore.androidPackageId, id);
    expect(
      AppStore.playStorePage.toString(),
      'https://play.google.com/store/apps/details?id=$id',
    );
  });
}
