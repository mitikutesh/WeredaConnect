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
}
