import 'package:dio/dio.dart';

import 'package:wereda_connect/core/network/data_usage_tracker.dart';

/// Dio interceptor that measures request/response sizes (request line,
/// headers and body) and feeds them into [DataUsageTracker].
///
/// On a client with no AuthInterceptor (token refresh, the AuthInterceptor
/// retry client) one full interceptor is enough. On the main API client it
/// is split around the AuthInterceptor, see [DataUsageInterceptor.requests]
/// and [DataUsageInterceptor.responses].
class DataUsageInterceptor extends Interceptor {
  final DataUsageTracker tracker;
  final bool _meterRequests;
  final bool _meterResponses;

  /// Meters both directions.
  DataUsageInterceptor(this.tracker)
      : _meterRequests = true,
        _meterResponses = true;

  /// Meters requests only. Place it AFTER the AuthInterceptor so the
  /// injected bearer token is counted.
  DataUsageInterceptor.requests(this.tracker)
      : _meterRequests = true,
        _meterResponses = false;

  /// Meters responses and error responses only. Place it BEFORE the
  /// AuthInterceptor: that interceptor resolves a refreshed 401 without
  /// calling later interceptors, so a meter behind it would miss the 401 and
  /// would count a retried error response a second time (the retry client
  /// already meters it).
  DataUsageInterceptor.responses(this.tracker)
      : _meterRequests = false,
        _meterResponses = true;

  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) {
    if (_meterRequests) tracker.addUploaded(estimateRequestBytes(options));
    handler.next(options);
  }

  @override
  void onResponse(
    Response<dynamic> response,
    ResponseInterceptorHandler handler,
  ) {
    if (_meterResponses) _trackDownload(response);
    handler.next(response);
  }

  @override
  void onError(DioException err, ErrorInterceptorHandler handler) {
    // Also track bytes from error responses (e.g. 4xx/5xx).
    if (_meterResponses && err.response != null) {
      _trackDownload(err.response!);
    }
    handler.next(err);
  }

  void _trackDownload(Response<dynamic> response) {
    final contentLength = response.headers.value('content-length');
    final parsed = contentLength != null ? int.tryParse(contentLength) : null;
    final bodyBytes = parsed ?? estimateBodyBytes(response.data);
    tracker.addDownloaded(
      bodyBytes + estimateHeaderBytes(response.headers.map),
    );
  }
}
