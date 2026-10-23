import 'dart:convert';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache_web.dart';
import 'package:wereda_connect/core/models/user.dart';

/// F-34: the web offline cache must treat an unrecognized/missing role as a
/// cache miss (return null) rather than silently defaulting to farmer, so
/// callers never infer a wrong role from a stale/corrupt cache entry.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  final Map<String, String> store = {};

  setUpAll(() {
    // Back FlutterSecureStorage with an in-memory map (same channel the app's
    // web cache talks to via localStorage on web).
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
          case 'deleteAll':
            store.clear();
            return null;
          default:
            return null;
        }
      },
    );
  });

  setUp(store.clear);

  const phone = '+251911234567';
  String userKey(String p) => 'cached_user_$p';
  String tokensKey(String p) => 'cached_tokens_$p';

  Map<String, dynamic> cachedUser(String role) => {
        'userId': 'user-001',
        'phoneNumber': phone,
        'name': 'Abebe',
        'role': role,
        'woreda': 'Jimma',
      };

  test('returns the user when the cached role is recognized', () async {
    store[userKey(phone)] = jsonEncode(cachedUser('trucker'));

    final user = await OfflineAuthCache().getCachedUser(phone);

    expect(user, isNotNull);
    expect(user!.role, UserRole.trucker);
  });

  test('returns null when the cached role is unrecognized (F-34)', () async {
    store[userKey(phone)] = jsonEncode(cachedUser('wizard'));

    final user = await OfflineAuthCache().getCachedUser(phone);

    expect(user, isNull);
  });

  test('returns null when there is no cached user', () async {
    final user = await OfflineAuthCache().getCachedUser(phone);

    expect(user, isNull);
  });

  test('returns the buyer when the cached role is buyer', () async {
    // 3-role completeness: a recognized buyer role must round-trip, since F-34's
    // risk is mis-scoping a buyer/trucker as a farmer.
    store[userKey(phone)] = jsonEncode(cachedUser('buyer'));

    final user = await OfflineAuthCache().getCachedUser(phone);

    expect(user, isNotNull);
    expect(user!.role, UserRole.buyer);
  });

  // M1: a corrupt/legacy localStorage blob must fail-closed to a cache miss
  // (return null), never throw. `jsonDecode(...) as Map` and
  // AuthTokens.fromJson throw a TypeError (an Error, not an Exception) on a
  // shape-mismatched blob; the caller (auth_bloc / loginOffline) only catches
  // `on Exception`, so an escaping TypeError hangs the web app on splash.

  test('getCachedUser returns null on a non-map JSON blob (M1)', () async {
    store[userKey(phone)] = jsonEncode(<dynamic>[1, 2, 3]);

    final user = await OfflineAuthCache().getCachedUser(phone);

    expect(user, isNull);
  });

  test('getCachedUser returns null on a non-JSON blob (M1)', () async {
    store[userKey(phone)] = 'not-json-at-all';

    final user = await OfflineAuthCache().getCachedUser(phone);

    expect(user, isNull);
  });

  test('getCachedTokens returns null on a non-map JSON blob (M1)', () async {
    store[tokensKey(phone)] = jsonEncode(<dynamic>['corrupt']);

    final tokens = await OfflineAuthCache().getCachedTokens(phone);

    expect(tokens, isNull);
  });

  test(
    'getCachedTokens returns null when required token fields are missing (M1)',
    () async {
      store[tokensKey(phone)] = jsonEncode(<String, dynamic>{'foo': 'bar'});

      final tokens = await OfflineAuthCache().getCachedTokens(phone);

      expect(tokens, isNull);
    },
  );

  // ACC-03: a deleted account leaves nothing in this browser; another
  // account cached in the same browser is untouched.
  test('forgetAccount removes only that phone\'s cache entries', () async {
    const other = '+251911000002';
    for (final p in [phone, other]) {
      store[userKey(p)] = jsonEncode(cachedUser('farmer'));
      store[tokensKey(p)] = '{}';
      store['cached_at_$p'] = '2026-10-23T00:00:00.000Z';
    }

    await OfflineAuthCache().forgetAccount(phone);

    expect(store.keys.where((k) => k.endsWith(phone)), isEmpty);
    expect(store.keys.where((k) => k.endsWith(other)), hasLength(3));
  });
}
