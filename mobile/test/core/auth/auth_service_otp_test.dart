import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/device_identity.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';

class MockConnectivity extends Mock implements Connectivity {}

class MockOfflineAuthCache extends Mock implements OfflineAuthCache {}

class MockSecureStorage extends Mock implements FlutterSecureStorage {}

const _phone = '+251911234567';

Map<String, dynamic> _tokens() => {
      'accessToken': 'access-1',
      'idToken': 'id-1',
      'refreshToken': 'refresh-1',
      'expiresIn': 900,
    };

Map<String, dynamic> _user({bool faydaVerified = false}) => {
      'userId': 'user-1',
      'phoneNumber': _phone,
      'displayName': 'Abebe',
      'fullName': 'Abebe Kebede',
      'role': 'buyer',
      'woreda': 'Adama',
      'faydaVerified': faydaVerified,
      if (faydaVerified) 'faydaVerifiedAt': '2026-09-01T10:00:00Z',
      'isPhoneVerified': true,
    };

void main() {
  late Dio dio;
  late DioAdapter adapter;
  late MockConnectivity connectivity;
  late MockOfflineAuthCache cache;
  late MockSecureStorage storage;
  late AuthService service;
  late List<RequestOptions> requests;

  setUpAll(() {
    registerFallbackValue(
      const AuthTokens(accessToken: '', refreshToken: '', idToken: ''),
    );
    registerFallbackValue(UserRole.farmer);
  });

  setUp(() {
    dio = Dio(BaseOptions(baseUrl: 'https://api.test'));
    adapter = DioAdapter(dio: dio);
    requests = [];
    dio.interceptors.add(
      InterceptorsWrapper(
        onRequest: (options, handler) {
          requests.add(options);
          handler.next(options);
        },
      ),
    );

    connectivity = MockConnectivity();
    when(() => connectivity.checkConnectivity())
        .thenAnswer((_) async => [ConnectivityResult.wifi]);

    cache = MockOfflineAuthCache();
    when(
      () => cache.cacheCredentials(
        phoneNumber: any(named: 'phoneNumber'),
        password: any(named: 'password'),
        userId: any(named: 'userId'),
        role: any(named: 'role'),
        name: any(named: 'name'),
        woreda: any(named: 'woreda'),
        tokens: any(named: 'tokens'),
        fullName: any(named: 'fullName'),
        displayName: any(named: 'displayName'),
        drivingLicensePath: any(named: 'drivingLicensePath'),
        buyerType: any(named: 'buyerType'),
        companyName: any(named: 'companyName'),
        faydaVerified: any(named: 'faydaVerified'),
        faydaVerifiedAt: any(named: 'faydaVerifiedAt'),
      ),
    ).thenAnswer((_) async {});
    when(
      () => cache.saveUserToIsar(
        userId: any(named: 'userId'),
        phoneNumber: any(named: 'phoneNumber'),
        name: any(named: 'name'),
        role: any(named: 'role'),
        woreda: any(named: 'woreda'),
        fullName: any(named: 'fullName'),
        displayName: any(named: 'displayName'),
        drivingLicensePath: any(named: 'drivingLicensePath'),
        buyerType: any(named: 'buyerType'),
        companyName: any(named: 'companyName'),
        faydaVerified: any(named: 'faydaVerified'),
        faydaVerifiedAt: any(named: 'faydaVerifiedAt'),
      ),
    ).thenAnswer((_) async {});
    when(() => cache.updateTokens(any(), any())).thenAnswer((_) async {});

    storage = MockSecureStorage();
    when(() => storage.read(key: DeviceIdentity.deviceIdKey))
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

  group('register', () {
    test('202 means an SMS OTP is required: no tokens, nothing cached',
        () async {
      adapter.onPost(
        '/auth/register',
        (server) => server.reply(202, {
          'userId': 'user-1',
          'phoneNumber': _phone,
          'otpRequired': true,
          'otpExpiresAt': '2026-09-25T10:05:00Z',
          'message': 'Verification code sent',
        }),
        data: Matchers.any,
      );

      final result = await service.register(
        phoneNumber: _phone,
        password: 'Secret123',
        name: 'Abebe',
        role: UserRole.buyer,
        woreda: 'Adama',
      );

      expect(result.isSuccess, isTrue);
      expect(result.otpRequired, isTrue);
      expect(result.tokens, isNull);
      expect(result.otpExpiresAt, DateTime.utc(2026, 9, 25, 10, 5));
      verifyNever(
        () => cache.cacheCredentials(
          phoneNumber: any(named: 'phoneNumber'),
          password: any(named: 'password'),
          userId: any(named: 'userId'),
          role: any(named: 'role'),
          name: any(named: 'name'),
          woreda: any(named: 'woreda'),
          tokens: any(named: 'tokens'),
        ),
      );
    });

    test('sends the device id and name headers', () async {
      adapter.onPost(
        '/auth/register',
        (server) => server.reply(202, {'otpRequired': true}),
        data: Matchers.any,
      );

      await service.register(
        phoneNumber: _phone,
        password: 'Secret123',
        name: 'Abebe',
        role: UserRole.buyer,
        woreda: 'Adama',
      );

      final headers = requests.single.headers;
      expect(headers[DeviceIdentity.deviceIdHeader], 'device-abc');
      expect(headers[DeviceIdentity.deviceNameHeader], 'Android phone');
    });

    test('201 with a matching Fayda token returns tokens and Fayda status',
        () async {
      adapter.onPost(
        '/auth/register',
        (server) => server.reply(201, {
          'userId': 'user-1',
          'tokens': _tokens(),
          'user': _user(faydaVerified: true),
        }),
        data: Matchers.any,
      );

      final result = await service.register(
        phoneNumber: _phone,
        password: 'Secret123',
        name: 'Abebe',
        role: UserRole.buyer,
        woreda: 'Adama',
        faydaVerificationToken: 'fayda-token',
      );

      expect(result.isSuccess, isTrue);
      expect(result.otpRequired, isFalse);
      expect(result.tokens!.idToken, 'id-1');
      expect(result.faydaVerified, isTrue);
      expect(result.role, UserRole.buyer);
      verify(
        () => cache.cacheCredentials(
          phoneNumber: _phone,
          password: 'Secret123',
          userId: 'user-1',
          role: UserRole.buyer,
          name: 'Abebe',
          woreda: 'Adama',
          tokens: any(named: 'tokens'),
          fullName: 'Abebe Kebede',
          displayName: 'Abebe',
          faydaVerified: true,
          faydaVerifiedAt: DateTime.utc(2026, 9, 1, 10),
        ),
      ).called(1);
    });
  });

  group('verifyOtp', () {
    test('returns tokens like a login and caches with the password', () async {
      adapter.onPost(
        '/auth/verify-otp',
        (server) => server.reply(200, {
          'tokens': _tokens(),
          'user': _user(),
        }),
        data: {
          'phoneNumber': _phone,
          'otpCode': '123456',
          'password': 'Secret123',
        },
      );

      final result = await service.verifyOtp(
        phoneNumber: _phone,
        otpCode: '123456',
        password: 'Secret123',
        buyerType: 'private',
      );

      expect(result.isSuccess, isTrue);
      expect(result.userId, 'user-1');
      expect(result.role, UserRole.buyer);
      expect(result.faydaVerified, isFalse);
      expect(
        requests.single.headers[DeviceIdentity.deviceIdHeader],
        'device-abc',
      );
      verify(
        () => cache.cacheCredentials(
          phoneNumber: _phone,
          password: 'Secret123',
          userId: 'user-1',
          role: UserRole.buyer,
          name: 'Abebe',
          woreda: 'Adama',
          tokens: any(named: 'tokens'),
          fullName: 'Abebe Kebede',
          displayName: 'Abebe',
          buyerType: 'private',
          faydaVerified: false,
        ),
      ).called(1);
    });

    test('without a password keeps the profile and tokens only', () async {
      adapter.onPost(
        '/auth/verify-otp',
        (server) => server.reply(200, {
          'tokens': _tokens(),
          'user': _user(),
        }),
        data: Matchers.any,
      );

      final result =
          await service.verifyOtp(phoneNumber: _phone, otpCode: '123456');

      expect(result.isSuccess, isTrue);
      verify(() => cache.updateTokens(_phone, any())).called(1);
      verifyNever(
        () => cache.cacheCredentials(
          phoneNumber: any(named: 'phoneNumber'),
          password: any(named: 'password'),
          userId: any(named: 'userId'),
          role: any(named: 'role'),
          name: any(named: 'name'),
          woreda: any(named: 'woreda'),
          tokens: any(named: 'tokens'),
        ),
      );
    });

    test('maps AUTH_012 (invalid or expired code)', () async {
      adapter.onPost(
        '/auth/verify-otp',
        (server) => server.reply(400, {
          'error': {'code': 'AUTH_012', 'message': 'Invalid or expired code'},
        }),
        data: Matchers.any,
      );

      final result =
          await service.verifyOtp(phoneNumber: _phone, otpCode: '000000');

      expect(result.isSuccess, isFalse);
      expect(result.errorCode, AuthErrorCode.otpInvalidOrExpired);
    });

    test('rejects an unknown role instead of defaulting to farmer', () async {
      adapter.onPost(
        '/auth/verify-otp',
        (server) => server.reply(200, {
          'tokens': _tokens(),
          'user': {..._user(), 'role': 'admin'},
        }),
        data: Matchers.any,
      );

      final result =
          await service.verifyOtp(phoneNumber: _phone, otpCode: '123456');

      expect(result.isSuccess, isFalse);
      expect(result.errorCode, AuthErrorCode.internalServerError);
    });

    test('offline fails fast with a network error', () async {
      when(() => connectivity.checkConnectivity())
          .thenAnswer((_) async => [ConnectivityResult.none]);

      final result =
          await service.verifyOtp(phoneNumber: _phone, otpCode: '123456');

      expect(result.errorCode, AuthErrorCode.networkError);
      expect(requests, isEmpty);
    });
  });

  group('resendOtp', () {
    test('returns the new expiry', () async {
      adapter.onPost(
        '/auth/resend-otp',
        (server) => server.reply(200, {
          'message': 'sent',
          'otpExpiresAt': '2026-09-25T11:00:00Z',
        }),
        data: {'phoneNumber': _phone},
      );

      final result = await service.resendOtp(phoneNumber: _phone);

      expect(result.isSuccess, isTrue);
      expect(result.otpRequired, isTrue);
      expect(result.otpExpiresAt, DateTime.utc(2026, 9, 25, 11));
    });

    test('maps AUTH_006 with retryAfterSeconds', () async {
      adapter.onPost(
        '/auth/resend-otp',
        (server) => server.reply(429, {
          'error': {
            'code': 'AUTH_006',
            'message': 'Too many requests',
            'retryAfterSeconds': 42,
          },
        }),
        data: Matchers.any,
      );

      final result = await service.resendOtp(phoneNumber: _phone);

      expect(result.isSuccess, isFalse);
      expect(result.errorCode, AuthErrorCode.rateLimitExceeded);
      expect(result.retryAfterSeconds, 42);
    });
  });

  group('loginOnline', () {
    test('sends device headers and reads Fayda status and newDevice', () async {
      adapter.onPost(
        '/auth/login',
        (server) => server.reply(200, {
          'tokens': _tokens(),
          'user': _user(faydaVerified: true),
          'newDevice': true,
        }),
        data: Matchers.any,
      );

      final result = await service.loginOnline(
        phoneNumber: _phone,
        password: 'Secret123',
      );

      expect(result.isSuccess, isTrue);
      expect(result.faydaVerified, isTrue);
      expect(result.faydaVerifiedAt, DateTime.utc(2026, 9, 1, 10));
      expect(result.newDevice, isTrue);
      final headers = requests.single.headers;
      expect(headers[DeviceIdentity.deviceIdHeader], 'device-abc');
      expect(headers[DeviceIdentity.deviceNameHeader], 'Android phone');
    });
  });

  group('unregisterDeviceToken', () {
    test('sends DELETE /device-tokens with the token', () async {
      adapter.onDelete(
        '/device-tokens',
        (server) => server.reply(204, null),
        data: {'token': 'fcm-1'},
      );

      await service.unregisterDeviceToken('fcm-1');

      expect(requests.single.method, 'DELETE');
      expect(requests.single.data, {'token': 'fcm-1'});
    });

    test('swallows server errors', () async {
      adapter.onDelete(
        '/device-tokens',
        (server) => server.reply(500, {'message': 'boom'}),
        data: Matchers.any,
      );

      await expectLater(service.unregisterDeviceToken('fcm-1'), completes);
    });

    test('skips an empty token', () async {
      await service.unregisterDeviceToken('');
      expect(requests, isEmpty);
    });
  });
}
