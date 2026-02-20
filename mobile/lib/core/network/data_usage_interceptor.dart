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
      Response<dynamic> response, ResponseInterceptorHandler handler) {
    // Prefer Content-Length header for accuracy; fall back to body estimate.
    final contentLength = response.headers.value('content-length');
    final parsed = contentLength != null ? int.tryParse(contentLength) : null;
    final downloadBytes = parsed ?? estimateBodyBytes(response.data);
    if (downloadBytes > 0) {
      tracker.addDownloaded(downloadBytes);
    }
    handler.next(response);
  }
}
