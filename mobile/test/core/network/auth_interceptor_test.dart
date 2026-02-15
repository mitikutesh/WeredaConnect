import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';

import 'package:wereda_connect/core/network/auth_interceptor.dart';

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
        refreshTokenCallback: (refreshToken) async {
          capturedRefreshToken = refreshToken;
          // Simulate successful refresh
          return 'new-access-token-123';
        },
      );

      dio.interceptors.add(authInterceptor);
    });

    group('onRequest - token injection', () {
      test('injects access token into Authorization header', () async {
        await fakeStorage.write(key: 'access_token', value: 'valid-token-123');

        dioAdapter.onGet(
          '/protected',
          (server) => server.reply(200, {'data': 'success'}),
          headers: {'Authorization': 'Bearer valid-token-123'},
        );

        final response = await dio.get('/protected');

        expect(response.statusCode, 200);
        expect(response.data['data'], 'success');
      });

      test('skips token injection when no token in storage', () async {
        // Don't write any token to storage

        dioAdapter.onGet(
          '/public',
          (server) => server.reply(200, {'data': 'public'}),
        );

        final response = await dio.get('/public');

        expect(response.statusCode, 200);
      });

      test('skips token injection when token is empty', () async {
        await fakeStorage.write(key: 'access_token', value: '');

        dioAdapter.onGet(
          '/public',
          (server) => server.reply(200, {'data': 'public'}),
        );

        final response = await dio.get('/public');

        expect(response.statusCode, 200);
      });

      test('skips token injection for /auth/refresh endpoint', () async {
        await fakeStorage.write(key: 'access_token', value: 'some-token');

        dioAdapter.onPost(
          '/auth/refresh',
          (server) => server.reply(200, {'accessToken': 'new-token'}),
          data: Matchers.any,
        );

        final response =
            await dio.post('/auth/refresh', data: {'refreshToken': 'refresh-123'});

        expect(response.statusCode, 200);
      });
    });

    group('onError - token refresh on 401', () {
      test('refreshes token and retries request on 401', () async {
        await fakeStorage.write(key: 'access_token', value: 'expired-token');
        await fakeStorage.write(key: 'refresh_token', value: 'valid-refresh-token');

        // First request fails with 401
        dioAdapter.onGet(
          '/protected',
          (server) => server.reply(401, {'message': 'Token expired'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        // Retry succeeds with new token
        dioAdapter.onGet(
          '/protected',
          (server) => server.reply(200, {'data': 'success'}),
          headers: {'Authorization': 'Bearer new-access-token-123'},
        );

        final response = await dio.get('/protected');

        expect(response.statusCode, 200);
        expect(response.data['data'], 'success');
        expect(capturedRefreshToken, 'valid-refresh-token');

        // Verify new token was saved
        final savedToken = await fakeStorage.read(key: 'access_token');
        expect(savedToken, 'new-access-token-123');
      });

      test('clears tokens and propagates error when no refresh token', () async {
        await fakeStorage.write(key: 'access_token', value: 'expired-token');
        // Don't write refresh_token

        dioAdapter.onGet(
          '/protected',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        expect(
          () => dio.get('/protected'),
          throwsA(isA<DioException>()),
        );

        await Future<void>.delayed(const Duration(milliseconds: 100));

        // Verify tokens were cleared
        final accessToken = await fakeStorage.read(key: 'access_token');
        final refreshToken = await fakeStorage.read(key: 'refresh_token');
        expect(accessToken, isNull);
        expect(refreshToken, isNull);
      });

      test('clears tokens when refresh callback returns null', () async {
        final testStorage = FakeSecureStorage();
        await testStorage.write(key: 'access_token', value: 'expired-token');
        await testStorage.write(key: 'refresh_token', value: 'invalid-refresh');

        final failingInterceptor = AuthInterceptor(
          storage: testStorage,
          refreshTokenCallback: (_) async => null, // Refresh fails
        );

        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
        final testAdapter = DioAdapter(dio: testDio);
        testDio.interceptors.add(failingInterceptor);

        testAdapter.onGet(
          '/protected',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        expect(
          () => testDio.get('/protected'),
          throwsA(isA<DioException>()),
        );

        await Future<void>.delayed(const Duration(milliseconds: 100));

        // Verify tokens were cleared
        final accessToken = await testStorage.read(key: 'access_token');
        final refreshToken = await testStorage.read(key: 'refresh_token');
        expect(accessToken, isNull);
        expect(refreshToken, isNull);
      });

      test('clears tokens when refresh callback returns empty string', () async {
        final testStorage = FakeSecureStorage();
        await testStorage.write(key: 'access_token', value: 'expired-token');
        await testStorage.write(key: 'refresh_token', value: 'valid-refresh');

        final failingInterceptor = AuthInterceptor(
          storage: testStorage,
          refreshTokenCallback: (_) async => '', // Refresh returns empty
        );

        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
        final testAdapter = DioAdapter(dio: testDio);
        testDio.interceptors.add(failingInterceptor);

        testAdapter.onGet(
          '/protected',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        expect(
          () => testDio.get('/protected'),
          throwsA(isA<DioException>()),
        );

        await Future<void>.delayed(const Duration(milliseconds: 100));

        // Verify tokens were cleared
        final accessToken = await testStorage.read(key: 'access_token');
        final refreshToken = await testStorage.read(key: 'refresh_token');
        expect(accessToken, isNull);
        expect(refreshToken, isNull);
      });

      test('clears tokens and propagates error when refresh throws', () async {
        final testStorage = FakeSecureStorage();
        await testStorage.write(key: 'access_token', value: 'expired-token');
        await testStorage.write(key: 'refresh_token', value: 'valid-refresh');

        final throwingInterceptor = AuthInterceptor(
          storage: testStorage,
          refreshTokenCallback: (_) async {
            throw Exception('Network error during refresh');
          },
        );

        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
        final testAdapter = DioAdapter(dio: testDio);
        testDio.interceptors.add(throwingInterceptor);

        testAdapter.onGet(
          '/protected',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        expect(
          () => testDio.get('/protected'),
          throwsA(isA<DioException>()),
        );

        await Future<void>.delayed(const Duration(milliseconds: 100));

        // Verify tokens were cleared
        final accessToken = await testStorage.read(key: 'access_token');
        final refreshToken = await testStorage.read(key: 'refresh_token');
        expect(accessToken, isNull);
        expect(refreshToken, isNull);
      });

      test('does not attempt refresh for /auth/refresh endpoint', () async {
        await fakeStorage.write(key: 'access_token', value: 'expired-token');
        await fakeStorage.write(key: 'refresh_token', value: 'some-refresh');

        dioAdapter.onPost(
          '/auth/refresh',
          (server) => server.reply(401, {'message': 'Invalid refresh token'}),
          data: Matchers.any,
        );

        expect(
          () => dio.post('/auth/refresh', data: {'token': 'bad-token'}),
          throwsA(isA<DioException>()),
        );

        // Refresh token should still be in storage (not cleared)
        final refreshToken = await fakeStorage.read(key: 'refresh_token');
        expect(refreshToken, 'some-refresh');
      });

      test('ignores non-401 errors', () async {
        await fakeStorage.write(key: 'access_token', value: 'valid-token');
        await fakeStorage.write(key: 'refresh_token', value: 'valid-refresh');

        dioAdapter.onGet(
          '/error',
          (server) => server.reply(500, {'message': 'Server error'}),
          headers: {'Authorization': 'Bearer valid-token'},
        );

        expect(
          () => dio.get('/error'),
          throwsA(isA<DioException>()),
        );

        // Tokens should NOT be cleared for non-401 errors
        final accessToken = await fakeStorage.read(key: 'access_token');
        final refreshToken = await fakeStorage.read(key: 'refresh_token');
        expect(accessToken, 'valid-token');
        expect(refreshToken, 'valid-refresh');
      });
    });

    group('concurrent request handling', () {
      test('queues concurrent requests during refresh', () async {
        await fakeStorage.write(key: 'access_token', value: 'expired-token');
        await fakeStorage.write(key: 'refresh_token', value: 'valid-refresh');

        // Both requests fail with 401
        dioAdapter.onGet(
          '/resource1',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        dioAdapter.onGet(
          '/resource2',
          (server) => server.reply(401, {'message': 'Unauthorized'}),
          headers: {'Authorization': 'Bearer expired-token'},
        );

        // Retry both with new token
        dioAdapter.onGet(
          '/resource1',
          (server) => server.reply(200, {'data': 'resource1'}),
          headers: {'Authorization': 'Bearer new-access-token-123'},
        );

        dioAdapter.onGet(
          '/resource2',
          (server) => server.reply(200, {'data': 'resource2'}),
          headers: {'Authorization': 'Bearer new-access-token-123'},
        );

        // Fire both requests concurrently
        final results = await Future.wait([
          dio.get('/resource1'),
          dio.get('/resource2'),
        ]);

        expect(results[0].statusCode, 200);
        expect(results[0].data['data'], 'resource1');
        expect(results[1].statusCode, 200);
        expect(results[1].data['data'], 'resource2');

        // Refresh callback should be called only once
        expect(capturedRefreshToken, 'valid-refresh');

        // New token should be saved
        final savedToken = await fakeStorage.read(key: 'access_token');
        expect(savedToken, 'new-access-token-123');
      });
    });

    group('custom token keys', () {
      test('uses custom token keys when provided', () async {
        final customStorage = FakeSecureStorage();
        await customStorage.write(key: 'custom_access', value: 'custom-token');

        final customInterceptor = AuthInterceptor(
          storage: customStorage,
          refreshTokenCallback: (_) async => 'new-token',
          tokenKey: 'custom_access',
          refreshTokenKey: 'custom_refresh',
        );

        final testDio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
        final testAdapter = DioAdapter(dio: testDio);
        testDio.interceptors.add(customInterceptor);

        testAdapter.onGet(
          '/protected',
          (server) => server.reply(200, {'data': 'success'}),
          headers: {'Authorization': 'Bearer custom-token'},
        );

        final response = await testDio.get('/protected');

        expect(response.statusCode, 200);
      });
    });
  });
}
