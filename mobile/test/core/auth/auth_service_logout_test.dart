import 'dart:convert';
import 'dart:typed_data';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/device_identity.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/network/api_config.dart';

class MockConnectivity extends Mock implements Connectivity {}

class MockOfflineAuthCache extends Mock implements OfflineAuthCache {}

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

const _phone = '+251911234567';
const _refreshToken = 'refresh-1';
const _mintedAccess = 'minted-access';

/// A Cognito-shaped JWT whose payload carries [exp].
String _jwt(DateTime exp, {String sub = 'user-1'}) {
  String part(Map<String, Object> json) =>
      base64Url.encode(utf8.encode(jsonEncode(json))).replaceAll('=', '');
  final payload = {'sub': sub, 'exp': exp.millisecondsSinceEpoch ~/ 1000};
  return '${part({'alg': 'RS256'})}.${part(payload)}.signature';
}

ResponseBody _json(int status, Map<String, Object> body) =>
    ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );

/// Fake auth API. http_mock_adapter cannot answer the same path differently
/// per Authorization header, which the retry tests need.
class _FakeAuthApi implements HttpClientAdapter {
  /// Answer for POST /auth/logout, by the bearer it was sent with.
  ResponseBody Function(String? bearer) onLogout = (bearer) =>
      bearer == _mintedAccess
          ? _json(200, {'loggedOut': true})
          : _json(401, {'message': 'Unauthorized'});

  /// Answer for POST /auth/refresh.
  ResponseBody Function() onRefresh = () => _json(200, {
        'tokens': {
          'accessToken': _mintedAccess,
          'idToken': 'minted-id',
          'expiresIn': 900,
        },
      });

  final calls = <({String path, String? bearer, Object? body})>[];

  List<String> get paths => [for (final c in calls) c.path];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final auth = options.headers['Authorization'] as String?;
    final bearer = auth?.substring('Bearer '.length);
    calls.add((path: options.path, bearer: bearer, body: options.data));
    if (options.path == ApiConfig.refreshEndpoint) return onRefresh();
    if (options.path == ApiConfig.logoutEndpoint) return onLogout(bearer);
    return _json(404, {'message': 'not found'});
  }

  @override
  void close({bool force = false}) {}
}

