import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/network/auth_interceptor.dart';
import 'package:wereda_connect/core/network/data_usage_interceptor.dart';
import 'package:wereda_connect/core/network/data_usage_tracker.dart';

/// Replies with a fixed body, status and headers, and records each request.
class _FixedAdapter implements HttpClientAdapter {
  _FixedAdapter(this.body, this.status, this.headers);

  final String body;
  final int status;
  final Map<String, List<String>> headers;
  final requests = <RequestOptions>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    return ResponseBody.fromString(body, status, headers: headers);
  }

  @override
  void close({bool force = false}) {}
}

/// 200-style reply (with [okStatus]) for [validBearer], 401 otherwise.
class _TokenAdapter implements HttpClientAdapter {
  _TokenAdapter(this.validBearer, {this.okStatus = 200});

  final String validBearer;
  final int okStatus;
  final requests = <RequestOptions>[];

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    if (options.headers['Authorization'] == 'Bearer $validBearer') {
      return ResponseBody.fromString(
        'x' * 1000,
        okStatus,
        headers: {
          'content-length': ['1000'],
        },
      );
    }
    return ResponseBody.fromString(
      'y' * 10,
      401,
      headers: {
        'content-length': ['10'],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

class _MapStorage implements FlutterSecureStorage {
  final values = <String, String>{};

  @override
  Future<String?> read({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async =>
      values[key];

  @override
  Future<void> write({
    required String key,
    required String? value,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    if (value == null) {
      values.remove(key);
    } else {
      values[key] = value;
    }
  }

  @override
  Future<void> delete({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async =>
      values.remove(key);

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

int _responseBytes(int contentLength) =>
    contentLength + estimateHeaderBytes({'content-length': '$contentLength'});

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late DataUsageTracker tracker;

  setUp(() async {
    SharedPreferences.setMockInitialValues({});
    tracker = DataUsageTracker();
    await tracker.initialize();
    tracker.setUser('user-a');
  });

  group('single client', () {
    Dio dioWith(HttpClientAdapter adapter) =>
        Dio(BaseOptions(baseUrl: 'https://api.test'))
          ..httpClientAdapter = adapter
          ..interceptors.add(DataUsageInterceptor(tracker));

    test('upload is request line + Host + headers + body', () async {
      final adapter = _FixedAdapter('{}', 200, {});
      final dio = dioWith(adapter);

      await dio.post<dynamic>(
        '/sync',
        data: {'k': 'v'},
        options: Options(headers: {'Authorization': 'Bearer abc'}),
      );

      final sent = adapter.requests.single;
      expect(sent.headers['Authorization'], 'Bearer abc');
      expect(
        tracker.bytesUploaded,
        'POST /sync HTTP/1.1\r\n'.length +
            'Host: api.test\r\n'.length +
            // Dio sets content-length after interceptors run, so the meter
            // cannot see it (a 20-byte-ish known underestimate).
            estimateHeaderBytes(
              Map.of(sent.headers)..remove(Headers.contentLengthHeader),
            ) +
            '{"k":"v"}'.length,
      );
    });

    test('uses content-length plus response headers for download', () async {
      final dio = dioWith(
        _FixedAdapter('{"a":1}', 200, {
          'content-length': ['1234'],
        }),
      );

      await dio.get<dynamic>('/crops');

      expect(tracker.bytesDownloaded, _responseBytes(1234));
    });

    test('falls back to the body size when content-length is missing',
        () async {
      final dio = dioWith(_FixedAdapter('hello', 200, {}))
        ..options.responseType = ResponseType.plain;

      await dio.get<dynamic>('/crops');

      expect(tracker.bytesDownloaded, 5);
    });

    test('counts error responses once', () async {
      final dio = dioWith(
        _FixedAdapter('{"message":"nope"}', 400, {
          'content-length': ['18'],
        }),
      );

      await expectLater(
        dio.get<dynamic>('/crops'),
        throwsA(isA<DioException>()),
      );

      expect(tracker.bytesDownloaded, _responseBytes(18));
    });

    test('counts nothing while signed out', () async {
      tracker.setUser(null);
      final dio = dioWith(_FixedAdapter('{}', 200, {}));

      await dio.post<dynamic>('/auth/login', data: {'p': 'x'});

      expect(tracker.totalBytes, 0);
    });
  });

  group('production wiring around AuthInterceptor', () {
    late _MapStorage storage;

    /// Mirrors main.dart: request meter added by ApiClient.initialize,
    /// AuthInterceptor inserted at 0, response meter inserted at 0 before
    /// it, full meter on the retry client.
    Dio wired(HttpClientAdapter adapter, {String? refreshTo}) {
      final dio = Dio(BaseOptions(baseUrl: 'https://api.test'))
        ..httpClientAdapter = adapter
        ..interceptors.add(DataUsageInterceptor.requests(tracker));
      final auth = AuthInterceptor(
        storage: storage,
        dio: dio,
        retryInterceptors: [DataUsageInterceptor(tracker)],
        refreshTokenCallback: (_) async => refreshTo,
      );
      dio.interceptors
        ..insert(0, auth)
        ..insert(0, DataUsageInterceptor.responses(tracker));
      return dio;
    }

    setUp(() => storage = _MapStorage());

    test('counts the injected bearer token on upload', () async {
      storage.values['id_token'] = 'tok';
      final adapter = _TokenAdapter('tok');
      final dio = wired(adapter);

      await dio.get<dynamic>('/crops');

      final sent = adapter.requests.single;
      expect(sent.headers['Authorization'], 'Bearer tok');
      expect(
        tracker.bytesUploaded,
        estimateRequestBytes(sent),
      );
      expect(
        tracker.bytesUploaded,
        greaterThan(
          'GET /crops HTTP/1.1\r\n'.length +
              'Authorization: Bearer tok\r\n'.length,
        ),
      );
      expect(tracker.bytesDownloaded, _responseBytes(1000));
    });

    test('401, refresh, retry: counts both requests and both responses once',
        () async {
      storage.values
        ..['id_token'] = 'old'
        ..['refresh_token'] = 'r';
      final adapter = _TokenAdapter('new');
      final dio = wired(adapter, refreshTo: 'new');

      final response = await dio.get<dynamic>('/crops');

      expect(response.statusCode, 200);
      expect(adapter.requests, hasLength(2));
      expect(
        tracker.bytesUploaded,
        adapter.requests.map(estimateRequestBytes).reduce((a, b) => a + b),
      );
      expect(
        tracker.bytesDownloaded,
        _responseBytes(10) + _responseBytes(1000),
      );
    });

    test('stored-token retry that errors is counted once, not twice', () async {
      // The request goes out with a stale bearer, then the stored token has
      // already been refreshed by the time its 401 is handled.
      storage.values
        ..['id_token'] = 'stale'
        ..['refresh_token'] = 'r';
      final adapter = _TokenAdapter('fresh', okStatus: 500);
      // Runs first on the error path, simulating a concurrent refresh.
      final dio = wired(adapter)
        ..interceptors.insert(
          0,
          InterceptorsWrapper(
            onError: (err, handler) {
              storage.values['id_token'] = 'fresh';
              handler.next(err);
            },
          ),
        );

      await expectLater(
        dio.get<dynamic>('/crops'),
        throwsA(
          isA<DioException>().having(
            (e) => e.response?.statusCode,
            'status',
            500,
          ),
        ),
      );

      expect(adapter.requests, hasLength(2));
      expect(
        tracker.bytesDownloaded,
        _responseBytes(10) + _responseBytes(1000),
      );
    });
  });
}
