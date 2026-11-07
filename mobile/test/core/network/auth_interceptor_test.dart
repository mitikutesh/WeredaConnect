import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';

import 'package:wereda_connect/core/auth/session_signals.dart';
import 'package:wereda_connect/core/network/auth_interceptor.dart';

/// Adapter that replies 401 unless the request carries [validBearer].
///
/// Mirrors a server with an expired token: every request 401s until the
/// interceptor refreshes and retries with the new bearer.
class TokenAwareAdapter implements HttpClientAdapter {
  TokenAwareAdapter({required this.validBearer});

  final String validBearer;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final auth = options.headers['Authorization'];
    if (auth == 'Bearer $validBearer') {
      return ResponseBody.fromString(
        '{"data":"ok"}',
        200,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
        },
      );
    }
    return ResponseBody.fromString(
      '{"message":"expired"}',
      401,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

/// Records each request's bearer; replies 401 to the bearers in [rejected]
/// and 200 to the rest. [onFetch] runs before the reply (e.g. another tab
/// signs in while the request is out).
class RecordingAdapter implements HttpClientAdapter {
  RecordingAdapter({this.rejected = const {}, this.onFetch});

  final Set<String> rejected;
  final Future<void> Function()? onFetch;
  final bearers = <String?>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final auth = options.headers['Authorization'] as String?;
    bearers.add(auth?.substring('Bearer '.length));
    await onFetch?.call();
    final status =
        rejected.contains(auth?.substring('Bearer '.length)) ? 401 : 200;
    return ResponseBody.fromString(
      '{"data":"ok"}',
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

/// An unsigned JWT whose `sub` is [sub]; [issued] tells two apart.
String jwt(String sub, {int issued = 0}) {
  String part(Map<String, dynamic> m) =>
      base64Url.encode(utf8.encode(jsonEncode(m))).replaceAll('=', '');
  return '${part({'alg': 'none'})}.${part({'sub': sub, 'iat': issued})}.sig';
}

/// Fake FlutterSecureStorage for testing
class FakeSecureStorage implements FlutterSecureStorage {
  final Map<String, String> _storage = {};

  @override
  Future<String?> read({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    return _storage[key];
  }

  @override
  Future<void> write({
    required String key,
    required String? value,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    if (value != null) {
      _storage[key] = value;
    }
  }

  @override
  Future<void> delete({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    _storage.remove(key);
  }

  @override
  Future<void> deleteAll({
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    _storage.clear();
  }

  @override
  Future<Map<String, String>> readAll({
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    return Map.from(_storage);
  }

  @override
  Future<bool> containsKey({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    return _storage.containsKey(key);
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  group('AuthInterceptor', () {
    late FakeSecureStorage fakeStorage;
    late Dio dio;
    late DioAdapter dioAdapter;
    late AuthInterceptor authInterceptor;
    late String? capturedRefreshToken;

    setUp(() {
      fakeStorage = FakeSecureStorage();
      dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
      dioAdapter = DioAdapter(dio: dio);

      capturedRefreshToken = null;

      authInterceptor = AuthInterceptor(
        storage: fakeStorage,
        dio: dio,
        refreshTokenCallback: (refreshToken) async {
          capturedRefreshToken = refreshToken;
          // Simulate successful refresh
          return 'new-access-token-123';
        },
      );

      dio.interceptors.add(authInterceptor);
    });

    group('onRequest - token injection', () {
      test('injects ID token into Authorization header', () async {
        // The bearer is the ID token (carries custom:role), not the access
        // token — the access token has no custom attributes.
        await fakeStorage.write(key: 'id_token', value: 'valid-token-123');

        dioAdapter.onGet(
          '/protected',
          (server) => server.reply(200, {'data': 'success'}),
          headers: {'Authorization': 'Bearer valid-token-123'},
        );

        final response = await dio.get<dynamic>('/protected');

        expect(response.statusCode, 200);
        expect(response.data['data'], 'success');
      });

      test('skips token injection when no token in storage', () async {
        // Don't write any token to storage

        dioAdapter.onGet(
          '/public',
          (server) => server.reply(200, {'data': 'public'}),
        );

        final response = await dio.get<dynamic>('/public');

        expect(response.statusCode, 200);
      });

      test('skips token injection when token is empty', () async {
        await fakeStorage.write(key: 'id_token', value: '');

        dioAdapter.onGet(
          '/public',
          (server) => server.reply(200, {'data': 'public'}),
        );

        final response = await dio.get<dynamic>('/public');

        expect(response.statusCode, 200);
      });

      test('skips token injection for /auth/refresh endpoint', () async {
        await fakeStorage.write(key: 'id_token', value: 'some-token');

        dioAdapter.onPost(
          '/auth/refresh',
          (server) => server.reply(200, {'accessToken': 'new-token'}),
          data: Matchers.any,
        );

        final response = await dio.post<dynamic>(
          '/auth/refresh',
          data: {'refreshToken': 'refresh-123'},
        );

        expect(response.statusCode, 200);
      });
    });

    group('onError - token refresh on 401', () {
      test('calls refresh callback with correct token on 401', () async {
        await fakeStorage.write(key: 'id_token', value: 'expired-token');
        await fakeStorage.write(
          key: 'refresh_token',
          value: 'valid-refresh-token',
        );

        // Mock always returns 401 (simulating retry also failing)
        dioAdapter.onGet(
          '/protected',
          (server) => server.reply(401, {'message': 'Token expired'}),
        );

        try {
          await dio.get<dynamic>('/protected');
          fail('Should have thrown');
        } on DioException {
          // Expected: retry also gets 401 from static mock
        }

        // Verify refresh callback was invoked with the correct refresh token
        expect(capturedRefreshToken, 'valid-refresh-token');
      });

      test('clears tokens and propagates error when no refresh token',
          () async {
        await fakeStorage.write(key: 'id_token', value: 'expired-token');
        // Don't write refresh_token

        dioAdapter.onGet(
          '/protected',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        await expectLater(
          dio.get<dynamic>('/protected'),
          throwsA(isA<DioException>()),
        );

        // Verify tokens were cleared
        final bearerToken = await fakeStorage.read(key: 'id_token');
        final refreshToken = await fakeStorage.read(key: 'refresh_token');
        expect(bearerToken, isNull);
        expect(refreshToken, isNull);
      });

      test('clears tokens when refresh callback returns null', () async {
        final testStorage = FakeSecureStorage();
        await testStorage.write(key: 'id_token', value: 'expired-token');
        await testStorage.write(key: 'refresh_token', value: 'invalid-refresh');

        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
        final failingInterceptor = AuthInterceptor(
          storage: testStorage,
          dio: testDio,
          refreshTokenCallback: (_) async => null, // Refresh fails
        );

        final testAdapter = DioAdapter(dio: testDio);
        testDio.interceptors.add(failingInterceptor);

        testAdapter.onGet(
          '/protected',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        await expectLater(
          testDio.get<dynamic>('/protected'),
          throwsA(isA<DioException>()),
        );

        // Verify tokens were cleared
        final bearerToken = await testStorage.read(key: 'id_token');
        final refreshToken = await testStorage.read(key: 'refresh_token');
        expect(bearerToken, isNull);
        expect(refreshToken, isNull);
      });

      test('clears tokens when refresh callback returns empty string',
          () async {
        final testStorage = FakeSecureStorage();
        await testStorage.write(key: 'id_token', value: 'expired-token');
        await testStorage.write(key: 'refresh_token', value: 'valid-refresh');

        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
        final failingInterceptor = AuthInterceptor(
          storage: testStorage,
          dio: testDio,
          refreshTokenCallback: (_) async => '', // Refresh returns empty
        );

        final testAdapter = DioAdapter(dio: testDio);
        testDio.interceptors.add(failingInterceptor);

        testAdapter.onGet(
          '/protected',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        await expectLater(
          testDio.get<dynamic>('/protected'),
          throwsA(isA<DioException>()),
        );

        // Verify tokens were cleared
        final bearerToken = await testStorage.read(key: 'id_token');
        final refreshToken = await testStorage.read(key: 'refresh_token');
        expect(bearerToken, isNull);
        expect(refreshToken, isNull);
      });

      test('preserves tokens when refresh throws (transient error)', () async {
        final testStorage = FakeSecureStorage();
        await testStorage.write(key: 'id_token', value: 'expired-token');
        await testStorage.write(key: 'refresh_token', value: 'valid-refresh');

        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
        final throwingInterceptor = AuthInterceptor(
          storage: testStorage,
          dio: testDio,
          refreshTokenCallback: (_) async {
            throw Exception('Network error during refresh');
          },
        );

        final testAdapter = DioAdapter(dio: testDio);
        testDio.interceptors.add(throwingInterceptor);

        testAdapter.onGet(
          '/protected',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        await expectLater(
          testDio.get<dynamic>('/protected'),
          throwsA(isA<DioException>()),
        );

        // Tokens preserved — transient errors (network timeout) should not
        // clear tokens because the refresh token is likely still valid.
        final bearerToken = await testStorage.read(key: 'id_token');
        final refreshToken = await testStorage.read(key: 'refresh_token');
        expect(bearerToken, 'expired-token');
        expect(refreshToken, 'valid-refresh');
      });

      test('does not attempt refresh for /auth/refresh endpoint', () async {
        await fakeStorage.write(key: 'id_token', value: 'expired-token');
        await fakeStorage.write(key: 'refresh_token', value: 'some-refresh');

        dioAdapter.onPost(
          '/auth/refresh',
          (server) => server.reply(401, {'message': 'Invalid refresh token'}),
          data: Matchers.any,
        );

        await expectLater(
          dio.post<dynamic>('/auth/refresh', data: {'token': 'bad-token'}),
          throwsA(isA<DioException>()),
        );

        // Refresh token should still be in storage (not cleared)
        final refreshToken = await fakeStorage.read(key: 'refresh_token');
        expect(refreshToken, 'some-refresh');
      });

      test('ignores non-401 errors', () async {
        await fakeStorage.write(key: 'id_token', value: 'valid-token');
        await fakeStorage.write(key: 'refresh_token', value: 'valid-refresh');

        dioAdapter.onGet(
          '/error',
          (server) => server.reply(500, {'message': 'Server error'}),
          headers: {'Authorization': 'Bearer valid-token'},
        );

        await expectLater(
          dio.get<dynamic>('/error'),
          throwsA(isA<DioException>()),
        );

        // Tokens should NOT be cleared for non-401 errors
        final bearerToken = await fakeStorage.read(key: 'id_token');
        final refreshToken = await fakeStorage.read(key: 'refresh_token');
        expect(bearerToken, 'valid-token');
        expect(refreshToken, 'valid-refresh');
      });
    });

    group('concurrent request handling', () {
      test('a burst of concurrent 401s all complete after a single refresh',
          () async {
        // Regression: the old _isRefreshing/_pendingRequests logic dropped a
        // queued handler when several requests hit 401 together, permanently
        // jamming the QueuedInterceptor error queue — every later request
        // that got an error response hung forever (Safari logout bug).
        var refreshCalls = 0;
        final testStorage = FakeSecureStorage();
        await testStorage.write(key: 'id_token', value: 'expired-token');
        await testStorage.write(key: 'refresh_token', value: 'valid-refresh');

        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'))
          // Set the adapter before constructing the interceptor: the
          // interceptor's retry Dio copies the adapter at construction.
          ..httpClientAdapter = TokenAwareAdapter(validBearer: 'fresh-token');
        final interceptor = AuthInterceptor(
          storage: testStorage,
          dio: testDio,
          // Like the app's callback, this one persists the new bearer.
          refreshTokenCallback: (_) async {
            refreshCalls++;
            await testStorage.write(key: 'id_token', value: 'fresh-token');
            return 'fresh-token';
          },
        );
        testDio.interceptors.add(interceptor);

        // Fire all requests before any completes so their 401s queue up
        // on the interceptor's serialized error queue together.
        final responses = await Future.wait([
          testDio.get<dynamic>('/r1'),
          testDio.get<dynamic>('/r2'),
          testDio.get<dynamic>('/r3'),
        ]).timeout(const Duration(seconds: 10));

        expect(responses.map((r) => r.statusCode), everyElement(200));
        // Later 401s see the already-refreshed stored token and retry with
        // it directly instead of refreshing again.
        expect(refreshCalls, 1);
      });

      test('concurrent 401s with a rejected refresh all complete (no jam)',
          () async {
        // Regression: with the refresh token rejected, the old code dropped
        // the second queued 401's handler on the floor — its request never
        // completed and the error queue jammed forever.
        final testStorage = FakeSecureStorage();
        await testStorage.write(key: 'id_token', value: 'expired-token');
        await testStorage.write(key: 'refresh_token', value: 'bad-refresh');

        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
        final interceptor = AuthInterceptor(
          storage: testStorage,
          dio: testDio,
          refreshTokenCallback: (_) async => null, // server rejects refresh
        );
        final adapter = DioAdapter(dio: testDio);
        testDio.interceptors.add(interceptor);

        adapter
          ..onGet('/a', (server) => server.reply(401, {'message': 'x'}))
          ..onGet('/b', (server) => server.reply(401, {'message': 'x'}))
          ..onGet('/c', (server) => server.reply(401, {'message': 'x'}));

        final results = await Future.wait<Object?>([
          for (final path in ['/a', '/b', '/c'])
            testDio
                .get<dynamic>(path)
                .then<Object?>((r) => r, onError: (Object e) => e),
        ]).timeout(const Duration(seconds: 10));

        // Every request completes — with an error, not a hang.
        expect(results, everyElement(isA<DioException>()));
        // Tokens cleared so the app can force re-authentication.
        expect(await testStorage.read(key: 'id_token'), isNull);
        expect(await testStorage.read(key: 'refresh_token'), isNull);
      });

      test('invokes refresh callback on 401', () async {
        await fakeStorage.write(key: 'id_token', value: 'expired-token');
        await fakeStorage.write(key: 'refresh_token', value: 'valid-refresh');

        dioAdapter.onGet(
          '/resource1',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
        );

        try {
          await dio.get<dynamic>('/resource1');
          fail('Should have thrown');
        } on DioException {
          // Expected: retry also gets 401 from static mock
        }

        // Verify refresh callback was invoked with correct token
        expect(capturedRefreshToken, 'valid-refresh');
      });
    });

    group('custom token keys', () {
      test('uses custom token keys when provided', () async {
        final customStorage = FakeSecureStorage();
        await customStorage.write(key: 'custom_access', value: 'custom-token');

        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
        final customInterceptor = AuthInterceptor(
          storage: customStorage,
          dio: testDio,
          refreshTokenCallback: (_) async => 'new-token',
          tokenKey: 'custom_access',
          refreshTokenKey: 'custom_refresh',
        );

        final testAdapter = DioAdapter(dio: testDio);
        testDio.interceptors.add(customInterceptor);

        testAdapter.onGet(
          '/protected',
          (server) => server.reply(200, {'data': 'success'}),
          headers: {'Authorization': 'Bearer custom-token'},
        );

        final response = await testDio.get<dynamic>('/protected');

        expect(response.statusCode, 200);
      });
    });

    group('retryInterceptors', () {
      test('run on the retried request after a token refresh', () async {
        final testStorage = FakeSecureStorage();
        await testStorage.write(key: 'id_token', value: 'expired-token');
        await testStorage.write(key: 'refresh_token', value: 'valid-refresh');

        final seen = <String>[];
        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'))
          ..httpClientAdapter = TokenAwareAdapter(validBearer: 'fresh-token');
        final interceptor = AuthInterceptor(
          storage: testStorage,
          dio: testDio,
          retryInterceptors: [
            InterceptorsWrapper(
              onRequest: (options, handler) {
                seen.add('${options.headers['Authorization']}');
                handler.next(options);
              },
            ),
          ],
          refreshTokenCallback: (_) async => 'fresh-token',
        );
        testDio.interceptors.add(interceptor);

        final response = await testDio.get<dynamic>('/r1');

        expect(response.statusCode, 200);
        expect(seen, ['Bearer fresh-token']);
      });
    });
  });

  // FUP-82: a refresh that outlives its session must not write it back.
  group('the interceptor writes no tokens itself', () {
    test('a refreshed bearer the callback did not keep is used, not stored',
        () async {
      // The app's callback keeps nothing when the session ended while it
      // ran; the retry still finishes the old session's own request.
      final storage = FakeSecureStorage();
      await storage.write(key: 'id_token', value: 'expired-token');
      await storage.write(key: 'refresh_token', value: 'r1');
      final dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'))
        ..httpClientAdapter = TokenAwareAdapter(validBearer: 'fresh-token');
      dio.interceptors.add(
        AuthInterceptor(
          storage: storage,
          dio: dio,
          refreshTokenCallback: (_) async {
            // A logout cleared the slots while the refresh ran.
            await storage.delete(key: 'id_token');
            await storage.delete(key: 'refresh_token');
            return 'fresh-token';
          },
        ),
      );

      final response = await dio.get<dynamic>('/devices');

      expect(response.statusCode, 200);
      expect(await storage.read(key: 'id_token'), isNull);
      expect(await storage.read(key: 'refresh_token'), isNull);
    });

    test('a rejected refresh keeps slots another sign-in rewrote meanwhile',
        () async {
      final storage = FakeSecureStorage();
      await storage.write(key: 'id_token', value: 'old-id');
      await storage.write(key: 'refresh_token', value: 'old-refresh');
      final dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'))
        ..httpClientAdapter = RecordingAdapter(rejected: {'old-id'});
      dio.interceptors.add(
        AuthInterceptor(
          storage: storage,
          dio: dio,
          refreshTokenCallback: (_) async {
            // The next user signed in before the refusal came back.
            await storage.write(key: 'id_token', value: 'next-id');
            await storage.write(key: 'refresh_token', value: 'next-refresh');
            return null;
          },
        ),
      );

      await expectLater(
        dio.get<dynamic>('/orders'),
        throwsA(isA<DioException>()),
      );

      expect(await storage.read(key: 'id_token'), 'next-id');
      expect(await storage.read(key: 'refresh_token'), 'next-refresh');
    });
  });

  // FUP-96: web tabs share the token slots.
  group('another account\'s session in the shared slots', () {
    late FakeSecureStorage storage;
    late SessionSignals signals;
    late int otherAccountSignals;
    late int refreshCalls;
    String? tabUser;

    setUp(() async {
      storage = FakeSecureStorage();
      signals = SessionSignals.forTesting();
      otherAccountSignals = 0;
      refreshCalls = 0;
      tabUser = 'user-a';
      final sub =
          signals.onOtherAccountSignedIn.listen((_) => otherAccountSignals++);
      addTearDown(sub.cancel);
    });

    Dio build(
      RecordingAdapter adapter, {
      Future<String?> Function(String)? refresh,
      bool checkTab = true,
    }) {
      final dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'))
        ..httpClientAdapter = adapter;
      dio.interceptors.add(
        AuthInterceptor(
          storage: storage,
          dio: dio,
          signals: signals,
          sessionSubject: checkTab ? () => tabUser : null,
          refreshTokenCallback: refresh ??
              (_) async {
                refreshCalls++;
                return null;
              },
        ),
      );
      return dio;
    }

    test('a stored token of another account is never sent', () async {
      await storage.write(key: 'id_token', value: jwt('user-b'));
      final adapter = RecordingAdapter();
      final dio = build(adapter);

      const body = {'entityId': 'crop-of-a'};
      final error = await dio
          .post<dynamic>('/sync', data: body)
          .then<Object?>((_) => null, onError: (Object e) => e);
      await Future<void>.delayed(Duration.zero);

      expect(error, isA<DioException>());
      expect(
        (error! as DioException).error,
        isA<OtherAccountSessionException>(),
      );
      expect(adapter.bearers, isEmpty, reason: 'nothing left the tab');
      expect(otherAccountSignals, 1);
      expect(await storage.read(key: 'id_token'), jwt('user-b'));
    });

    test('the tab\'s own token is sent', () async {
      await storage.write(key: 'id_token', value: jwt('user-a'));
      final adapter = RecordingAdapter();
      final dio = build(adapter);

      await dio.get<dynamic>('/orders');

      expect(adapter.bearers, [jwt('user-a')]);
      expect(otherAccountSignals, 0);
    });

    test('no signed-in account, no check (login, mobile)', () async {
      await storage.write(key: 'id_token', value: jwt('user-b'));
      tabUser = null;
      final adapter = RecordingAdapter();

      await build(adapter).get<dynamic>('/orders');
      await build(adapter, checkTab: false).get<dynamic>('/orders');

      expect(adapter.bearers, [jwt('user-b'), jwt('user-b')]);
      expect(otherAccountSignals, 0);
    });

    test('a token whose sub cannot be read is sent as before', () async {
      await storage.write(key: 'id_token', value: 'not-a-jwt');
      final adapter = RecordingAdapter();

      await build(adapter).get<dynamic>('/orders');

      expect(adapter.bearers, ['not-a-jwt']);
      expect(otherAccountSignals, 0);
    });

    test(
        'a 401 after another tab signed in: no refresh, no retry with the '
        'other account\'s token', () async {
      await storage.write(key: 'id_token', value: jwt('user-a'));
      await storage.write(key: 'refresh_token', value: 'refresh-a');
      final adapter = RecordingAdapter(
        rejected: {jwt('user-a')},
        onFetch: () async {
          // Another tab signs user-b in while the request is out.
          await storage.write(key: 'current_user_id', value: 'user-b');
          await storage.write(key: 'id_token', value: jwt('user-b'));
          await storage.write(key: 'refresh_token', value: 'refresh-b');
        },
      );
      final dio = build(adapter);

      await expectLater(
        dio.get<dynamic>('/orders'),
        throwsA(isA<DioException>()),
      );
      await Future<void>.delayed(Duration.zero);

      expect(adapter.bearers, [jwt('user-a')], reason: 'no retry');
      expect(refreshCalls, 0);
      expect(otherAccountSignals, 1);
      expect(await storage.read(key: 'id_token'), jwt('user-b'));
      expect(await storage.read(key: 'refresh_token'), 'refresh-b');
    });

    test('a refreshed bearer of another account is not retried with', () async {
      await storage.write(key: 'id_token', value: jwt('user-a'));
      await storage.write(key: 'refresh_token', value: 'refresh-b');
      final adapter = RecordingAdapter(rejected: {jwt('user-a')});
      final dio = build(adapter, refresh: (_) async => jwt('user-b'));

      await expectLater(
        dio.get<dynamic>('/orders'),
        throwsA(isA<DioException>()),
      );
      await Future<void>.delayed(Duration.zero);

      expect(adapter.bearers, [jwt('user-a')], reason: 'no retry');
      expect(otherAccountSignals, 1);
    });

    test('the tab\'s own refreshed bearer is retried with', () async {
      await storage.write(key: 'id_token', value: jwt('user-a'));
      await storage.write(key: 'refresh_token', value: 'refresh-a');
      final adapter = RecordingAdapter(rejected: {jwt('user-a')});
      final dio =
          build(adapter, refresh: (_) async => jwt('user-a', issued: 1));

      final response = await dio.get<dynamic>('/orders');

      expect(response.statusCode, 200);
      expect(adapter.bearers, [jwt('user-a'), jwt('user-a', issued: 1)]);
      expect(otherAccountSignals, 0);
    });
  });
}