void main() {
  late _FakeAuthApi api;
  late MockConnectivity connectivity;
  late MockOfflineAuthCache cache;
  late AuthService service;

  final liveAccess = _jwt(DateTime.now().add(const Duration(minutes: 10)));
  final expiredAccess =
      _jwt(DateTime.now().subtract(const Duration(minutes: 1)));

  setUp(() {
    api = _FakeAuthApi();
    final dio = Dio(BaseOptions(baseUrl: 'https://api.test'))
      ..httpClientAdapter = api;

    connectivity = MockConnectivity();
    when(() => connectivity.checkConnectivity())
        .thenAnswer((_) async => [ConnectivityResult.wifi]);

    cache = MockOfflineAuthCache();
    when(() => cache.clearCredentials(any())).thenAnswer((_) async {});

    final storage = MockSecureStorage();
    when(() => storage.read(key: any(named: 'key')))
        .thenAnswer((_) async => 'device-abc');

    service = AuthService(
      dio: dio,
      offlineCache: cache,
      connectivity: connectivity,
      deviceIdentity:
          DeviceIdentity(storage: storage, deviceName: 'Android phone'),
      apiConfigured: true,
    );
  });

  Future<void> logout({String? accessToken, String? refreshToken}) =>
      service.logout(
        phoneNumber: _phone,
        accessToken: accessToken,
        refreshToken: refreshToken,
        clearCache: false,
      );

  group('logout revokes the refresh token (SIM-55)', () {
    test('a live access token revokes with a single call', () async {
      api.onLogout = (bearer) => bearer == liveAccess
          ? _json(200, {'loggedOut': true})
          : _json(401, {'message': 'Unauthorized'});

      await logout(accessToken: liveAccess, refreshToken: _refreshToken);

      expect(api.paths, [ApiConfig.logoutEndpoint]);
      expect(api.calls.single.bearer, liveAccess);
      expect(api.calls.single.body, {'refreshToken': _refreshToken});
    });

    test(
        'an expired access token (the reported 401) is replaced by one '
        'minted from the refresh token, and nothing is stored', () async {
      await logout(accessToken: expiredAccess, refreshToken: _refreshToken);

      expect(api.paths, [ApiConfig.refreshEndpoint, ApiConfig.logoutEndpoint]);
      expect(api.calls.first.body, {'refreshToken': _refreshToken});
      expect(api.calls.last.bearer, _mintedAccess);
      expect(api.calls.last.body, {'refreshToken': _refreshToken});
      // The minted tokens are used once and dropped: no cache write.
      verifyZeroInteractions(cache);
    });

    test('a missing access token is minted too', () async {
      await logout(refreshToken: _refreshToken);

      expect(api.paths, [ApiConfig.refreshEndpoint, ApiConfig.logoutEndpoint]);
      expect(api.calls.last.bearer, _mintedAccess);
    });

    test('a 401 on a bearer that looked live is retried once, minted',
        () async {
      // Undecodable token: the expiry check cannot tell, the server can.
      await logout(accessToken: 'opaque-token', refreshToken: _refreshToken);

      expect(api.paths, [
        ApiConfig.logoutEndpoint,
        ApiConfig.refreshEndpoint,
        ApiConfig.logoutEndpoint,
      ]);
      expect(api.calls.first.bearer, 'opaque-token');
      expect(api.calls.last.bearer, _mintedAccess);
    });

    test(
        'AUTH_026 (bearer of another user) is retried once with a bearer '
        'minted from the refresh token itself', () async {
      final otherUser = _jwt(
        DateTime.now().add(const Duration(minutes: 10)),
        sub: 'user-2',
      );
      api.onLogout = (bearer) => bearer == _mintedAccess
          ? _json(200, {'loggedOut': true})
          : _json(403, {
              'error': {
                'code': 'AUTH_026',
                'message':
                    'The refresh token does not belong to the signed-in user.',
              },
            });

      await logout(accessToken: otherUser, refreshToken: _refreshToken);

      expect(api.paths, [
        ApiConfig.logoutEndpoint,
        ApiConfig.refreshEndpoint,
        ApiConfig.logoutEndpoint,
      ]);
      expect(api.calls.last.bearer, _mintedAccess);
    });

    test('a refused mint stops: the refresh token is already dead', () async {
      api.onRefresh = () => _json(401, {
            'error': {'code': 'AUTH_019', 'message': 'Refresh token revoked'},
          });

      await logout(accessToken: expiredAccess, refreshToken: _refreshToken);

      expect(api.paths, [ApiConfig.refreshEndpoint]);
    });

    test('a minted bearer refused again is not retried a second time',
        () async {
      api.onLogout = (_) => _json(401, {'message': 'Unauthorized'});

      await logout(accessToken: expiredAccess, refreshToken: _refreshToken);

      expect(api.paths, [ApiConfig.refreshEndpoint, ApiConfig.logoutEndpoint]);
    });

    test('any other refusal (503) is not retried and does not throw', () async {
      api.onLogout = (_) => _json(503, {
            'error': {'code': 'AUTH_008', 'message': 'Cognito unavailable'},
          });

      await logout(accessToken: liveAccess, refreshToken: _refreshToken);

      expect(api.paths, [ApiConfig.logoutEndpoint]);
    });

    test('offline: no request is made', () async {
      when(() => connectivity.checkConnectivity())
          .thenAnswer((_) async => [ConnectivityResult.none]);

      await logout(accessToken: liveAccess, refreshToken: _refreshToken);

      expect(api.calls, isEmpty);
    });

    test('without a refresh token there is nothing to revoke', () async {
      await logout(accessToken: liveAccess);
      await logout(accessToken: liveAccess, refreshToken: '');

      expect(api.calls, isEmpty);
    });

    test('clearCache still clears offline credentials when revocation fails',
        () async {
      api.onLogout = (_) => _json(500, {'message': 'boom'});

      await service.logout(
        phoneNumber: _phone,
        accessToken: liveAccess,
        refreshToken: _refreshToken,
      );

      verify(() => cache.clearCredentials(_phone)).called(1);
    });
  });

  group('isJwtExpired', () {
    final now = DateTime.utc(2026, 10, 23, 12);

    test('a past exp is expired', () {
      expect(
        isJwtExpired(
          _jwt(now.subtract(const Duration(minutes: 1))),
          now: now,
        ),
        isTrue,
      );
    });

    test('an exp inside the clock-skew margin counts as expired', () {
      expect(
        isJwtExpired(_jwt(now.add(const Duration(seconds: 10))), now: now),
        isTrue,
      );
    });

    test('an exp beyond the margin is live', () {
      expect(
        isJwtExpired(_jwt(now.add(const Duration(minutes: 5))), now: now),
        isFalse,
      );
    });

    test('a token that cannot be read is left to the server', () {
      expect(isJwtExpired('opaque-token', now: now), isFalse);
      expect(isJwtExpired('a.not-base64!.c', now: now), isFalse);
      final noExp = '${base64Url.encode(utf8.encode('{}'))}.'
          '${base64Url.encode(utf8.encode('{"sub":"u"}'))}.s';
      expect(isJwtExpired(noExp, now: now), isFalse);
    });
  });
}
