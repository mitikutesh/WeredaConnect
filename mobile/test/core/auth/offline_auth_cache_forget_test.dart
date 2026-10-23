import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:path_provider_platform_interface/path_provider_platform_interface.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';

import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/database/isar_service.dart';
import 'package:wereda_connect/core/models/user.dart';

class _FakePathProvider extends Fake
    with MockPlatformInterfaceMixin
    implements PathProviderPlatform {
  _FakePathProvider(this.path);

  final String path;

  @override
  Future<String?> getApplicationDocumentsPath() async => path;
}

const _phone = '+251911234567';
const _other = '+251911000002';

/// ACC-03: a deleted account's offline cache goes, including the
/// offline-login counter keyed by its phone; another account cached on the
/// same phone keeps everything.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  final store = <String, String>{};
  late Directory dir;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (MethodCall call) async {
        final args = (call.arguments as Map?)?.cast<dynamic, dynamic>();
        switch (call.method) {
          case 'write':
            store[args!['key'] as String] = args['value'] as String;
            return null;
          case 'read':
            return store[args!['key'] as String];
          case 'delete':
            store.remove(args!['key'] as String);
            return null;
          default:
            return null;
        }
      },
    );
    dir = Directory.systemTemp.createTempSync('offline_cache_forget_test_');
    PathProviderPlatform.instance = _FakePathProvider(dir.path);
    await IsarService.instance.initialize();
  });

  tearDownAll(() async {
    await IsarService.instance.close();
    if (dir.existsSync()) dir.deleteSync(recursive: true);
  });

  Future<void> cache(String phone, String userId) async {
    await OfflineAuthCache().saveUserToIsar(
      userId: userId,
      phoneNumber: phone,
      name: 'Name',
      role: UserRole.trucker,
      woreda: 'Adama',
    );
    store['password_hash_$phone'] = 'hash';
    store['salt_$phone'] = 'salt';
    store['tokens_$phone'] = '{}';
    store['session_timestamp_$phone'] = '2026-10-23T00:00:00.000Z';
    store['rate_limit_$phone'] = '["2026-10-23T00:00:00.000Z"]';
  }

  test('forgetAccount removes credentials, profile and failure counter',
      () async {
    await cache(_phone, 'user-1');
    await cache(_other, 'user-2');

    await OfflineAuthCache().forgetAccount(_phone);

    expect(store.keys.where((k) => k.endsWith(_phone)), isEmpty);
    expect(store.keys.where((k) => k.endsWith(_other)), hasLength(5));
    expect(await OfflineAuthCache().getCachedUser(_phone), isNull);
    expect(await OfflineAuthCache().getCachedUser(_other), isNotNull);
  });
}
