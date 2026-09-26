import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache_web.dart';
import 'package:wereda_connect/core/models/user.dart';

/// F: the web cache (platform pair of the Isar cache) must carry the Fayda
/// verification status so the profile badge works on web too.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  final Map<String, String> store = {};
  const phone = '+251911234567';
  const tokens = AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i');

  setUpAll(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (MethodCall call) async {
        final args = (call.arguments as Map?)?.cast<dynamic, dynamic>();
        switch (call.method) {
          case 'read':
            return store[args!['key'] as String];
          case 'write':
            store[args!['key'] as String] = args['value'] as String;
            return null;
          case 'delete':
            store.remove(args!['key'] as String);
            return null;
          default:
            return null;
        }
      },
    );
  });

  setUp(store.clear);

  Future<void> cacheLogin(OfflineAuthCache cache, {bool? faydaVerified}) {
    return cache.cacheCredentials(
      phoneNumber: phone,
      password: 'Secret123',
      userId: 'user-1',
      role: UserRole.buyer,
      name: 'Abebe',
      woreda: 'Adama',
      tokens: tokens,
      faydaVerified: faydaVerified,
      faydaVerifiedAt: faydaVerified == true ? DateTime.utc(2026, 9, 2) : null,
    );
  }

  test('login stores the Fayda status and getCachedUser reads it', () async {
    final cache = OfflineAuthCache();
    await cacheLogin(cache, faydaVerified: true);

    final user = await cache.getCachedUser(phone);

    expect(user!.faydaVerified, isTrue);
    expect(user.isVerified, isTrue);
    expect(user.faydaVerifiedAt, DateTime.utc(2026, 9, 2));
  });

  test('a response without the field keeps the cached status', () async {
    final cache = OfflineAuthCache();
    await cacheLogin(cache, faydaVerified: true);
    await cacheLogin(cache);

    expect((await cache.getCachedUser(phone))!.faydaVerified, isTrue);
  });

  test('updateVerification marks the user verified after a Fayda link',
      () async {
    final cache = OfflineAuthCache();
    await cacheLogin(cache, faydaVerified: false);
    expect((await cache.getCachedUser(phone))!.isVerified, isFalse);

    await cache.updateVerification(
      userId: 'user-1',
      phoneNumber: phone,
      faydaVerified: true,
      faydaVerifiedAt: DateTime.utc(2026, 9, 25),
    );

    final user = await cache.getCachedUser(phone);
    expect(user!.faydaVerified, isTrue);
    expect(user.isVerified, isTrue);
    expect(user.faydaVerifiedAt, DateTime.utc(2026, 9, 25));
  });

  test('updateVerification ignores a different user', () async {
    final cache = OfflineAuthCache();
    await cacheLogin(cache, faydaVerified: false);

    await cache.updateVerification(
      userId: 'someone-else',
      phoneNumber: phone,
      faydaVerified: true,
    );

    expect((await cache.getCachedUser(phone))!.faydaVerified, isFalse);
  });
}
