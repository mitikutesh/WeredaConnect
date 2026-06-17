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

// ---------------------------------------------------------------------------
// Mocks
// ---------------------------------------------------------------------------
class MockAuthService extends Mock implements AuthService {}

class MockOfflineAuthCache extends Mock implements OfflineAuthCache {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

// ---------------------------------------------------------------------------
// Test data
// ---------------------------------------------------------------------------
const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

const _refreshedTokens = AuthTokens(
  accessToken: 'refreshed-access-token',
  refreshToken: 'refreshed-refresh-token',
  idToken: 'refreshed-id-token',
);

final _testUser = User()
  ..userId = 'user-001'
  ..phoneNumber = '+251911234567'
  ..name = 'Abebe Kebede'
  ..role = UserRole.farmer
  ..woreda = 'Jimma'
  ..createdAt = DateTime.utc(2026)
  ..updatedAt = DateTime.utc(2026)
  ..version = 1;

/// In-memory store for FlutterSecureStorage method channel mock.
final Map<String, String> _secureStorageData = {};

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late MockAuthService authService;
  late MockOfflineAuthCache offlineCache;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(
      const AuthTokens(
        accessToken: '',
        refreshToken: '',
        idToken: '',
      ),
    );
    registerFallbackValue(UserRole.farmer);

