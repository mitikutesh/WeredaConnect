import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/core/network/api_exception.dart';

void main() {
  group('ApiClient', () {
    late DioAdapter dioAdapter;

    setUp(() {
      // Initialize ApiClient with development environment
      ApiClient().initialize(environment: Environment.development);

      // Create mock adapter for the ApiClient's dio instance
      dioAdapter = DioAdapter(dio: ApiClient().dio);
    });

    group('app build header (FUP-61)', () {
      test('every request carries X-App-Build with this build', () async {
        final sent = <Object?>[];
        final recorder = InterceptorsWrapper(
          onRequest: (options, handler) {
            sent.add(options.headers['X-App-Build']);
            handler.next(options);
          },
        );
        ApiClient().dio.interceptors.add(recorder);
        final body = <String, dynamic>{};
        dioAdapter
          ..onGet('/escrow/history', (server) => server.reply(200, body))
          ..onPost('/sync', (server) => server.reply(200, body), data: body);

        try {
          await ApiClient().get<Map<String, dynamic>>('/escrow/history');
          await ApiClient().post<Map<String, dynamic>>('/sync', data: body);
        } finally {
          ApiClient().dio.interceptors.remove(recorder);
        }

        const build = '${ApiConfig.appBuild}';
        expect(sent, [build, build]);
        expect(ApiConfig.appBuildHeader, 'X-App-Build');
      });

      test('appBuild is the build number in pubspec.yaml', () {
        final pubspec = File('pubspec.yaml').readAsStringSync();
        final match = RegExp(r'^version:\s*\S+\+(\d+)\s*$', multiLine: true)
            .firstMatch(pubspec);

        expect(match, isNotNull, reason: 'pubspec version has no +build');
        expect(int.parse(match!.group(1)!), ApiConfig.appBuild);
      });
    });

    group('GET requests', () {
      test('successful GET request returns parsed data', () async {
        // Arrange
        const path = '/users/123';
        final responseData = {'id': 123, 'name': 'Test User'};

        dioAdapter.onGet(
          path,
          (server) => server.reply(200, responseData),
        );

        // Act
        final result = await ApiClient().get<Map<String, dynamic>>(path);

        // Assert
        expect(result, equals(responseData));
      });

      test('GET request with query parameters', () async {
        // Arrange
        const path = '/users';
        final queryParams = {'page': 1, 'limit': 10};
        final responseData = {'users': <dynamic>[]};

        dioAdapter.onGet(
          path,
          (server) => server.reply(200, responseData),
          queryParameters: queryParams,
        );

        // Act
        final result = await ApiClient().get<Map<String, dynamic>>(
          path,
          queryParameters: queryParams,
        );

        // Assert
        expect(result, equals(responseData));
      });

      test('identical concurrent GETs share one network request (SIM-21)',
          () async {
        const path = '/trips';
        var requests = 0;
        final counter = InterceptorsWrapper(
          onRequest: (options, handler) {
            requests++;
            handler.next(options);
          },
        );
        ApiClient().dio.interceptors.add(counter);
        dioAdapter.onGet(
          path,
          (server) => server.reply(200, {'trips': <dynamic>[]}),
          queryParameters: {'truckerId': 't1', 'limit': '50'},
        );

        try {
          // Same query in a different key order is the same request.
          final results = await Future.wait([
            ApiClient().get<Map<String, dynamic>>(
              path,
              queryParameters: {'truckerId': 't1', 'limit': '50'},
            ),
            ApiClient().get<Map<String, dynamic>>(
              path,
              queryParameters: {'limit': '50', 'truckerId': 't1'},
            ),
          ]);
          expect(results[0], equals(results[1]));
          expect(requests, 1);

          // Once settled nothing is cached: the next read goes out again.
          await ApiClient().get<Map<String, dynamic>>(
            path,
            queryParameters: {'truckerId': 't1', 'limit': '50'},
          );
          expect(requests, 2);
        } finally {
          ApiClient().dio.interceptors.remove(counter);
        }
      });

      test('a rejected certificate is a SecureConnectionException (SIM-23)',
          () async {
        const path = '/pinned';
        dioAdapter.onGet(
          path,
          (server) => server.throws(
            0,
            DioException(
              requestOptions: RequestOptions(path: path),
              type: DioExceptionType.badCertificate,
            ),
          ),
        );

        await expectLater(
          ApiClient().get<Map<String, dynamic>>(path),
          throwsA(isA<SecureConnectionException>()),
        );
      });

      test('GET request with custom headers', () async {
        // Arrange
        const path = '/data';
        final headers = {'X-Custom-Header': 'test-value'};
        final responseData = {'data': 'test'};

        dioAdapter.onGet(
          path,
          (server) => server.reply(200, responseData),
          headers: headers,
        );

        // Act
        final result = await ApiClient().get<Map<String, dynamic>>(
          path,
          headers: headers,
        );

        // Assert
        expect(result, equals(responseData));
      });

      test('GET request with custom parser', () async {
        // Arrange
        const path = '/user';
        final responseData = {'id': 1, 'name': 'John'};

        dioAdapter.onGet(
          path,
          (server) => server.reply(200, responseData),
        );

        // Act
        final result = await ApiClient().get<String>(
          path,
          parser: (data) => data['name'] as String,
        );

        // Assert
        expect(result, equals('John'));
      });
    });

    group('POST requests', () {
      test('successful POST request with body', () async {
        // Arrange
        const path = '/users';
        final requestData = {'name': 'New User', 'email': 'test@example.com'};
        final responseData = {'id': 456, ...requestData};

        dioAdapter.onPost(
          path,
          (server) => server.reply(201, responseData),
          data: requestData,
        );

        // Act
        final result = await ApiClient().post<Map<String, dynamic>>(
          path,
          data: requestData,
        );

        // Assert
        expect(result, equals(responseData));
      });
    });

    group('PUT requests', () {
      test('successful PUT request updates resource', () async {
        // Arrange
        const path = '/users/123';
        final requestData = {'name': 'Updated User'};
        final responseData = {'id': 123, ...requestData};

        dioAdapter.onPut(
          path,
          (server) => server.reply(200, responseData),
          data: requestData,
        );

        // Act
        final result = await ApiClient().put<Map<String, dynamic>>(
          path,
          data: requestData,
        );

        // Assert
        expect(result, equals(responseData));
      });
    });

    group('DELETE requests', () {
      test('successful DELETE request', () async {
        // Arrange
        const path = '/users/123';
        final responseData = {'message': 'User deleted'};

        dioAdapter.onDelete(
          path,
          (server) => server.reply(200, responseData),
        );

        // Act
        final result = await ApiClient().delete<Map<String, dynamic>>(path);

        // Assert
        expect(result, equals(responseData));
      });
    });

    group('PATCH requests', () {
      test('successful PATCH request', () async {
        // Arrange
        const path = '/users/123';
        final requestData = {'status': 'active'};
        final responseData = {'id': 123, 'status': 'active'};

        dioAdapter.onPatch(
          path,
          (server) => server.reply(200, responseData),
          data: requestData,
        );

        // Act
        final result = await ApiClient().patch<Map<String, dynamic>>(
          path,
          data: requestData,
        );

        // Assert
        expect(result, equals(responseData));
      });
    });

    group('Error handling', () {
      test('400 error throws ValidationException', () async {
        // Arrange
        const path = '/error400';
        dioAdapter.onPost(
          path,
          (server) => server.reply(400, {'message': 'Invalid input'}),
          data: Matchers.any,
        );

        // Act & Assert
        await expectLater(
          () => ApiClient().post<dynamic>(path, data: <String, dynamic>{}),
          throwsA(isA<ValidationException>()),
        );
      });

      test('401 error throws AuthException', () async {
        // Arrange
        const path = '/protected';
        dioAdapter.onGet(
          path,
          (server) => server.reply(
            401,
            {'message': 'Unauthorized'},
          ),
        );

        // Act & Assert
        expect(
          () => ApiClient().get<dynamic>(path),
          throwsA(isA<AuthException>()),
        );
      });

      test('404 error throws NotFoundException', () async {
        // Arrange
        const path = '/users/999';
        dioAdapter.onGet(
          path,
          (server) => server.reply(
            404,
            {'message': 'User not found'},
          ),
        );

        // Act & Assert
        expect(
          () => ApiClient().get<dynamic>(path),
          throwsA(isA<NotFoundException>()),
        );
      });

      test('409 error throws ConflictException', () async {
        // Arrange
        const path = '/error409';
        dioAdapter.onPost(
          path,
          (server) => server.reply(409, {'message': 'User already exists'}),
          data: Matchers.any,
        );

        // Act & Assert
        await expectLater(
          () => ApiClient().post<dynamic>(path, data: <String, dynamic>{}),
          throwsA(isA<ConflictException>()),
        );
      });

      test('nested error envelope yields the message and code (SIM-24)',
          () async {
        const path = '/escrow/initiate';
        dioAdapter.onPost(
          path,
          (server) => server.reply(409, {
            'error': {
              'code': 'ESCROW_024',
              'message': 'Funds for this order are already held in escrow.',
              'field': 'orderId',
            },
          }),
          data: Matchers.any,
        );

        await expectLater(
          () => ApiClient().post<dynamic>(path, data: <String, dynamic>{}),
          throwsA(
            isA<ConflictException>()
                .having((e) => e.code, 'code', 'ESCROW_024')
                .having(
                  (e) => e.message,
                  'message',
                  'Funds for this order are already held in escrow.',
                ),
          ),
        );
      });

      test('nested 400 envelope keeps its ESCROW_001 code', () async {
        const path = '/escrow/initiate-400';
        dioAdapter.onPost(
          path,
          (server) => server.reply(400, {
            'error': {'code': 'ESCROW_001', 'message': 'Invalid amount.'},
          }),
          data: Matchers.any,
        );

        await expectLater(
          () => ApiClient().post<dynamic>(path, data: <String, dynamic>{}),
          throwsA(
            isA<ValidationException>()
                .having((e) => e.code, 'code', 'ESCROW_001')
                .having((e) => e.message, 'message', 'Invalid amount.'),
          ),
        );
      });

      test('flat message body has no code', () async {
        const path = '/flat409';
        dioAdapter.onPost(
          path,
          (server) => server.reply(409, {'message': 'Conflict'}),
          data: Matchers.any,
        );

        await expectLater(
          () => ApiClient().post<dynamic>(path, data: <String, dynamic>{}),
          throwsA(
            isA<ConflictException>()
                .having((e) => e.code, 'code', isNull)
                .having((e) => e.message, 'message', 'Conflict'),
          ),
        );
      });

      test('429 error throws RateLimitException', () async {
        // Arrange
        const path = '/api';
        dioAdapter.onGet(
          path,
          (server) => server.reply(
            429,
            {'message': 'Too many requests'},
          ),
        );

        // Act & Assert
        expect(
          () => ApiClient().get<dynamic>(path),
          throwsA(isA<RateLimitException>()),
        );
      });

      test('500 error throws ServerException', () async {
        // Arrange
        const path = '/api';
        dioAdapter.onGet(
          path,
          (server) => server.reply(
            500,
            {'message': 'Internal server error'},
          ),
        );

        // Act & Assert
        expect(
          () => ApiClient().get<dynamic>(path),
          throwsA(isA<ServerException>()),
        );
      });

      // Every Dio timeout, including transformTimeout (dio 5.11.1), is the
      // same TimeoutException to the app.
      for (final type in [
        DioExceptionType.connectionTimeout,
        DioExceptionType.sendTimeout,
        DioExceptionType.receiveTimeout,
        DioExceptionType.transformTimeout,
      ]) {
        test('${type.name} throws TimeoutException', () async {
          // Arrange
          const path = '/slow';
          dioAdapter.onGet(
            path,
            (server) => server.throws(
              408,
              DioException(
                requestOptions: RequestOptions(path: path),
                type: type,
              ),
            ),
          );

          // Act & Assert
          expect(
            () => ApiClient().get<dynamic>(path),
            throwsA(isA<TimeoutException>()),
          );
        });
      }

      test('network error throws NetworkException', () async {
        // Arrange
        const path = '/offline';
        dioAdapter.onGet(
          path,
          (server) => server.throws(
            0,
            DioException(
              requestOptions: RequestOptions(path: path),
              type: DioExceptionType.connectionError,
            ),
          ),
        );

        // Act & Assert
        expect(
          () => ApiClient().get<dynamic>(path),
          throwsA(isA<NetworkException>()),
        );
      });

      test('extracts error message from response data', () async {
        // Arrange
        const path = '/error';
        const errorMessage = 'Custom error message';
        dioAdapter.onGet(
          path,
          (server) => server.reply(
            400,
            {'message': errorMessage},
          ),
        );

        // Act & Assert
        try {
          await ApiClient().get<dynamic>(path);
          fail('Should have thrown exception');
        } on Object catch (e) {
          expect(e, isA<ValidationException>());
          expect((e as ValidationException).message, equals(errorMessage));
        }
      });
    });

    group('Initialization', () {
      test('initialize sets up client correctly', () {
        // Arrange & Act - using singleton already initialized in setUp
        final testClient = ApiClient();

        // Assert
        expect(testClient.isInitialized, isTrue);
        // SEC-06: baseUrl is now provided via --dart-define only (no hardcoded default),
        // so in test environment it will be empty unless explicitly configured.
        expect(testClient.dio.options.baseUrl, isA<String>());
        expect(
          testClient.dio.options.connectTimeout,
          equals(const Duration(seconds: 30)),
        );
        expect(
          testClient.dio.options.receiveTimeout,
          equals(const Duration(seconds: 30)),
        );
      });

      test('initialize is idempotent (calling twice has no effect)', () {
        // Arrange
        final testClient = ApiClient();
        testClient.initialize(environment: Environment.development);
        final originalBaseUrl = testClient.dio.options.baseUrl;

        // Act - try to initialize again with different config
        testClient.initialize(environment: Environment.staging);

        // Assert - should still have original config
        expect(testClient.dio.options.baseUrl, equals(originalBaseUrl));
      });
    });
  });

  group('ensureSyncSucceeded', () {
    // Web has no sync queue, so a deferred op (nothing written) is a failure.
    test('throws SyncDeferredException on a deferred operation', () {
      final response = {
        'results': [
          {
            'operationId': 'op-1',
            'status': 'deferred',
            'message': 'DELIVERY_NONCE_PENDING: Waiting for the QR code.',
          },
        ],
      };

      expect(
        () => ensureSyncSucceeded(response),
        throwsA(isA<SyncDeferredException>()),
      );
    });

    test('throws SyncRejectedException with the server message on error', () {
      final response = {
        'syncBatchId': 'batch-1',
        'results': [
          {
            'operationId': 'op-1',
            'status': 'error',
            'message': 'CROP_UNAUTHORIZED: Only farmers can create listings.',
          },
        ],
      };

      expect(
        () => ensureSyncSucceeded(response),
        throwsA(
          isA<SyncRejectedException>().having(
            (e) => e.message,
            'message',
            contains('CROP_UNAUTHORIZED'),
          ),
        ),
      );
    });

    test('does not throw when every operation succeeds', () {
      final response = {
        'syncBatchId': 'batch-1',
        'results': [
          {'operationId': 'op-1', 'status': 'success', 'serverVersion': 1},
        ],
      };

      expect(() => ensureSyncSucceeded(response), returnsNormally);
    });

    test('throws if any operation in a batch failed', () {
      final response = {
        'results': [
          {'operationId': 'op-1', 'status': 'success'},
          {'operationId': 'op-2', 'status': 'error', 'message': 'BOOM'},
        ],
      };

      expect(
        () => ensureSyncSucceeded(response),
        throwsA(isA<SyncRejectedException>()),
      );
    });

    test('is a no-op when results are missing or malformed', () {
      expect(() => ensureSyncSucceeded(<String, dynamic>{}), returnsNormally);
      expect(
        () => ensureSyncSucceeded({'results': 'not-a-list'}),
        returnsNormally,
      );
    });

    // ESC-12: a version race wrote nothing, so web must not report success.
    test('throws SyncConflictException on a conflict', () {
      final response = {
        'results': [
          {
            'operationId': 'op-1',
            'status': 'conflict',
            'serverVersion': 4,
            'message': 'Version conflict.',
          },
        ],
      };

      expect(
        () => ensureSyncSucceeded(response),
        throwsA(
          isA<SyncConflictException>()
              .having((e) => e.message, 'message', 'Version conflict.'),
        ),
      );
      // Callers that catch SyncRejectedException still see it.
      expect(
        () => ensureSyncSucceeded(response),
        throwsA(isA<SyncRejectedException>()),
      );
    });
  });

  group('isRevertableRejectionMessage', () {
    test('every ORDER_* code but ORDER_KYC_REQUIRED', () {
      for (final message in [
        'ORDER_VALIDATION: Invalid status transition.',
        'ORDER_UNAUTHORIZED: You can only update orders you are a participant '
            'in.',
        'ORDER_DEADLINE_PASSED: too late',
        'ORDER_ACTION_NOT_ALLOWED: no',
        'ORDER_PAYMENT_METHOD_UNAVAILABLE: Bank transfer (cbe_transfer) is not '
            'available yet. Pay with Telebirr.',
      ]) {
        expect(isRevertableRejectionMessage(message), isTrue, reason: message);
      }
      expect(
        isRevertableRejectionMessage('ORDER_KYC_REQUIRED: buyer_fayda: x'),
        isFalse,
      );
    });

    test('nothing else, and only on the prefix', () {
      for (final message in [
        null,
        '',
        'Order not found.',
        'FRAUD_VELOCITY_EXCEEDED: slow down',
        'CROP_UNAUTHORIZED: no',
        'AUTH_040: step-up required',
        'x ORDER_VALIDATION',
      ]) {
        expect(
          isRevertableRejectionMessage(message),
          isFalse,
          reason: '$message',
        );
      }
    });
  });
}
