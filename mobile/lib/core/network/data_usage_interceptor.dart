import 'package:dio/dio.dart';

import 'package:wereda_connect/core/network/data_usage_tracker.dart';

/// Dio interceptor that measures request/response body sizes and feeds them
/// into [DataUsageTracker].
///
/// Attach to the Dio client via [ApiClient.initialize].
class DataUsageInterceptor extends Interceptor {
  final DataUsageTracker tracker;

  DataUsageInterceptor(this.tracker);

  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) {
    final uploadBytes = estimateBodyBytes(options.data);
    if (uploadBytes > 0) {
      tracker.addUploaded(uploadBytes);
    }
    handler.next(options);
  }

  @override
  void onResponse(
    Response<dynamic> response,
    ResponseInterceptorHandler handler,
  ) {
    _trackDownload(response);
    handler.next(response);
  }

  @override
  void onError(DioException err, ErrorInterceptorHandler handler) {
    // Also track bytes from error responses (e.g. 4xx/5xx).
    if (err.response != null) {
      _trackDownload(err.response!);
    }
    handler.next(err);
  }

  void _trackDownload(Response<dynamic> response) {
    final contentLength = response.headers.value('content-length');
    final parsed = contentLength != null ? int.tryParse(contentLength) : null;
    final downloadBytes = parsed ?? estimateBodyBytes(response.data);
    if (downloadBytes > 0) {
      tracker.addDownloaded(downloadBytes);
    }
  }
}
