import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache_web.dart';
import 'package:wereda_connect/core/models/user.dart';

/// SIM-56: the web cache (platform pair of the Isar cache) takes a profile
/// edit and gives a company buyer's company name back to the edit screen.
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

  Future<void> cacheLogin(OfflineAuthCache cache) => cache.cacheCredentials(
        phoneNumber: phone,
        password: 'Secret123',
        userId: 'buyer-1',
        role: UserRole.buyer,
        name: 'Almaz',
        displayName: 'Almaz',
        woreda: 'Adama',
        tokens: tokens,
        buyerType: 'company',
        companyName: 'Adama Traders',
        faydaVerified: true,
      );

  test('getCachedUser returns the buyer type and company name', () async {
    final cache = OfflineAuthCache();
    await cacheLogin(cache);

    final user = (await cache.getCachedUser(phone))!;
    expect(user.buyerType, 'company');
    expect(user.companyName, 'Adama Traders');
  });

  test('updateProfile changes the name, woreda and company name only',
      () async {
    final cache = OfflineAuthCache();
    await cacheLogin(cache);

    await cache.updateProfile(
      userId: 'buyer-1',
      phoneNumber: phone,
      displayName: 'Almaz T.',
      woreda: 'Meki',
      companyName: 'Meki Traders',
    );

    final user = (await cache.getCachedUser(phone))!;
    expect(user.name, 'Almaz T.');
    expect(user.displayName, 'Almaz T.');
    expect(user.woreda, 'Meki');
    expect(user.companyName, 'Meki Traders');
    expect(user.buyerType, 'company');
    expect(user.role, UserRole.buyer);
    expect(user.faydaVerified, isTrue);
    // The session is untouched.
    expect(await cache.getCachedTokens(phone), isNotNull);
  });

  test("another account's cached copy is left alone", () async {
    final cache = OfflineAuthCache();
    await cacheLogin(cache);

    await cache.updateProfile(
      userId: 'buyer-2',
      phoneNumber: phone,
      displayName: 'Someone else',
      woreda: 'Meki',
    );

    expect((await cache.getCachedUser(phone))!.displayName, 'Almaz');
  });
}
