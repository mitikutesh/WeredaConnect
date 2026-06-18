import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
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

      test('connection timeout throws TimeoutException', () async {
        // Arrange
        const path = '/slow';
        dioAdapter.onGet(
          path,
          (server) => server.throws(
            408,
            DioException(
              requestOptions: RequestOptions(path: path),
              type: DioExceptionType.connectionTimeout,
            ),
          ),
        );

        // Act & Assert
        expect(
          () => ApiClient().get<dynamic>(path),
          throwsA(isA<TimeoutException>()),
        );
      });

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
  });
}
