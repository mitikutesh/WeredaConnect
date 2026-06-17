import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';

/// Abstraction over platform connectivity for testability.
///
/// In production, use [PlatformConnectivitySource] which delegates to
/// the `connectivity_plus` plugin. In tests, provide a fake implementation
/// with controlled streams.
abstract class ConnectivitySource {
  /// Stream of connectivity state changes from the platform.
  Stream<List<ConnectivityResult>> get onConnectivityChanged;

  /// Check current connectivity state.
  Future<List<ConnectivityResult>> checkConnectivity();
}

/// Production implementation backed by [connectivity_plus].
class PlatformConnectivitySource implements ConnectivitySource {
  final Connectivity _connectivity = Connectivity();

  @override
  Stream<List<ConnectivityResult>> get onConnectivityChanged =>
      _connectivity.onConnectivityChanged;

  @override
  Future<List<ConnectivityResult>> checkConnectivity() =>
      _connectivity.checkConnectivity();
}

/// Monitors network connectivity and verifies actual internet access.
///
/// Provides two levels of connectivity checking:
/// 1. **Local check** via [isConnected] — uses OS-level connectivity info
///    (fast, no network traffic, but can report false positives)
/// 2. **Remote check** via [hasInternetAccess] — pings the API `/health`
///    endpoint to verify real internet (costs a tiny HTTP request)
///
/// The [onConnectivityChanged] stream is debounced by [debounceDuration]
/// to prevent rapid state toggling on flaky 3G networks common in
/// Ethiopian rural areas.
///
/// Ethiopian context:
/// - 3G networks frequently toggle connected/disconnected states
/// - Without debouncing, sync would trigger/abort many times per minute
/// - Health check catches "connected to Wi-Fi but no internet" scenarios
///
/// Usage by SyncExecutor:
/// ```dart
/// connectivityMonitor.onConnectivityChanged.listen((results) {
///   if (connectivityMonitor.isConnectedFromResults(results)) {
///     syncExecutor.syncNow();
///   }
/// });
/// ```
///
/// See: `specs/contracts/sync-protocol.md`
class ConnectivityMonitor {
  final ConnectivitySource _source;
  final Dio _dio;

  /// Debounce window to filter out rapid connectivity flickers.
  /// 2 seconds balances responsiveness with stability on 3G networks.
  static const Duration debounceDuration = Duration(seconds: 2);

  /// Timeout for the health check ping.
  /// 3 seconds gives slow 3G time to respond without excessive user wait.
  static const Duration healthCheckTimeout = Duration(seconds: 3);

  /// API health check endpoint path.
  static const String healthCheckPath = '/health';

  StreamController<List<ConnectivityResult>>? _controller;
  StreamSubscription<List<ConnectivityResult>>? _subscription;
  Timer? _debounceTimer;

  /// Creates a ConnectivityMonitor.
  ///
  /// Parameters:
  /// - [source]: Connectivity data source (defaults to [PlatformConnectivitySource])
  /// - [dio]: Dio HTTP client for health check requests
  ConnectivityMonitor({
    required Dio dio,
    ConnectivitySource? source,
  })  : _source = source ?? PlatformConnectivitySource(),
        _dio = dio;

  /// Stream of debounced connectivity state changes.
  ///
  /// Emits [List<ConnectivityResult>] after [debounceDuration] of stability.
  /// Multiple connectivity types can be active simultaneously (e.g., wifi + mobile).
  ///
  /// The stream is broadcast, allowing multiple listeners (e.g., SyncExecutor
  /// and UI indicators).
  Stream<List<ConnectivityResult>> get onConnectivityChanged {
    _controller ??= _createDebouncedController();
    return _controller!.stream;
  }

  /// Quick local check for network connectivity.
  ///
  /// Returns `true` if the device reports any network interface as connected
  /// (wifi, mobile data, ethernet, etc.). Does NOT verify actual internet access.
  ///
  /// This is a fast, zero-cost check suitable for gating sync attempts before
  /// the more expensive [hasInternetAccess] call.
  Future<bool> isConnected() async {
    final results = await _source.checkConnectivity();
    return isConnectedFromResults(results);
  }

  /// Determine connectivity from a list of [ConnectivityResult].
  ///
  /// Returns `true` if any result indicates a connected network interface.
  /// Useful for evaluating results from the [onConnectivityChanged] stream
  /// without making another platform call.
  bool isConnectedFromResults(List<ConnectivityResult> results) {
    if (results.isEmpty) return false;
    return !results.every((r) => r == ConnectivityResult.none);
  }

  /// Verify actual internet access by pinging the API health endpoint.
  ///
  /// Returns `true` if the `/health` endpoint responds with HTTP 200
  /// within [healthCheckTimeout]. Returns `false` on any error (timeout,
  /// network failure, non-200 status, etc.).
  ///
  /// This catches scenarios where:
  /// - Device is connected to Wi-Fi but the router has no internet
  /// - Mobile data is "connected" but the carrier is throttling/blocking
  /// - A captive portal is intercepting requests
  ///
  /// Cost: ~200 bytes per request — negligible even on expensive Ethiopian data plans.
  Future<bool> hasInternetAccess() async {
    try {
      final response = await _dio.get<dynamic>(
        healthCheckPath,
        options: Options(
          sendTimeout: healthCheckTimeout,
          receiveTimeout: healthCheckTimeout,
        ),
      );
      return response.statusCode == 200;
    } on Object catch (_) {
      return false;
    }
  }

  /// Release resources held by this monitor.
  ///
  /// Cancels the platform connectivity subscription, stops the debounce timer,
  /// and closes the stream controller. Call this when the monitor is no longer
  /// needed (e.g., app shutdown or scope disposal).
  void dispose() {
    _debounceTimer?.cancel();
    _debounceTimer = null;
    _subscription?.cancel();
    _subscription = null;
    _controller?.close();
    _controller = null;
  }

  /// Creates a broadcast StreamController that debounces raw connectivity events.
  ///
  /// Debouncing logic:
  /// 1. Raw event arrives from connectivity_plus
  /// 2. Cancel any pending debounce timer
  /// 3. Start a new timer for [debounceDuration]
  /// 4. When timer fires, emit the latest event to listeners
  ///
  /// This ensures only the final stable state is emitted after rapid toggling.
  StreamController<List<ConnectivityResult>> _createDebouncedController() {
    final controller = StreamController<List<ConnectivityResult>>.broadcast(
      onCancel: () {
        // Clean up when last listener unsubscribes. Reset all state so the
        // next listener (e.g. after a stop->start cycle) rebuilds a fresh
        // controller with a live upstream subscription instead of reusing
        // this dead one (the getter uses `_controller ??= ...`).
        _debounceTimer?.cancel();
        _debounceTimer = null;
        _subscription?.cancel();
        _subscription = null;
        _controller = null;
      },
    );

    _subscription = _source.onConnectivityChanged
        .listen((List<ConnectivityResult> results) {
      _debounceTimer?.cancel();
      _debounceTimer = Timer(debounceDuration, () {
        if (!controller.isClosed) {
          controller.add(results);
        }
      });
    });

    return controller;
  }
}
