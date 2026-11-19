import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';

/// Fake [ConnectivitySource] for test-driven connectivity events.
class FakeConnectivitySource implements ConnectivitySource {
  final StreamController<List<ConnectivityResult>> controller =
      StreamController<List<ConnectivityResult>>.broadcast();

  List<ConnectivityResult> _currentResults = [ConnectivityResult.none];

  /// Set what [checkConnectivity] will return.
  set currentResults(List<ConnectivityResult> results) {
    _currentResults = results;
  }

  @override
  Stream<List<ConnectivityResult>> get onConnectivityChanged =>
      controller.stream;

  @override
  Future<List<ConnectivityResult>> checkConnectivity() async {
    return _currentResults;
  }

  void dispose() {
    controller.close();
  }
}

/// A network that never connects: the request stays open until it is
/// cancelled.
class _NeverAnswers implements HttpClientAdapter {
  bool cancelled = false;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<List<int>>? requestStream,
    Future<void>? cancelFuture,
  ) {
    cancelFuture?.then((_) => cancelled = true);
    return Completer<ResponseBody>().future;
  }

  @override
  void close({bool force = false}) {}
}

void main() {
  group('ConnectivityMonitor', () {
    late FakeConnectivitySource fakeSource;
    late Dio dio;
    late DioAdapter dioAdapter;
    late ConnectivityMonitor monitor;

    setUp(() {
      fakeSource = FakeConnectivitySource();
      dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
      dioAdapter = DioAdapter(dio: dio);
      monitor = ConnectivityMonitor(
        source: fakeSource,
        dio: dio,
      );
    });

    tearDown(() {
      monitor.dispose();
      fakeSource.dispose();
    });

    group('isConnected', () {
      test('returns true when wifi is connected', () async {
        fakeSource.currentResults = [ConnectivityResult.wifi];

        final result = await monitor.isConnected();

        expect(result, isTrue);
      });

      test('returns true when mobile data is connected', () async {
        fakeSource.currentResults = [ConnectivityResult.mobile];

        final result = await monitor.isConnected();

        expect(result, isTrue);
      });

      test('returns true when multiple interfaces are active', () async {
        fakeSource.currentResults = [
          ConnectivityResult.wifi,
          ConnectivityResult.mobile,
        ];

        final result = await monitor.isConnected();

        expect(result, isTrue);
      });

      test('returns false when disconnected', () async {
        fakeSource.currentResults = [ConnectivityResult.none];

        final result = await monitor.isConnected();

        expect(result, isFalse);
      });

      test('returns true for ethernet connection', () async {
        fakeSource.currentResults = [ConnectivityResult.ethernet];

        final result = await monitor.isConnected();

        expect(result, isTrue);
      });
    });

    group('isConnectedFromResults', () {
      test('returns false for empty results', () {
        expect(monitor.isConnectedFromResults([]), isFalse);
      });

      test('returns false when only none', () {
        expect(
          monitor.isConnectedFromResults([ConnectivityResult.none]),
          isFalse,
        );
      });

      test('returns true when wifi present alongside none', () {
        expect(
          monitor.isConnectedFromResults([
            ConnectivityResult.none,
            ConnectivityResult.wifi,
          ]),
          isTrue,
        );
      });
    });

    group('hasInternetAccess', () {
      test(
          'BUG-35: a check that never connects ends at the deadline as no '
          'internet, and the request is cancelled', () async {
        final never = _NeverAnswers();
        final slowDio = Dio(
          BaseOptions(
            baseUrl: 'https://api.test.com',
            // The client's own connect timeout, far beyond the deadline.
            connectTimeout: const Duration(seconds: 30),
          ),
        )..httpClientAdapter = never;
        final slow = ConnectivityMonitor(
          source: fakeSource,
          dio: slowDio,
          healthCheckDeadline: const Duration(milliseconds: 50),
        );
        final watch = Stopwatch()..start();

        expect(await slow.hasInternetAccess(), isFalse);

        expect(watch.elapsed, lessThan(const Duration(seconds: 5)));
        expect(never.cancelled, isTrue);
        slow.dispose();
      });

      test('the deadline is 10 seconds', () {
        expect(
          ConnectivityMonitor.healthCheckDeadline,
          const Duration(seconds: 10),
        );
      });

      test('returns true when /health responds with 200', () async {
        dioAdapter.onGet(
          '/health',
          (server) => server.reply(200, {'status': 'ok'}),
        );

        final result = await monitor.hasInternetAccess();

        expect(result, isTrue);
      });

      test('returns false when /health responds with non-200', () async {
        dioAdapter.onGet(
          '/health',
          (server) => server.reply(503, {'status': 'unavailable'}),
        );

        final result = await monitor.hasInternetAccess();

        expect(result, isFalse);
      });

      test('returns false on connection timeout', () async {
        dioAdapter.onGet(
          '/health',
          (server) => server.throws(
            0,
            DioException(
              requestOptions: RequestOptions(path: '/health'),
              type: DioExceptionType.connectionTimeout,
            ),
          ),
        );

        final result = await monitor.hasInternetAccess();

        expect(result, isFalse);
      });

      test('returns false on network error', () async {
        dioAdapter.onGet(
          '/health',
          (server) => server.throws(
            0,
            DioException(
              requestOptions: RequestOptions(path: '/health'),
              type: DioExceptionType.connectionError,
            ),
          ),
        );

        final result = await monitor.hasInternetAccess();

        expect(result, isFalse);
      });

      test('returns false on receive timeout', () async {
        dioAdapter.onGet(
          '/health',
          (server) => server.throws(
            0,
            DioException(
              requestOptions: RequestOptions(path: '/health'),
              type: DioExceptionType.receiveTimeout,
            ),
          ),
        );

        final result = await monitor.hasInternetAccess();

        expect(result, isFalse);
      });
    });

    group('onConnectivityChanged', () {
      test('emits connectivity state changes after debounce', () async {
        final events = <List<ConnectivityResult>>[];
        final subscription = monitor.onConnectivityChanged.listen(events.add);

        // Emit a connectivity change
        fakeSource.controller.add([ConnectivityResult.wifi]);

        // Wait for debounce to settle (2s + buffer)
        await Future<void>.delayed(const Duration(milliseconds: 2200));

        expect(events, hasLength(1));
        expect(events[0], [ConnectivityResult.wifi]);

        await subscription.cancel();
      });

      test('debounces rapid changes and only emits the last value', () async {
        final events = <List<ConnectivityResult>>[];
        final subscription = monitor.onConnectivityChanged.listen(events.add);

        // Rapid toggles simulating 3G flicker
        fakeSource.controller.add([ConnectivityResult.mobile]);
        await Future<void>.delayed(const Duration(milliseconds: 500));
        fakeSource.controller.add([ConnectivityResult.none]);
        await Future<void>.delayed(const Duration(milliseconds: 500));
        fakeSource.controller.add([ConnectivityResult.mobile]);

        // Wait for debounce to settle
        await Future<void>.delayed(const Duration(milliseconds: 2200));

        // Only the last stable value should be emitted
        expect(events, hasLength(1));
        expect(events[0], [ConnectivityResult.mobile]);

        await subscription.cancel();
      });

      test('emits multiple events when changes are spaced apart', () async {
        final events = <List<ConnectivityResult>>[];
        final subscription = monitor.onConnectivityChanged.listen(events.add);

        // First change
        fakeSource.controller.add([ConnectivityResult.wifi]);
        await Future<void>.delayed(const Duration(milliseconds: 2200));

        // Second change after debounce settled
        fakeSource.controller.add([ConnectivityResult.none]);
        await Future<void>.delayed(const Duration(milliseconds: 2200));

        expect(events, hasLength(2));
        expect(events[0], [ConnectivityResult.wifi]);
        expect(events[1], [ConnectivityResult.none]);

        await subscription.cancel();
      });
    });

    group('dispose', () {
      test('stops emitting events after dispose', () async {
        final events = <List<ConnectivityResult>>[];
        final subscription = monitor.onConnectivityChanged.listen(events.add);

        monitor.dispose();

        // This should not cause errors or emit events
        fakeSource.controller.add([ConnectivityResult.wifi]);
        await Future<void>.delayed(const Duration(milliseconds: 2200));

        expect(events, isEmpty);

        await subscription.cancel();
      });
    });

    group('constants', () {
      test('debounce duration is 2 seconds', () {
        expect(
          ConnectivityMonitor.debounceDuration,
          const Duration(seconds: 2),
        );
      });

      test('health check timeout is 3 seconds', () {
        expect(
          ConnectivityMonitor.healthCheckTimeout,
          const Duration(seconds: 3),
        );
      });

      test('health check path is /health', () {
        expect(ConnectivityMonitor.healthCheckPath, '/health');
      });
    });
  });
}