    // Mock FlutterSecureStorage method channel
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (MethodCall methodCall) async {
        switch (methodCall.method) {
          case 'write':
            final args = methodCall.arguments as Map<dynamic, dynamic>;
            final key = args['key'] as String;
            final value = args['value'] as String?;
            if (value != null) {
              _secureStorageData[key] = value;
            }
            return null;
          case 'read':
            final args = methodCall.arguments as Map<dynamic, dynamic>;
            final key = args['key'] as String;
            return _secureStorageData[key];
          case 'delete':
            final args = methodCall.arguments as Map<dynamic, dynamic>;
            final key = args['key'] as String;
            _secureStorageData.remove(key);
            return null;
          case 'deleteAll':
            _secureStorageData.clear();
            return null;
          default:
            return null;
        }
      },
    );
  });

  setUp(() {
    _secureStorageData.clear();
    authService = MockAuthService();
    offlineCache = MockOfflineAuthCache();
  });

  AuthBloc buildBloc() => AuthBloc(
        authService: authService,
        offlineCache: offlineCache,
      );

  /// Collects states emitted by [bloc] after adding [event].
  Future<List<AuthState>> collectStates(
    AuthBloc bloc,
    AuthEvent event, {
    int expectedCount = 2,
  }) async {
    final states = <AuthState>[];
    final subscription = bloc.stream.listen(states.add);

    bloc.add(event);

    // Wait enough time for the BLoC to process
    await Future<void>.delayed(const Duration(milliseconds: 300));

    await subscription.cancel();
    return states;
  }

  group('AuthBloc', () {
    // -----------------------------------------------------------------------
    // Login flow: success
    // -----------------------------------------------------------------------
    group('Login flow', () {
      test('emits [AuthLoading, AuthAuthenticated] on successful login',
          () async {
        when(
          () => authService.login(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.success(
            _testTokens,
            userId: 'user-001',
            role: UserRole.farmer,
          ),
        );
        when(() => offlineCache.getCachedUser(any()))
            .thenAnswer((_) async => _testUser);

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'test1234',
          ),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthAuthenticated>());

        final authenticated = states[1] as AuthAuthenticated;
        expect(authenticated.userId, equals('user-001'));
        expect(authenticated.tokens, equals(_testTokens));
      });

      test('uses role from login result even on cache miss (F-34)', () async {
        // Login result carries an authoritative role (trucker)...
        when(
          () => authService.login(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.success(
            _testTokens,
            userId: 'user-001',
            role: UserRole.trucker,
          ),
        );
        // ...but the local cache is empty. Must NOT silently default to farmer.
        when(() => offlineCache.getCachedUser(any()))
            .thenAnswer((_) async => null);

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'test1234',
          ),
        );

        expect(states[1], isA<AuthAuthenticated>());
        final authenticated = states[1] as AuthAuthenticated;
        expect(authenticated.role, equals(UserRole.trucker));
      });

      test('fails closed with AuthError when result has no role (F-34)',
          () async {
        // A successful login that somehow carries no resolvable role must
        // surface an error, not authenticate with a guessed (farmer) role.
        when(
          () => authService.login(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.success(_testTokens, userId: 'user-001'),
        );
        when(() => offlineCache.getCachedUser(any()))
            .thenAnswer((_) async => null);

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'test1234',
          ),
        );

        expect(states[1], isA<AuthError>());
      });

      test('emits [AuthLoading, AuthError] on failed login', () async {
        when(
          () => authService.login(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.failure(
            AuthErrorCode.invalidCredentials,
            'Invalid phone number or password',
          ),
        );

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'wrong',
          ),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthError>());

        final error = states[1] as AuthError;
        expect(error.errorCode, equals(AuthErrorCode.invalidCredentials));
        expect(error.canRetry, isTrue);
      });
    });

    // -----------------------------------------------------------------------
    // Registration flow (Fayda-only, no OTP)
    // -----------------------------------------------------------------------
    group('Registration flow', () {
      test(
          'emits [AuthLoading, AuthAuthenticated] on successful registration with Fayda token',
          () async {
        when(
          () => authService.register(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
            name: any(named: 'name'),
            role: any(named: 'role'),
            woreda: any(named: 'woreda'),
            faydaVerificationToken: any(named: 'faydaVerificationToken'),
            preferredLanguage: any(named: 'preferredLanguage'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.success(
            _testTokens,
            userId: 'user-002',
          ),
        );
        when(() => offlineCache.getCachedUser(any()))
            .thenAnswer((_) async => _testUser);

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthRegisterRequested(
            phoneNumber: '+251912345678',
            password: 'newpass1',
            name: 'Kebede Alemu',
            role: UserRole.trucker,
            woreda: 'Bahir Dar',
            faydaVerificationToken: 'fake-fayda-token',
          ),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthAuthenticated>());

        final authenticated = states[1] as AuthAuthenticated;
        expect(authenticated.userId, equals('user-002'));
        expect(authenticated.tokens, equals(_testTokens));
      });

      test('emits [AuthLoading, AuthError] on failed registration', () async {
        when(
          () => authService.register(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
            name: any(named: 'name'),
            role: any(named: 'role'),
            woreda: any(named: 'woreda'),
            faydaVerificationToken: any(named: 'faydaVerificationToken'),
            preferredLanguage: any(named: 'preferredLanguage'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.failure(
            AuthErrorCode.phoneAlreadyRegistered,
            'This phone number is already registered',
          ),
        );

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthRegisterRequested(
            phoneNumber: '+251911234567',
            password: 'pass1234',
            name: 'Test',
            role: UserRole.farmer,
            woreda: 'Jimma',
            faydaVerificationToken: 'fake-fayda-token',
          ),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthError>());

        final error = states[1] as AuthError;
        expect(error.errorCode, equals(AuthErrorCode.phoneAlreadyRegistered));
      });

      test('forwards role-specific buyer fields to AuthService.register (F-32)',
          () async {
        when(
          () => authService.register(
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
        ).thenAnswer(
          (_) async => AuthResult.success(
            _testTokens,
            userId: 'user-001',
            role: UserRole.buyer,
          ),
        );
        when(() => offlineCache.getCachedUser(any()))
            .thenAnswer((_) async => _testUser);

        final bloc = buildBloc();
        addTearDown(bloc.close);

        await collectStates(
          bloc,
          const AuthRegisterRequested(
            phoneNumber: '+251911234567',
            password: 'pass1234',
            name: 'Acme Buyer',
            role: UserRole.buyer,
            woreda: 'Adama',
            buyerType: 'company',
            companyName: 'Acme PLC',
          ),
        );

        // The BLoC must pass the buyer fields through to the service (they were
        // previously dropped); the driving-license stays null for a buyer.
        // Omitted named args (faydaVerificationToken, preferredLanguage,
        // drivingLicensePath) are matched against their defaults by mocktail,
        // so this also asserts the driving-license is NOT sent for a buyer.
        verify(
          () => authService.register(
            phoneNumber: '+251911234567',
            password: 'pass1234',
            name: 'Acme Buyer',
            role: UserRole.buyer,
            woreda: 'Adama',
            buyerType: 'company',
            companyName: 'Acme PLC',
          ),
        ).called(1);
      });
    });

    // -----------------------------------------------------------------------
    // Token refresh
    // -----------------------------------------------------------------------
    group('Token refresh', () {
      test('stays AuthAuthenticated with new tokens on successful refresh',
          () async {
        when(
          () => authService.refreshToken(any()),
        ).thenAnswer(
          (_) async => AuthResult.success(_refreshedTokens),
        );
        when(
          () => offlineCache.updateTokens(any(), any()),
        ).thenAnswer((_) async {});

        final bloc = buildBloc();
        addTearDown(bloc.close);

        // Seed authenticated state by simulating a successful login first
        when(
          () => authService.login(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.success(
            _testTokens,
            userId: 'user-001',
            role: UserRole.farmer,
          ),
        );
        when(() => offlineCache.getCachedUser(any()))
            .thenAnswer((_) async => _testUser);

        bloc.add(
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'test1234',
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 300));

        // Now refresh
        final states = await collectStates(
          bloc,
          const AuthRefreshTokenRequested(
            refreshToken: 'fake-refresh-token',
          ),
          expectedCount: 1,
        );

        // Should emit AuthAuthenticated with new tokens (no loading for refresh)
        expect(states.any((s) => s is AuthAuthenticated), isTrue);

        final authenticated = states.whereType<AuthAuthenticated>().last;
        expect(authenticated.tokens.accessToken, 'refreshed-access-token');
        expect(authenticated.userId, equals('user-001'));
      });

      test('emits AuthUnauthenticated on failed token refresh', () async {
        when(
          () => authService.refreshToken(any()),
        ).thenAnswer(
          (_) async => AuthResult.failure(
            AuthErrorCode.refreshTokenExpired,
            'Refresh token expired',
          ),
        );

        final bloc = buildBloc();
        addTearDown(bloc.close);

        // Seed authenticated state
        when(
          () => authService.login(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.success(
            _testTokens,
            userId: 'user-001',
            role: UserRole.farmer,
          ),
        );
        when(() => offlineCache.getCachedUser(any()))
            .thenAnswer((_) async => _testUser);

        bloc.add(
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'test1234',
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 300));

        // Refresh with expired token
        final states = await collectStates(
          bloc,
          const AuthRefreshTokenRequested(
            refreshToken: 'expired-refresh-token',
          ),
          expectedCount: 1,
        );

        expect(states.any((s) => s is AuthUnauthenticated), isTrue);
      });
    });

    // -----------------------------------------------------------------------
    // Logout
    // -----------------------------------------------------------------------
    group('Logout', () {
      test('emits [AuthLoading, AuthUnauthenticated] on logout', () async {
        when(
          () => authService.logout(
            phoneNumber: any(named: 'phoneNumber'),
            accessToken: any(named: 'accessToken'),
            refreshToken: any(named: 'refreshToken'),
            clearCache: any(named: 'clearCache'),
          ),
        ).thenAnswer((_) async {});

        final bloc = buildBloc();
        addTearDown(bloc.close);

        // Seed authenticated state
        when(
          () => authService.login(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.success(
            _testTokens,
            userId: 'user-001',
            role: UserRole.farmer,
          ),
        );
        when(() => offlineCache.getCachedUser(any()))
            .thenAnswer((_) async => _testUser);

        bloc.add(
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'test1234',
          ),
        );
        await Future<void>.delayed(const Duration(milliseconds: 300));

        // Now logout
        final states = await collectStates(
          bloc,
          const AuthLogoutRequested(),
        );

        expect(states.any((s) => s is AuthLoading), isTrue);
        expect(states.any((s) => s is AuthUnauthenticated), isTrue);
      });
    });

    // -----------------------------------------------------------------------
    // Session check
    // -----------------------------------------------------------------------
    group('Session check', () {
      test('emits [AuthLoading, AuthUnauthenticated] when no session',
          () async {
        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthCheckRequested(),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthUnauthenticated>());
      });

      test('emits [AuthLoading, AuthAuthenticated] when session exists',
          () async {
        // Pre-populate secure storage with a phone number
        _secureStorageData['current_user_phone'] = '+251911234567';

        when(() => offlineCache.getCachedTokens('+251911234567'))
            .thenAnswer((_) async => _testTokens);
        when(() => offlineCache.getCachedUser('+251911234567'))
            .thenAnswer((_) async => _testUser);

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthCheckRequested(),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthAuthenticated>());

        final authenticated = states[1] as AuthAuthenticated;
        expect(authenticated.userId, equals('user-001'));
      });
    });

    // -----------------------------------------------------------------------
    // Rate limit error with retryAfterSeconds
    // -----------------------------------------------------------------------
    group('Rate limit error', () {
      test('login returns AuthError with retryAfterSeconds', () async {
        when(
          () => authService.login(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.failure(
            AuthErrorCode.rateLimitExceeded,
            'Too many attempts',
            retryAfterSeconds: 300,
          ),
        );

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'test1234',
          ),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthError>());

        final error = states[1] as AuthError;
        expect(error.errorCode, equals(AuthErrorCode.rateLimitExceeded));
        expect(error.retryAfterSeconds, equals(300));
      });
    });

    // -----------------------------------------------------------------------
    // Offline login fallback
    // -----------------------------------------------------------------------
    group('Offline login fallback', () {
      test('network error falls back to offline login via AuthService.login',
          () async {
        when(
          () => authService.login(
            phoneNumber: any(named: 'phoneNumber'),
            password: any(named: 'password'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.success(
            _testTokens,
            userId: 'user-001',
            role: UserRole.farmer,
            isOfflineAuth: true,
          ),
        );
        when(() => offlineCache.getCachedUser(any()))
            .thenAnswer((_) async => _testUser);

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthLoginRequested(
            phoneNumber: '+251911234567',
            password: 'test1234',
          ),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthAuthenticated>());

        final authenticated = states[1] as AuthAuthenticated;
        expect(authenticated.isOfflineAuth, isTrue);
        expect(authenticated.userId, equals('user-001'));
      });
    });

    // -----------------------------------------------------------------------
    // Forgot password flow
    // -----------------------------------------------------------------------
    group('Forgot password flow', () {
      test('emits [AuthLoading, AuthPasswordResetCodeSent] on success',
          () async {
        when(
          () => authService.forgotPassword(
            phoneNumber: any(named: 'phoneNumber'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.success(
            const AuthTokens(accessToken: '', refreshToken: '', idToken: ''),
          ),
        );

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthForgotPasswordRequested(phoneNumber: '+251911234567'),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthPasswordResetCodeSent>());

        final codeSent = states[1] as AuthPasswordResetCodeSent;
        expect(codeSent.phoneNumber, equals('+251911234567'));
      });

      test('emits [AuthLoading, AuthError] on failure', () async {
        when(
          () => authService.forgotPassword(
            phoneNumber: any(named: 'phoneNumber'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.failure(
            AuthErrorCode.internalServerError,
            'Failed to send code',
          ),
        );

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthForgotPasswordRequested(phoneNumber: '+251911234567'),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthError>());
      });

      test(
          'emits [AuthLoading, AuthError] with retryAfterSeconds on rate limit',
          () async {
        when(
          () => authService.forgotPassword(
            phoneNumber: any(named: 'phoneNumber'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.failure(
            AuthErrorCode.rateLimitExceeded,
            'Too many attempts',
            retryAfterSeconds: 600,
          ),
        );

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthForgotPasswordRequested(phoneNumber: '+251911234567'),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthError>());

        final error = states[1] as AuthError;
        expect(error.retryAfterSeconds, equals(600));
      });

      test('emits [AuthLoading, AuthError] on unexpected exception', () async {
        when(
          () => authService.forgotPassword(
            phoneNumber: any(named: 'phoneNumber'),
          ),
        ).thenThrow(Exception('network timeout'));

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthForgotPasswordRequested(phoneNumber: '+251911234567'),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthError>());
      });
    });

    // -----------------------------------------------------------------------
    // Reset password flow
    // -----------------------------------------------------------------------
    group('Reset password flow', () {
      test('emits [AuthLoading, AuthPasswordResetSuccess] on success',
          () async {
        when(
          () => authService.resetPassword(
            phoneNumber: any(named: 'phoneNumber'),
            confirmationCode: any(named: 'confirmationCode'),
            newPassword: any(named: 'newPassword'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.success(
            const AuthTokens(accessToken: '', refreshToken: '', idToken: ''),
          ),
        );

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthResetPasswordRequested(
            phoneNumber: '+251911234567',
            confirmationCode: '123456',
            newPassword: 'NewPass1dev',
          ),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthPasswordResetSuccess>());
      });

      test('emits [AuthLoading, AuthError] on wrong code', () async {
        when(
          () => authService.resetPassword(
            phoneNumber: any(named: 'phoneNumber'),
            confirmationCode: any(named: 'confirmationCode'),
            newPassword: any(named: 'newPassword'),
          ),
        ).thenAnswer(
          (_) async => AuthResult.failure(
            AuthErrorCode.internalServerError,
            'Invalid or expired verification code.',
          ),
        );

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthResetPasswordRequested(
            phoneNumber: '+251911234567',
            confirmationCode: 'wrong',
            newPassword: 'NewPass1dev',
          ),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthError>());
      });

      test('emits [AuthLoading, AuthError] on unexpected exception', () async {
        when(
          () => authService.resetPassword(
            phoneNumber: any(named: 'phoneNumber'),
            confirmationCode: any(named: 'confirmationCode'),
            newPassword: any(named: 'newPassword'),
          ),
        ).thenThrow(Exception('socket error'));

        final bloc = buildBloc();
        addTearDown(bloc.close);

        final states = await collectStates(
          bloc,
          const AuthResetPasswordRequested(
            phoneNumber: '+251911234567',
            confirmationCode: '123456',
            newPassword: 'NewPass1dev',
          ),
        );

        expect(states.length, greaterThanOrEqualTo(2));
        expect(states[0], isA<AuthLoading>());
        expect(states[1], isA<AuthError>());
      });
    });
  });
}
