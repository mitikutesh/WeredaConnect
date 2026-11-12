import 'dart:async';
import 'dart:convert';

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
import 'package:wereda_connect/core/auth/session_signals.dart';
import 'package:wereda_connect/core/auth/token_refresh.dart';
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

/// Answers POST /auth/refresh with fresh tokens once [gate] completes.
class _GatedRefreshAdapter implements HttpClientAdapter {
  final gate = Completer<void>();
  var requests = 0;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests++;
    await gate.future;
    return ResponseBody.fromString(
      jsonEncode({
        'tokens': {
          'accessToken': 'late-access',
          'idToken': 'late-id',
          'expiresIn': 900,
        },
      }),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

/// An unsigned JWT whose `sub` is [sub].
String _jwt(String sub) {
  String part(Map<String, dynamic> m) =>
      base64Url.encode(utf8.encode(jsonEncode(m))).replaceAll('=', '');
  return '${part({'alg': 'none'})}.${part({'sub': sub})}.sig';
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

/// When true, every storage read fails (a broken keystore or WebCrypto).
bool _failReads = false;

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
            if (_failReads) {
              throw PlatformException(code: 'read-failed');
            }
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
    _failReads = false;
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
  Future<AuthBloc> signedIn(
    AuthService service, {
    SessionSignals? signals,
    Future<String?> Function()? pushTokenProvider,
  }) async {
    _store['current_user_phone'] = _phone;
    final bloc = AuthBloc(
      authService: service,
      offlineCache: offlineCache,
      sessionSignals: signals ?? SessionSignals.forTesting(),
      pushTokenProvider: pushTokenProvider,
    );
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

    test(
        'another user signed in since (web tabs share localStorage): the '
        'stored tokens are not this session\'s, the login copy is revoked',
        () async {
      final bloc = await signedIn(service);
      // A second tab signed user-002 in; the slots are theirs now.
      _store['current_user_id'] = 'user-002';
      _store['access_token'] = 'other-user-access';
      _store['refresh_token'] = 'other-user-refresh';

      await logOut(bloc);

      verify(
        () => service.logout(
          phoneNumber: _phone,
          accessToken: 'login-access',
          refreshToken: 'login-refresh',
          clearCache: false,
        ),
      ).called(1);
      verifyNever(
        () => service.logout(
          phoneNumber: any(named: 'phoneNumber'),
          accessToken: any(named: 'accessToken'),
          refreshToken: 'other-user-refresh',
          clearCache: any(named: 'clearCache'),
        ),
      );
    });

    test(
        'unreadable storage falls back to the login copy and logout still '
        'completes locally', () async {
      final bloc = await signedIn(service);
      _failReads = true;

      await logOut(bloc);

      expect(bloc.state, isA<AuthUnauthenticated>());
      verify(
        () => service.logout(
          phoneNumber: _phone,
          accessToken: 'login-access',
          refreshToken: 'login-refresh',
          clearCache: false,
        ),
      ).called(1);
      expectSessionCleared();
      verify(() => offlineCache.clearCredentials(_phone)).called(1);
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

  // FUP-82 and FUP-96: on web every tab shares the stored session.
  group('another tab signed another account in', () {
    late MockAuthService service;
    late SessionSignals signals;

    /// What another tab's sign-in of user-002 leaves in the shared slots.
    const otherSession = {
      'current_user_phone': '+251911000002',
      'current_user_id': 'user-002',
      'access_token': 'other-access',
      'id_token': 'other-id',
      'refresh_token': 'other-refresh',
    };

    setUp(() {
      service = MockAuthService();
      signals = SessionSignals.forTesting();
      when(
        () => service.logout(
          phoneNumber: any(named: 'phoneNumber'),
          accessToken: any(named: 'accessToken'),
          refreshToken: any(named: 'refreshToken'),
          clearCache: any(named: 'clearCache'),
        ),
      ).thenAnswer((_) async {});
    });

    test('a stale tab\'s logout keeps the other account\'s session', () async {
      final bloc = await signedIn(service, signals: signals);
      _store.addAll(otherSession);

      await logOut(bloc);

      expect(bloc.state, isA<AuthUnauthenticated>());
      expect(_store, otherSession);
      // This account's own offline cache still goes.
      verify(() => offlineCache.clearCredentials(_phone)).called(1);
    });

    test('without a user pointer, the ID token\'s sub decides', () async {
      final bloc = await signedIn(service, signals: signals);
      _store
        ..remove('current_user_id')
        ..['id_token'] = _jwt('user-002');

      await logOut(bloc);

      expect(_store['id_token'], _jwt('user-002'));
    });

    test('this account\'s own token without a pointer is cleared', () async {
      final bloc = await signedIn(service, signals: signals);
      _store
        ..remove('current_user_id')
        ..['id_token'] = _jwt('user-001');

      await logOut(bloc);

      expectSessionCleared();
    });

    test(
        'the session token\'s sub is the tab\'s identity, also when the '
        'profile id differs', () async {
      // A profile resolved by phone can carry another id than the sub.
      when(() => offlineCache.getCachedTokens(_phone)).thenAnswer(
        (_) async => AuthTokens(
          accessToken: 'login-access',
          refreshToken: 'login-refresh',
          idToken: _jwt('cognito-sub'),
        ),
      );
      final bloc = await signedIn(service, signals: signals);
      expect(bloc.sessionSubject, 'cognito-sub');

      // No pointer, and the stored token is this session's own.
      _store.remove('current_user_id');
      await logOut(bloc);

      expectSessionCleared();
    });

    test('a stale tab\'s expired session keeps the other account\'s', () async {
      final bloc = await signedIn(service, signals: signals);
      _store.addAll(otherSession);

      signals.sessionExpired();
      await bloc.stream.firstWhere((s) => s is AuthUnauthenticated);

      expect(_store, otherSession);
    });

    test(
        'a suspended account\'s refused write signs out like an expired '
        'session and says why', () async {
      final bloc = await signedIn(service, signals: signals);

      signals.accountSuspended();
      final state = await bloc.stream
          .firstWhere((s) => s is AuthUnauthenticated)
          .timeout(const Duration(seconds: 2));

      expect(state, isA<AuthSuspendedSignedOut>());
      expect(bloc.sessionSubject, isNull);
    });

    test('an expired session is a plain sign-out, not a suspension', () async {
      final bloc = await signedIn(service, signals: signals);

      signals.sessionExpired();
      final state = await bloc.stream
          .firstWhere((s) => s is AuthUnauthenticated)
          .timeout(const Duration(seconds: 2));

      expect(state, isNot(isA<AuthSuspendedSignedOut>()));
    });

    test(
        'the interceptor\'s signal signs the tab out and leaves the stored '
        'session and the offline cache alone', () async {
      final bloc = await signedIn(service, signals: signals);
      _store.addAll(otherSession);

      signals.otherAccountSignedIn();
      await bloc.stream
          .firstWhere((s) => s is AuthUnauthenticated)
          .timeout(const Duration(seconds: 2));

      expect(_store, otherSession);
      expect(bloc.sessionSubject, isNull);
      verifyNever(() => offlineCache.clearCredentials(any()));
      verifyNever(
        () => service.logout(
          phoneNumber: any(named: 'phoneNumber'),
          accessToken: any(named: 'accessToken'),
          refreshToken: any(named: 'refreshToken'),
          clearCache: any(named: 'clearCache'),
        ),
      );
    });

    test('the signal is ignored while the stored session is still this one',
        () async {
      final bloc = await signedIn(service, signals: signals);

      signals.otherAccountSignedIn();
      await Future<void>.delayed(const Duration(milliseconds: 50));

      expect(bloc.state, isA<AuthAuthenticated>());
      expect(_store['current_user_id'], 'user-001');
    });

    test('sessionSubject: set by sign-in, kept while logging out, then null',
        () async {
      final seen = <(String, String?)>[];
      final bloc = AuthBloc(
        authService: service,
        offlineCache: offlineCache,
        sessionSignals: signals,
      );
      addTearDown(bloc.close);
      bloc.stream.listen(
        (s) => seen.add((s.runtimeType.toString(), bloc.sessionSubject)),
      );
      expect(bloc.sessionSubject, isNull);
      _store['current_user_phone'] = _phone;

      bloc.add(const AuthCheckRequested());
      await bloc.stream.firstWhere((s) => s is AuthAuthenticated);
      await logOut(bloc);

      expect(seen, [
        ('AuthLoading', null),
        ('AuthAuthenticated', 'user-001'),
        ('AuthLoading', 'user-001'),
        ('AuthUnauthenticated', null),
      ]);
    });
  });

  // FUP-82: the FCM token unregister gave up while the interceptor was
  // refreshing; the refresh answers after the local clear.
  group('a token refresh that outlives the logout', () {
    late MockAuthService service;
    late SessionSignals signals;
    late _GatedRefreshAdapter refreshServer;
    late List<String> cacheWrites;
    late Future<String?> Function(String) refresh;
    Future<String?>? lateRefresh;

    setUp(() {
      service = MockAuthService();
      signals = SessionSignals.forTesting();
      refreshServer = _GatedRefreshAdapter();
      cacheWrites = [];
      lateRefresh = null;
      // The app's own refresh callback, on the same storage as the bloc.
      refresh = buildRefreshTokenCallback(
        refreshDio: Dio(BaseOptions(baseUrl: 'https://api.test'))
          ..httpClientAdapter = refreshServer,
        storage: const FlutterSecureStorage(
          aOptions: AndroidOptions(encryptedSharedPreferences: true),
        ),
        signals: signals,
        writeCachedTokens: (phone, _) async => cacheWrites.add(phone),
      );
      when(
        () => service.logout(
          phoneNumber: any(named: 'phoneNumber'),
          accessToken: any(named: 'accessToken'),
          refreshToken: any(named: 'refreshToken'),
          clearCache: any(named: 'clearCache'),
        ),
      ).thenAnswer((_) async {});
      // The unregister's 401 starts a refresh; the unregister gives up
      // before the refresh answers.
      when(() => service.unregisterDeviceToken(any())).thenAnswer((_) async {
        lateRefresh = refresh(_store['refresh_token']!);
      });
    });

    test('its tokens are not written back after the clear', () async {
      final bloc = await signedIn(
        service,
        signals: signals,
        pushTokenProvider: () async => 'fcm-1',
      );
      _store['refresh_token'] = 'live-refresh';

      await logOut(bloc);
      expectSessionCleared();

      refreshServer.gate.complete();
      expect(await lateRefresh, 'late-id');

      expect(refreshServer.requests, 1);
      expectSessionCleared();
      expect(cacheWrites, isEmpty, reason: 'offline cache stays cleared');
    });

    test('nor over the next user\'s session', () async {
      final bloc = await signedIn(
        service,
        signals: signals,
        pushTokenProvider: () async => 'fcm-1',
      );
      await logOut(bloc);
      // The next user signs in before the old refresh answers.
      _store.addAll({
        'current_user_phone': '+251911000002',
        'current_user_id': 'user-002',
        'access_token': 'next-access',
        'id_token': 'next-id',
        'refresh_token': 'next-refresh',
      });

      refreshServer.gate.complete();
      await lateRefresh;

      expect(_store['id_token'], 'next-id');
      expect(_store['access_token'], 'next-access');
      expect(_store['refresh_token'], 'next-refresh');
      expect(cacheWrites, isEmpty);
    });

    test(
        'a refresh that starts after the unregister keeps nothing, even when '
        'it answers before the session is cleared', () async {
      // Started after the unregister (say a sync request's 401), so it
      // shares the session count the logout is still on.
      when(
        () => service.logout(
          phoneNumber: any(named: 'phoneNumber'),
          accessToken: any(named: 'accessToken'),
          refreshToken: any(named: 'refreshToken'),
          clearCache: any(named: 'clearCache'),
        ),
      ).thenAnswer((_) async {
        lateRefresh = refresh('live-refresh');
      });
      // It answers while the offline cache is being cleared.
      when(() => offlineCache.clearCredentials(any())).thenAnswer((_) async {
        refreshServer.gate.complete();
        await lateRefresh;
      });
      final bloc = await signedIn(service, signals: signals);
      _store['refresh_token'] = 'live-refresh';

      await logOut(bloc);

      expect(await lateRefresh, 'late-id');
      expect(cacheWrites, isEmpty, reason: 'offline cache stays cleared');
      expectSessionCleared();
    });

    test('a stale tab\'s logout leaves another account\'s refresh token',
        () async {
      final bloc = await signedIn(service, signals: signals);
      _store
        ..['current_user_id'] = 'user-002'
        ..['refresh_token'] = 'other-refresh';
      String? slotDuringRevoke;
      when(
        () => service.logout(
          phoneNumber: any(named: 'phoneNumber'),
          accessToken: any(named: 'accessToken'),
          refreshToken: any(named: 'refreshToken'),
          clearCache: any(named: 'clearCache'),
        ),
      ).thenAnswer((_) async {
        await Future<void>.delayed(Duration.zero);
        slotDuringRevoke = _store['refresh_token'];
      });

      await logOut(bloc);

      expect(slotDuringRevoke, 'other-refresh');
      expect(_store['refresh_token'], 'other-refresh');
    });

    test('a refresh that answers within the logout is kept (control)',
        () async {
      when(() => service.unregisterDeviceToken(any())).thenAnswer((_) async {
        refreshServer.gate.complete();
        await refresh(_store['refresh_token']!);
      });
      String? revoked;
      when(
        () => service.logout(
          phoneNumber: any(named: 'phoneNumber'),
          accessToken: any(named: 'accessToken'),
          refreshToken: any(named: 'refreshToken'),
          clearCache: any(named: 'clearCache'),
        ),
      ).thenAnswer((invocation) async {
        revoked = invocation.namedArguments[#accessToken] as String?;
      });
      final bloc = await signedIn(
        service,
        signals: signals,
        pushTokenProvider: () async => 'fcm-1',
      );

      await logOut(bloc);

      // The in-time refresh was stored, so logout revoked with its tokens.
      expect(revoked, 'late-access');
      expect(cacheWrites, [_phone]);
      expectSessionCleared();
    });
  });
}
