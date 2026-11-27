import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/models/auth_result.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

class MockAuthService extends Mock implements AuthService {}

class MockOfflineAuthCache extends Mock implements OfflineAuthCache {}

const _phone = '+251911234567';
const _tokens = AuthTokens(
  accessToken: 'access-1',
  refreshToken: 'refresh-1',
  idToken: 'id-1',
);
const _freshTokens = AuthTokens(
  accessToken: 'access-2',
  refreshToken: 'refresh-2',
  idToken: 'id-2',
);

final Map<String, String> _storage = {};

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late MockAuthService service;
  late MockOfflineAuthCache cache;
  late StreamController<void> sessionExpired;

  setUpAll(() {
    registerFallbackValue(UserRole.farmer);
    registerFallbackValue(
      const AuthTokens(accessToken: '', refreshToken: '', idToken: ''),
    );
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

  setUp(() {
    _storage.clear();
    service = MockAuthService();
    cache = MockOfflineAuthCache();
    sessionExpired = StreamController<void>.broadcast();
    when(() => cache.clearCredentials(any())).thenAnswer((_) async {});
  });

  tearDown(() => sessionExpired.close());

  AuthBloc build({Future<String?> Function()? pushToken}) => AuthBloc(
        authService: service,
        offlineCache: cache,
        pushTokenProvider: pushToken,
        sessionExpired: sessionExpired.stream,
      );

  Future<List<AuthState>> collect(
    AuthBloc bloc,
    AuthEvent event, {
    required int count,
  }) async {
    final states = <AuthState>[];
    final done = Completer<void>();
    final sub = bloc.stream.listen((s) {
      states.add(s);
      if (states.length >= count && !done.isCompleted) done.complete();
    });
    bloc.add(event);
    await done.future.timeout(const Duration(seconds: 5));
    await sub.cancel();
    return states;
  }

  void stubRegister(AuthResult result) {
    when(
      () => service.register(
        phoneNumber: any(named: 'phoneNumber'),
        password: any(named: 'password'),
        name: any(named: 'name'),
        role: any(named: 'role'),
        woreda: any(named: 'woreda'),
        faydaVerificationToken: any(named: 'faydaVerificationToken'),
        preferredLanguage: any(named: 'preferredLanguage'),
        drivingLicensePath: any(named: 'drivingLicensePath'),
        buyerType: any(named: 'buyerType'),
        companyName: any(named: 'companyName'),
      ),
    ).thenAnswer((_) async => result);
  }

  void stubVerify(AuthResult result) {
    when(
      () => service.verifyOtp(
        phoneNumber: any(named: 'phoneNumber'),
        otpCode: any(named: 'otpCode'),
        password: any(named: 'password'),
        drivingLicensePath: any(named: 'drivingLicensePath'),
        buyerType: any(named: 'buyerType'),
        companyName: any(named: 'companyName'),
      ),
    ).thenAnswer((_) async => result);
  }

  AuthResult signedIn(AuthTokens tokens) => AuthResult.success(
        tokens,
        userId: 'user-1',
        role: UserRole.buyer,
        userName: 'Abebe',
        woreda: 'Adama',
      );

  const register = AuthRegisterRequested(
    phoneNumber: _phone,
    password: 'Secret123',
    name: 'Abebe',
    role: UserRole.buyer,
    woreda: 'Adama',
    buyerType: 'company',
    companyName: 'Adama Grain',
  );

  group('registration OTP', () {
    test('a 202 register moves to AuthOtpVerificationRequired', () async {
      final expires = DateTime.utc(2026, 9, 25, 10, 5);
      stubRegister(AuthResult.otpRequired(otpExpiresAt: expires));
      final bloc = build();
      addTearDown(bloc.close);

      final states = await collect(bloc, register, count: 2);

      expect(states.first, isA<AuthLoading>());
      expect(
        states.last,
        AuthOtpVerificationRequired(phoneNumber: _phone, expiresAt: expires),
      );
      expect(_storage['id_token'], isNull);
    });

    test('verify-otp signs in and reuses the in-memory password + extras',
        () async {
      stubRegister(AuthResult.otpRequired());
      stubVerify(signedIn(_tokens));
      final bloc = build();
      addTearDown(bloc.close);
      await collect(bloc, register, count: 2);

      final states = await collect(
        bloc,
        const AuthVerifyOtpRequested(otpCode: '123456'),
        count: 2,
      );

      expect(states.last, isA<AuthAuthenticated>());
      final auth = states.last as AuthAuthenticated;
      expect(auth.userId, 'user-1');
      expect(auth.role, UserRole.buyer);
      expect(auth.isRestoredSession, isFalse);
      expect(_storage['id_token'], 'id-1');
      expect(_storage['current_user_phone'], _phone);
      verify(
        () => service.verifyOtp(
          phoneNumber: _phone,
          otpCode: '123456',
          password: 'Secret123',
          buyerType: 'company',
          companyName: 'Adama Grain',
        ),
      ).called(1);
    });

    test('a wrong code surfaces AUTH_012 and keeps the pending phone',
        () async {
      stubRegister(AuthResult.otpRequired());
      stubVerify(
        AuthResult.failure(AuthErrorCode.otpInvalidOrExpired, 'bad code'),
      );
      final bloc = build();
      addTearDown(bloc.close);
      await collect(bloc, register, count: 2);

      final states = await collect(
        bloc,
        const AuthVerifyOtpRequested(otpCode: '000000'),
        count: 2,
      );

      expect(
        (states.last as AuthError).errorCode,
        AuthErrorCode.otpInvalidOrExpired,
      );

      stubVerify(signedIn(_tokens));
      final retry = await collect(
        bloc,
        const AuthVerifyOtpRequested(otpCode: '123456'),
        count: 2,
      );
      expect(retry.last, isA<AuthAuthenticated>());
    });

    test('resend re-emits the OTP state with the new expiry', () async {
      stubRegister(AuthResult.otpRequired());
      final newExpiry = DateTime.utc(2026, 9, 25, 11);
      when(() => service.resendOtp(phoneNumber: _phone)).thenAnswer(
        (_) async => AuthResult.otpRequired(otpExpiresAt: newExpiry),
      );
      final bloc = build();
      addTearDown(bloc.close);
      await collect(bloc, register, count: 2);

      final states =
          await collect(bloc, const AuthResendOtpRequested(), count: 2);

      final otp = states.last as AuthOtpVerificationRequired;
      expect(otp.phoneNumber, _phone);
      expect(otp.expiresAt, newExpiry);
      expect(otp.resentAt, isNotNull);
    });

    test('a login for an unconfirmed account (AUTH_016) goes to OTP', () async {
      when(
        () => service.login(phoneNumber: _phone, password: 'Secret123'),
      ).thenAnswer(
        (_) async => AuthResult.failure(
          AuthErrorCode.userNotConfirmed,
          'not confirmed',
        ),
      );
      when(() => service.resendOtp(phoneNumber: _phone))
          .thenAnswer((_) async => AuthResult.otpRequired());
      stubVerify(signedIn(_tokens));
      final bloc = build();
      addTearDown(bloc.close);

      final states = await collect(
        bloc,
        const AuthLoginRequested(phoneNumber: _phone, password: 'Secret123'),
        count: 2,
      );
      expect(states.last, isA<AuthOtpVerificationRequired>());

      await collect(
        bloc,
        const AuthVerifyOtpRequested(otpCode: '123456'),
        count: 2,
      );
      verify(
        () => service.verifyOtp(
          phoneNumber: _phone,
          otpCode: '123456',
          password: 'Secret123',
        ),
      ).called(1);
    });
  });

  group('pending OTP password is dropped', () {
    Future<void> expectVerifyWithoutPassword(AuthBloc bloc) async {
      await collect(
        bloc,
        const AuthVerifyOtpRequested(otpCode: '654321', phoneNumber: _phone),
        count: 2,
      );
      verify(
        () => service.verifyOtp(
          phoneNumber: _phone,
          otpCode: '654321',
          password: any(named: 'password', that: isNull),
          drivingLicensePath: any(named: 'drivingLicensePath', that: isNull),
          buyerType: any(named: 'buyerType', that: isNull),
          companyName: any(named: 'companyName', that: isNull),
        ),
      ).called(1);
    }

    test('when the OTP screen is abandoned', () async {
      stubRegister(AuthResult.otpRequired());
      stubVerify(signedIn(_tokens));
      final bloc = build();
      addTearDown(bloc.close);
      await collect(bloc, register, count: 2);

      bloc.add(const AuthOtpAbandoned());
      await expectVerifyWithoutPassword(bloc);
    });

    test('on logout', () async {
      stubRegister(AuthResult.otpRequired());
      stubVerify(signedIn(_tokens));
      final bloc = build();
      addTearDown(bloc.close);
      await collect(bloc, register, count: 2);

      bloc.add(const AuthLogoutRequested());
      await expectVerifyWithoutPassword(bloc);
    });

    test('after a successful verification', () async {
      stubRegister(AuthResult.otpRequired());
      stubVerify(signedIn(_tokens));
      final bloc = build();
      addTearDown(bloc.close);
      await collect(bloc, register, count: 2);
      await collect(
        bloc,
        const AuthVerifyOtpRequested(otpCode: '123456'),
        count: 2,
      );

      await expectVerifyWithoutPassword(bloc);
    });
  });

  group('step-up', () {
    Future<AuthBloc> signedInBloc() async {
      when(() => service.login(phoneNumber: _phone, password: 'Secret123'))
          .thenAnswer((_) async => signedIn(_tokens));
      final bloc = build();
      addTearDown(bloc.close);
      await collect(
        bloc,
        const AuthLoginRequested(phoneNumber: _phone, password: 'Secret123'),
        count: 2,
      );
      return bloc;
    }

    test('swaps in the fresh tokens without clearing anything', () async {
      final bloc = await signedInBloc();

      final states = await collect(
        bloc,
        const AuthStepUpSucceeded(tokens: _freshTokens),
        count: 1,
      );

      final auth = states.single as AuthAuthenticated;
      expect(auth.tokens, _freshTokens);
      expect(auth.userId, 'user-1');
      expect(_storage['id_token'], 'id-2');
      expect(_storage['refresh_token'], 'refresh-2');
      expect(_storage['current_user_phone'], _phone);
      verifyNever(() => cache.clearCredentials(any()));
    });
  });

  group('session expiry (refresh rejected)', () {
    test('signs the user out and clears the dead cached session', () async {
      when(() => service.login(phoneNumber: _phone, password: 'Secret123'))
          .thenAnswer((_) async => signedIn(_tokens));
      final bloc = build();
      addTearDown(bloc.close);
      await collect(
        bloc,
        const AuthLoginRequested(phoneNumber: _phone, password: 'Secret123'),
        count: 2,
      );

      final next = bloc.stream.first;
      sessionExpired.add(null);
      expect(await next, isA<AuthUnauthenticated>());

      verify(() => cache.clearCredentials(_phone)).called(1);
      expect(_storage['current_user_phone'], isNull);
      expect(_storage['id_token'], isNull);
    });

    test('is ignored when nobody is signed in', () async {
      final bloc = build();
      addTearDown(bloc.close);
      sessionExpired.add(null);
      await Future<void>.delayed(const Duration(milliseconds: 20));
      expect(bloc.state, isA<AuthInitial>());
      verifyNever(() => cache.clearCredentials(any()));
    });
  });

  group('logout', () {
    setUp(() {
      when(
        () => service.logout(
          phoneNumber: any(named: 'phoneNumber'),
          accessToken: any(named: 'accessToken'),
          refreshToken: any(named: 'refreshToken'),
          clearCache: any(named: 'clearCache'),
        ),
      ).thenAnswer((_) async {});
      when(() => service.login(phoneNumber: _phone, password: 'Secret123'))
          .thenAnswer((_) async => signedIn(_tokens));
    });

    test('unregisters the push token while the bearer is still stored',
        () async {
      String? bearerAtUnregister;
      when(() => service.unregisterDeviceToken('fcm-1')).thenAnswer((_) async {
        bearerAtUnregister = _storage['id_token'];
      });
      final bloc = build(pushToken: () async => 'fcm-1');
      addTearDown(bloc.close);
      await collect(
        bloc,
        const AuthLoginRequested(phoneNumber: _phone, password: 'Secret123'),
        count: 2,
      );

      final states = await collect(bloc, const AuthLogoutRequested(), count: 2);

      expect(states.last, isA<AuthUnauthenticated>());
      verify(() => service.unregisterDeviceToken('fcm-1')).called(1);
      expect(bearerAtUnregister, 'id-1');
      expect(_storage['id_token'], isNull);
    });

    test('a failing push-token lookup never blocks logout', () async {
      final bloc = build(pushToken: () async => throw Exception('no fcm'));
      addTearDown(bloc.close);
      await collect(
        bloc,
        const AuthLoginRequested(phoneNumber: _phone, password: 'Secret123'),
        count: 2,
      );

      final states = await collect(bloc, const AuthLogoutRequested(), count: 2);

      expect(states.last, isA<AuthUnauthenticated>());
      verifyNever(() => service.unregisterDeviceToken(any()));
    });
  });

  test('a session restored on app start is flagged for the app lock', () async {
    _storage['current_user_phone'] = _phone;
    when(() => cache.getCachedTokens(_phone)).thenAnswer((_) async => _tokens);
    when(() => cache.getCachedUser(_phone)).thenAnswer(
      (_) async => User()
        ..userId = 'user-1'
        ..phoneNumber = _phone
        ..name = 'Abebe'
        ..role = UserRole.buyer
        ..woreda = 'Adama'
        ..createdAt = DateTime.utc(2026)
        ..updatedAt = DateTime.utc(2026)
        ..version = 1,
    );
    final bloc = build();
    addTearDown(bloc.close);

    final states = await collect(bloc, const AuthCheckRequested(), count: 2);

    expect((states.last as AuthAuthenticated).isRestoredSession, isTrue);
  });
}
