import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter/services.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/device_identity.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

class MockAuthService extends Mock implements AuthService {}

class MockOfflineAuthCache extends Mock implements OfflineAuthCache {}

class MockConnectivity extends Mock implements Connectivity {}

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

/// Records every request; any request at all fails the offline test.
class _RecordingAdapter implements HttpClientAdapter {
  final paths = <String>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    paths.add(options.path);
    return ResponseBody.fromString('{}', 200);
  }

  @override
  void close({bool force = false}) {}
}

const _phone = '+251911234567';

/// The tokens from login, as held in [AuthAuthenticated.tokens].
const _loginTokens = AuthTokens(
  accessToken: 'login-access',
  refreshToken: 'login-refresh',
  idToken: 'login-id',
);

final _user = User()
  ..userId = 'user-001'
  ..phoneNumber = _phone
  ..name = 'Abebe Kebede'
  ..role = UserRole.farmer
  ..woreda = 'Jimma'
  ..createdAt = DateTime.utc(2026)
  ..updatedAt = DateTime.utc(2026)
  ..version = 1;

/// FlutterSecureStorage method-channel backing store.
final Map<String, String> _store = {};

/// SIM-55: the server-side logout is started before the local session is
/// cleared, with the live tokens, and logout stays local-first.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late MockOfflineAuthCache offlineCache;
  late List<String> calls;

  setUpAll(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (MethodCall call) async {
        final args = call.arguments as Map<dynamic, dynamic>?;
        final key = args?['key'] as String?;
        switch (call.method) {
          case 'write':
            final value = args!['value'] as String?;
            if (value != null) _store[key!] = value;
            return null;
          case 'read':
            return _store[key];
          case 'delete':
            _store.remove(key);
            return null;
          case 'deleteAll':
            _store.clear();
            return null;
          default:
            return null;
        }
      },
    );
  });

  setUp(() {
    _store.clear();
    calls = [];
    offlineCache = MockOfflineAuthCache();
    when(() => offlineCache.getCachedTokens(_phone))
        .thenAnswer((_) async => _loginTokens);
    when(() => offlineCache.getCachedUser(_phone))
        .thenAnswer((_) async => _user);
    when(() => offlineCache.clearCredentials(any())).thenAnswer((_) async {
      calls.add('clearCredentials');
    });
  });

  /// A restored session: the slots hold the login tokens, as after login.
  Future<AuthBloc> signedIn(AuthService service) async {
    _store['current_user_phone'] = _phone;
    final bloc = AuthBloc(authService: service, offlineCache: offlineCache);
    addTearDown(bloc.close);
    bloc.add(const AuthCheckRequested());
    await bloc.stream.firstWhere((s) => s is AuthAuthenticated);
    return bloc;
  }

  Future<void> logOut(AuthBloc bloc) async {
    bloc.add(const AuthLogoutRequested());
    await bloc.stream
        .firstWhere((s) => s is AuthUnauthenticated)
        .timeout(const Duration(seconds: 2));
  }

  void expectSessionCleared() {
    for (final key in [
      'current_user_phone',
      'current_user_id',
      'access_token',
      'id_token',
      'refresh_token',
    ]) {
      expect(_store[key], isNull, reason: key);
    }
  }

  group('with a stubbed service', () {
    late MockAuthService service;
    late Map<String, String>? storeAtLogout;

    setUp(() {
      service = MockAuthService();
      storeAtLogout = null;
      when(
        () => service.logout(
          phoneNumber: any(named: 'phoneNumber'),
          accessToken: any(named: 'accessToken'),
          refreshToken: any(named: 'refreshToken'),
          clearCache: any(named: 'clearCache'),
        ),
      ).thenAnswer((_) async {
        calls.add('logout');
        storeAtLogout = Map.of(_store);
      });
    });

    test(
        'the server logout is sent before the session is cleared, with the '
        'live tokens rather than the login copy', () async {
      final bloc = await signedIn(service);
      // The interceptor refreshed the slots since login.
      _store['access_token'] = 'live-access';
      _store['refresh_token'] = 'live-refresh';

      await logOut(bloc);

      expect(calls, ['logout', 'clearCredentials']);
      expect(storeAtLogout!['refresh_token'], 'live-refresh');
      expect(storeAtLogout!['current_user_phone'], _phone);
      verify(
        () => service.logout(
          phoneNumber: _phone,
          accessToken: 'live-access',
          refreshToken: 'live-refresh',
          clearCache: false,
        ),
      ).called(1);
      expectSessionCleared();
    });

    test('empty slots fall back to the tokens from login', () async {
      final bloc = await signedIn(service);
      _store.remove('access_token');
      _store.remove('refresh_token');

      await logOut(bloc);

      verify(
        () => service.logout(
          phoneNumber: _phone,
          accessToken: 'login-access',
          refreshToken: 'login-refresh',
          clearCache: false,
        ),
      ).called(1);
    });

    test('a revocation that never answers does not hold up local logout',
        () async {
      when(
        () => service.logout(
          phoneNumber: any(named: 'phoneNumber'),
          accessToken: any(named: 'accessToken'),
          refreshToken: any(named: 'refreshToken'),
          clearCache: any(named: 'clearCache'),
        ),
      ).thenAnswer((_) => Completer<void>().future);
      final bloc = await signedIn(service);

      await logOut(bloc);

      expect(bloc.state, isA<AuthUnauthenticated>());
      expectSessionCleared();
      verify(() => offlineCache.clearCredentials(_phone)).called(1);
    });
  });

  test('offline: logout completes locally and sends nothing', () async {
    final adapter = _RecordingAdapter();
    final connectivity = MockConnectivity();
    when(() => connectivity.checkConnectivity())
        .thenAnswer((_) async => [ConnectivityResult.none]);
    final deviceStorage = MockSecureStorage();
    when(() => deviceStorage.read(key: any(named: 'key')))
        .thenAnswer((_) async => 'device-abc');
    final service = AuthService(
      dio: Dio(BaseOptions(baseUrl: 'https://api.test'))
        ..httpClientAdapter = adapter,
      offlineCache: offlineCache,
      connectivity: connectivity,
      deviceIdentity:
          DeviceIdentity(storage: deviceStorage, deviceName: 'Android phone'),
      apiConfigured: true,
    );
    final bloc = await signedIn(service);

    await logOut(bloc);

    expect(adapter.paths, isEmpty);
    expectSessionCleared();
    // Cleared once, by the bloc; the service is told not to.
    verify(() => offlineCache.clearCredentials(_phone)).called(1);
  });
}
