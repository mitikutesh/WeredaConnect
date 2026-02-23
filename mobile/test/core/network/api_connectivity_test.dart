import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';

void main() {
  group('API Connectivity', () {
    late DioAdapter dioAdapter;

    setUp(() {
      ApiClient().initialize(environment: Environment.development);
      dioAdapter = DioAdapter(dio: ApiClient().dio);
    });

    group('Health check', () {
      test('health endpoint responds 200', () async {
        dioAdapter.onGet(
          '/health',
          (server) => server.reply(200, {'status': 'ok'}),
        );

        final result = await ApiClient().get<Map<String, dynamic>>('/health');

        expect(result['status'], equals('ok'));
      });
    });

    group('Auth endpoints', () {
      test('login endpoint responds 200 with tokens', () async {
        final requestData = {
          'phoneNumber': '+251911234567',
          'password': 'test1234',
        };
        final responseData = {
          'accessToken': 'at-123',
          'refreshToken': 'rt-123',
          'idToken': 'id-123',
          'expiresAt': '2026-03-01T00:00:00Z',
          'user': {
            'userId': 'user-001',
            'role': 'farmer',
            'name': 'Abebe',
            'woreda': 'Jimma',
          },
        };

        dioAdapter.onPost(
          '/auth/login',
          (server) => server.reply(200, responseData),
          data: requestData,
        );

        final result = await ApiClient().post<Map<String, dynamic>>(
          '/auth/login',
          data: requestData,
        );

        expect(result['accessToken'], equals('at-123'));
        expect(result['user'], isA<Map<String, dynamic>>());
      });

      test('register endpoint responds 200 with userId', () async {
        final requestData = {
          'phoneNumber': '+251911234567',
          'password': 'test1234',
          'displayName': 'Abebe Kebede',
          'role': 'farmer',
          'woreda': 'Jimma',
          'preferredLanguage': 'am',
        };
        final responseData = {'userId': 'user-002'};

        dioAdapter.onPost(
          '/auth/register',
          (server) => server.reply(200, responseData),
          data: requestData,
        );

        final result = await ApiClient().post<Map<String, dynamic>>(
          '/auth/register',
          data: requestData,
        );

        expect(result['userId'], equals('user-002'));
      });

      test('verify-otp endpoint responds 200 with tokens', () async {
        final requestData = {
          'phoneNumber': '+251911234567',
          'otp': '123456',
        };
        final responseData = {
          'accessToken': 'at-new',
          'refreshToken': 'rt-new',
          'idToken': 'id-new',
          'expiresAt': '2026-03-01T00:00:00Z',
          'userId': 'user-002',
        };

        dioAdapter.onPost(
          '/auth/verify-otp',
          (server) => server.reply(200, responseData),
          data: requestData,
        );

        final result = await ApiClient().post<Map<String, dynamic>>(
          '/auth/verify-otp',
          data: requestData,
        );

        expect(result['accessToken'], equals('at-new'));
        expect(result['userId'], equals('user-002'));
      });

      test('refresh endpoint responds 200 with new tokens', () async {
        final requestData = {'refreshToken': 'rt-123'};
        final responseData = {
          'accessToken': 'at-refreshed',
          'refreshToken': 'rt-refreshed',
          'idToken': 'id-refreshed',
          'expiresAt': '2026-03-02T00:00:00Z',
        };

        dioAdapter.onPost(
          '/auth/refresh',
          (server) => server.reply(200, responseData),
          data: requestData,
        );

        final result = await ApiClient().post<Map<String, dynamic>>(
          '/auth/refresh',
          data: requestData,
        );

        expect(result['accessToken'], equals('at-refreshed'));
      });

      test('logout endpoint responds 200', () async {
        dioAdapter.onPost(
          '/auth/logout',
          (server) => server.reply(200, {'message': 'Logged out'}),
          data: Matchers.any,
        );

        final result = await ApiClient().post<Map<String, dynamic>>(
          '/auth/logout',
          data: <String, dynamic>{},
        );

        expect(result['message'], equals('Logged out'));
      });
    });

    group('Escrow endpoints', () {
      test('escrow initiate responds 200', () async {
        final requestData = {
          'deliveryId': 'del-001',
          'buyerId': 'buyer-001',
          'farmerId': 'farmer-001',
          'amount': 5000.0,
        };
        final responseData = {
          'escrowId': 'esc-001',
          'status': 'initiated',
        };

        dioAdapter.onPost(
          '/escrow/initiate',
          (server) => server.reply(200, responseData),
          data: requestData,
        );

        final result = await ApiClient().post<Map<String, dynamic>>(
          '/escrow/initiate',
          data: requestData,
        );

        expect(result['escrowId'], equals('esc-001'));
        expect(result['status'], equals('initiated'));
      });

      test('escrow status responds 200', () async {
        final responseData = {
          'escrowId': 'esc-001',
          'status': 'escrowed',
          'amount': 5000.0,
        };

        dioAdapter.onGet(
          '/escrow/esc-001',
          (server) => server.reply(200, responseData),
        );

        final result =
            await ApiClient().get<Map<String, dynamic>>('/escrow/esc-001');

        expect(result['status'], equals('escrowed'));
      });

      test('escrow history responds 200', () async {
        final responseData = {
          'payments': <dynamic>[],
          'total': 0,
        };

        dioAdapter.onGet(
          '/escrow/history',
          (server) => server.reply(200, responseData),
        );

        final result =
            await ApiClient().get<Map<String, dynamic>>('/escrow/history');

        expect(result['payments'], isA<List>());
      });

      test('escrow dispute responds 200', () async {
        final requestData = {
          'escrowId': 'esc-001',
          'reason': 'damaged_goods',
          'description': 'Items were damaged',
        };
        final responseData = {
          'escrowId': 'esc-001',
          'status': 'disputed',
        };

        dioAdapter.onPost(
          '/escrow/dispute',
          (server) => server.reply(200, responseData),
          data: requestData,
        );

        final result = await ApiClient().post<Map<String, dynamic>>(
          '/escrow/dispute',
          data: requestData,
        );

        expect(result['status'], equals('disputed'));
      });
    });

    group('Sync endpoint', () {
      test('sync endpoint responds 200', () async {
        final requestData = {
          'operations': <dynamic>[],
          'lastSyncTimestamp': '2026-02-20T00:00:00Z',
        };
        final responseData = {
          'operations': <dynamic>[],
          'serverTimestamp': '2026-02-24T00:00:00Z',
        };

        dioAdapter.onPost(
          '/sync',
          (server) => server.reply(200, responseData),
          data: requestData,
        );

        final result = await ApiClient().post<Map<String, dynamic>>(
          '/sync',
          data: requestData,
        );

        expect(result['serverTimestamp'], isNotNull);
      });
    });

    group('Error scenarios', () {
      test('500 server error throws ServerException', () async {
        dioAdapter.onGet(
          '/auth/login',
          (server) => server.reply(
            500,
            {'message': 'Internal server error'},
          ),
        );

        expect(
          () => ApiClient().get<dynamic>('/auth/login'),
          throwsA(isA<ServerException>()),
        );
      });

      test('network timeout throws TimeoutException', () async {
        dioAdapter.onPost(
          '/auth/login',
          (server) => server.throws(
            408,
            DioException(
              requestOptions: RequestOptions(path: '/auth/login'),
              type: DioExceptionType.connectionTimeout,
            ),
          ),
          data: Matchers.any,
        );

        expect(
          () => ApiClient()
              .post<dynamic>('/auth/login', data: <String, dynamic>{}),
          throwsA(isA<TimeoutException>()),
        );
      });

      test('connection refused throws NetworkException', () async {
        dioAdapter.onPost(
          '/auth/login',
          (server) => server.throws(
            0,
            DioException(
              requestOptions: RequestOptions(path: '/auth/login'),
              type: DioExceptionType.connectionError,
            ),
          ),
          data: Matchers.any,
        );

        expect(
          () => ApiClient()
              .post<dynamic>('/auth/login', data: <String, dynamic>{}),
          throwsA(isA<NetworkException>()),
        );
      });

      test('429 rate limiting throws RateLimitException', () async {
        dioAdapter.onPost(
          '/auth/login',
          (server) => server.reply(
            429,
            {'message': 'Too many requests'},
          ),
          data: Matchers.any,
        );

        expect(
          () => ApiClient()
              .post<dynamic>('/auth/login', data: <String, dynamic>{}),
          throwsA(isA<RateLimitException>()),
        );
      });

      test('401 auth error throws AuthException', () async {
        dioAdapter.onGet(
          '/escrow/history',
          (server) => server.reply(
            401,
            {'message': 'Unauthorized'},
          ),
        );

        expect(
          () => ApiClient().get<dynamic>('/escrow/history'),
          throwsA(isA<AuthException>()),
        );
      });

      test('400 validation error throws ValidationException', () async {
        dioAdapter.onPost(
          '/auth/register',
          (server) => server.reply(
            400,
            {'message': 'Invalid phone format'},
          ),
          data: Matchers.any,
        );

        expect(
          () => ApiClient()
              .post<dynamic>('/auth/register', data: <String, dynamic>{}),
          throwsA(isA<ValidationException>()),
        );
      });

      test('404 not found throws NotFoundException', () async {
        dioAdapter.onGet(
          '/escrow/nonexistent',
          (server) => server.reply(
            404,
            {'message': 'Escrow not found'},
          ),
        );

        expect(
          () => ApiClient().get<dynamic>('/escrow/nonexistent'),
          throwsA(isA<NotFoundException>()),
        );
      });

      test('409 conflict throws ConflictException', () async {
        dioAdapter.onPost(
          '/auth/register',
          (server) => server.reply(
            409,
            {'message': 'Phone already registered'},
          ),
          data: Matchers.any,
        );

        expect(
          () => ApiClient()
              .post<dynamic>('/auth/register', data: <String, dynamic>{}),
          throwsA(isA<ConflictException>()),
        );
      });

      test('exception contains correct message from response', () async {
        const errorMessage = 'Rate limit exceeded, retry in 60s';
        dioAdapter.onPost(
          '/auth/login',
          (server) => server.reply(
            429,
            {'message': errorMessage},
          ),
          data: Matchers.any,
        );

        try {
          await ApiClient()
              .post<dynamic>('/auth/login', data: <String, dynamic>{});
          fail('Should have thrown exception');
        } on Object catch (e) {
          expect(e, isA<RateLimitException>());
          expect(
            (e as RateLimitException).message,
            equals(errorMessage),
          );
        }
      });
    });
  });
}
