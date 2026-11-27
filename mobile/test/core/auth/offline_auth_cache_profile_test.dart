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

/// SIM-56: a profile edit lands on the phone's copy of the signed-in user
/// (platform pair: offline_auth_cache_web_profile_test.dart).
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
    dir = Directory.systemTemp.createTempSync('offline_cache_profile_test_');
    PathProviderPlatform.instance = _FakePathProvider(dir.path);
    await IsarService.instance.initialize();
  });

  tearDownAll(() async {
    await IsarService.instance.close();
    if (dir.existsSync()) dir.deleteSync(recursive: true);
  });

  test('updateProfile changes the name, woreda and company name only',
      () async {
    final cache = OfflineAuthCache();
    await cache.saveUserToIsar(
      userId: 'buyer-1',
      phoneNumber: _phone,
      name: 'Almaz',
      displayName: 'Almaz',
      role: UserRole.buyer,
      woreda: 'Adama',
      buyerType: 'company',
      companyName: 'Adama Traders',
      faydaVerified: true,
    );

    await cache.updateProfile(
      userId: 'buyer-1',
      phoneNumber: _phone,
      displayName: 'Almaz T.',
      woreda: 'Meki',
      companyName: 'Meki Traders',
    );

    final user = (await cache.getCachedUser(_phone))!;
    expect(user.name, 'Almaz T.');
    expect(user.displayName, 'Almaz T.');
    expect(user.woreda, 'Meki');
    expect(user.companyName, 'Meki Traders');
    expect(user.buyerType, 'company');
    expect(user.role, UserRole.buyer);
    expect(user.faydaVerified, isTrue);
  });

  test('updateProfile for a user not cached here writes nothing', () async {
    final cache = OfflineAuthCache();

    await cache.updateProfile(
      userId: 'nobody',
      phoneNumber: '+251911000009',
      displayName: 'X',
      woreda: 'Y',
    );

    expect(await cache.getCachedUser('+251911000009'), isNull);
  });
}
