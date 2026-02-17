/// Dio interceptor for request/response logging in debug mode
library;

import 'dart:developer' as developer;
import 'package:dio/dio.dart';

/// Interceptor that logs HTTP requests and responses for debugging
///
/// This interceptor:
/// - Logs request details (method, URL, headers, body)
/// - Logs response details (status, headers, body)
/// - Logs errors with full context
/// - Only active in debug mode (respects environment config)
///
/// Ethiopian context:
/// - Minimal performance impact (uses dart:developer log)
/// - Helps debug 3G connectivity issues
/// - Truncates large responses to prevent log overflow
class LoggingInterceptor extends Interceptor {
  LoggingInterceptor({
    this.logRequests = true,
    this.logResponses = true,
    this.logErrors = true,
    this.maxBodyLength = 1000,
  });

  /// Whether to log outgoing requests
  final bool logRequests;

  /// Whether to log incoming responses
  final bool logResponses;

  /// Whether to log errors
  final bool logErrors;

  /// Maximum body length to log (prevents huge JSON dumps)
  final int maxBodyLength;

  @override
  void onRequest(RequestOptions options, RequestInterceptorHandler handler) {
    if (logRequests) {
      final buffer = StringBuffer();
      buffer.writeln('┌─────────────────────────────────────────────');
      buffer.writeln('│ REQUEST');
      buffer.writeln('├─────────────────────────────────────────────');
      buffer.writeln('│ ${options.method} ${options.uri}');

      if (options.headers.isNotEmpty) {
        buffer.writeln('│ Headers:');
        options.headers.forEach((key, value) {
          // Mask sensitive headers
          final maskedValue = _maskSensitiveHeader(key, value.toString());
          buffer.writeln('│   $key: $maskedValue');
        });
      }

      if (options.queryParameters.isNotEmpty) {
        buffer.writeln('│ Query Parameters:');
        options.queryParameters.forEach((key, value) {
          buffer.writeln('│   $key: $value');
        });
      }

      if (options.data != null) {
        final body = _truncateBody(options.data.toString());
        buffer.writeln('│ Body: $body');
      }

      buffer.writeln('└─────────────────────────────────────────────');
      developer.log(buffer.toString(), name: 'ApiClient');
    }

    handler.next(options);
  }

  @override
  void onResponse(
    Response<dynamic> response,
    ResponseInterceptorHandler handler,
  ) {
    if (logResponses) {
      final buffer = StringBuffer();
      buffer.writeln('┌─────────────────────────────────────────────');
      buffer.writeln('│ RESPONSE');
      buffer.writeln('├─────────────────────────────────────────────');
      buffer.writeln(
        '│ ${response.requestOptions.method} ${response.requestOptions.uri}',
      );
      buffer.writeln(
        '│ Status: ${response.statusCode} ${response.statusMessage}',
      );

      if (response.headers.map.isNotEmpty) {
        buffer.writeln('│ Headers:');
        response.headers.map.forEach((key, values) {
          buffer.writeln('│   $key: ${values.join(', ')}');
        });
      }

      if (response.data != null) {
        final body = _truncateBody(response.data.toString());
        buffer.writeln('│ Body: $body');
      }

      buffer.writeln('└─────────────────────────────────────────────');
      developer.log(buffer.toString(), name: 'ApiClient');
    }

    handler.next(response);
  }

  @override
  void onError(DioException err, ErrorInterceptorHandler handler) {
    if (logErrors) {
      final buffer = StringBuffer();
      buffer.writeln('┌─────────────────────────────────────────────');
      buffer.writeln('│ ERROR');
      buffer.writeln('├─────────────────────────────────────────────');
      buffer
          .writeln('│ ${err.requestOptions.method} ${err.requestOptions.uri}');
      buffer.writeln('│ Type: ${err.type}');
      buffer.writeln('│ Message: ${err.message}');

      if (err.response != null) {
        buffer.writeln(
          '│ Status: ${err.response?.statusCode} ${err.response?.statusMessage}',
        );
        if (err.response?.data != null) {
          final body = _truncateBody(err.response!.data.toString());
          buffer.writeln('│ Response: $body');
        }
      }

      buffer.writeln('│ Stack Trace:');
      final stackLines = err.stackTrace.toString().split('\n').take(5);
      for (final line in stackLines) {
        buffer.writeln('│   $line');
      }

      buffer.writeln('└─────────────────────────────────────────────');
      developer.log(buffer.toString(), name: 'ApiClient', error: err);
    }

    handler.next(err);
  }

  /// Truncate body to prevent massive log dumps
  String _truncateBody(String body) {
    if (body.length <= maxBodyLength) {
      return body;
    }
    return '${body.substring(0, maxBodyLength)}... (truncated, ${body.length} total chars)';
  }

  /// Mask sensitive headers (Authorization, API keys, etc.)
  String _maskSensitiveHeader(String key, String value) {
    final sensitiveHeaders = [
      'authorization',
      'api-key',
      'x-api-key',
      'cookie',
      'set-cookie',
    ];

    if (sensitiveHeaders.contains(key.toLowerCase())) {
      if (value.length <= 10) {
        return '***';
      }
      // Show first 10 chars, mask the rest
      return '${value.substring(0, 10)}***';
    }

    return value;
  }
}
