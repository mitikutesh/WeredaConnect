import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/models/auth_result.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/live_trip/sign_out_forgetter.dart';

class _MockAuthService extends Mock implements AuthService {}

class _MockOfflineAuthCache extends Mock implements OfflineAuthCache {}

const _phone = '+251900100002';
const _trucker = 'trucker-1';
const _tokens = AuthTokens(
  accessToken: 'access-1',
  refreshToken: 'refresh-1',
  idToken: 'id-1',
);

final Map<String, String> _storage = {};

/// T-3 review: the kept drop-off handoffs go on an explicit logout and with
/// a deleted account only. A session that lapsed (the 48 h offline window
/// at start, a refresh token the server refused) keeps them: nothing can
/// renew the drop-off code after pickup.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late _MockAuthService service;
  late _MockOfflineAuthCache cache;
  late MemoryDropoffHandoffStore handoffs;
  late List<String> tracksForgotten;
  late StreamController<void> sessionExpired;

  setUpAll(() {
    registerFallbackValue(UserRole.trucker);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (MethodCall call) async {
        final args = (call.arguments as Map?)?.cast<dynamic, dynamic>();
        switch (call.method) {
          case 'write':
            final value = args!['value'] as String?;
            if (value != null) _storage[args['key'] as String] = value;
            return null;
          case 'read':
            return _storage[args!['key'] as String];
          case 'delete':
            _storage.remove(args!['key'] as String);
            return null;
          default:
            return null;
        }
      },
    );
  });

  setUp(() async {
    _storage.clear();
    service = _MockAuthService();
    cache = _MockOfflineAuthCache();
    sessionExpired = StreamController<void>.broadcast();
    handoffs = MemoryDropoffHandoffStore();
    tracksForgotten = [];
    await handoffs.keep(
      _trucker,
      'd-1',
      const KeptHandoff(token: 'tok', code: '757555978'),
    );
    when(() => cache.clearCredentials(any())).thenAnswer((_) async {});
    when(() => service.login(phoneNumber: _phone, password: 'Secret123'))
        .thenAnswer(
      (_) async => AuthResult.success(
        _tokens,
        userId: _trucker,
        role: UserRole.trucker,
        userName: 'Dawit',
        woreda: 'Meki',
      ),
    );
    when(
      () => service.logout(
        phoneNumber: any(named: 'phoneNumber'),
        accessToken: any(named: 'accessToken'),
        refreshToken: any(named: 'refreshToken'),
        clearCache: any(named: 'clearCache'),
      ),
    ).thenAnswer((_) async {});
  });

  tearDown(() => sessionExpired.close());

  AuthBloc build() {
    final bloc = AuthBloc(
      authService: service,
      offlineCache: cache,
      sessionExpired: sessionExpired.stream,
      forgetTrackSnapshots: signOutForgetter(
        handoffs,
        forgetTrackSnapshots: (userId) async => tracksForgotten.add(userId),
      ),
    );
    addTearDown(bloc.close);
    return bloc;
  }

  Future<void> waitFor(AuthBloc bloc, bool Function(AuthState) done) async {
    if (done(bloc.state)) return;
    await bloc.stream.firstWhere(done).timeout(const Duration(seconds: 5));
  }

  Future<AuthBloc> signedIn() async {
    final bloc = build();
    bloc.add(
      const AuthLoginRequested(phoneNumber: _phone, password: 'Secret123'),
    );
    await waitFor(bloc, (s) => s is AuthAuthenticated);
    return bloc;
  }

  test('an explicit logout drops the kept handoffs and the snapshots',
      () async {
    final bloc = await signedIn();

    bloc.add(const AuthLogoutRequested());
    await waitFor(bloc, (s) => s is AuthUnauthenticated);

    expect(await handoffs.read(_trucker, 'd-1'), isNull);
    expect(tracksForgotten, [_trucker]);
  });

  test('a refresh token the server refused keeps them', () async {
    final bloc = await signedIn();

    sessionExpired.add(null);
    await waitFor(bloc, (s) => s is AuthUnauthenticated);

    expect(await handoffs.read(_trucker, 'd-1'), isNotNull);
    expect(tracksForgotten, isEmpty);
  });

  test('the 48 h offline window running out at start keeps them', () async {
    // The phone is known, but the offline cache no longer gives tokens.
    _storage['current_user_phone'] = _phone;
    when(() => cache.getCachedTokens(_phone)).thenAnswer((_) async => null);
    final bloc = build();

    bloc.add(const AuthCheckRequested());
    await waitFor(bloc, (s) => s is AuthUnauthenticated);

    expect(await handoffs.read(_trucker, 'd-1'), isNotNull);
    expect(tracksForgotten, isEmpty);
  });

  test('a failing snapshot clear still drops the handoffs', () async {
    final forget = signOutForgetter(
      handoffs,
      forgetTrackSnapshots: (_) async => throw StateError('prefs'),
    );

    await expectLater(forget(_trucker), throwsStateError);

    expect(await handoffs.read(_trucker, 'd-1'), isNull);
  });
}
