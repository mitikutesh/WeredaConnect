import 'dart:convert' show jsonDecode;

import 'package:flutter/foundation.dart' show kIsWeb;

import 'package:dio/dio.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/network/json_body.dart';
import 'package:wereda_connect/core/network/cert_pinning_stub.dart'
    if (dart.library.io) 'package:wereda_connect/core/network/cert_pinning_io.dart'
    as cert_pinning;

/// Singleton HTTP client with certificate pinning for secure API communication.
///
/// This client provides:
/// - Certificate pinning to prevent MitM attacks on Ethiopian 3G networks
/// - Standardized timeout configuration (30s for rural 3G)
/// - JSON content negotiation
/// - HTTP methods (GET, POST, PUT, DELETE, PATCH)
/// - Automatic error transformation to typed exceptions
///
/// SECURITY NOTE:
/// Certificate pinning is ENABLED by default on mobile builds. It is CA
/// pinning: the HttpClient trusts only the Amazon Trust Services roots
/// bundled in `amazon_trust_roots.dart` (see `cert_pinning_io.dart`), so leaf
/// and intermediate rotations by AWS never break the app. Rotation runbook:
/// docs/runbooks/f05-cert-pinning-activation.md.
///
/// Ethiopian context:
/// - 3G networks are vulnerable to MitM attacks
/// - Certificate pinning is critical for auth token security
/// - 30s timeouts account for slow rural connectivity
class ApiClient {
  static final ApiClient _instance = ApiClient._internal();
  factory ApiClient() => _instance;
  ApiClient._internal();

  late final Dio dio;
  bool _isInitialized = false;
  late final Environment _environment;

  /// Whether the API client has been initialized.
  bool get isInitialized => _isInitialized;

  /// GETs in flight, keyed by path + query. Identical concurrent reads (a
  /// dashboard's stats, activity and hero card all wanting the same list)
  /// then hit the network once (SIM-21). Entries go as soon as the request
  /// settles; nothing is cached after that.
  final Map<String, Future<Object?>> _inflightGets = {};

  static String _inflightKey(String path, Map<String, dynamic>? query) {
    if (query == null || query.isEmpty) return path;
    final keys = query.keys.toList()..sort();
    return '$path?${keys.map((k) => '$k=${query[k]}').join('&')}';
  }

  /// Initialize Dio client with environment and optional certificate pinning.
  ///
  /// Params:
  /// - [environment]: Environment configuration (defaults to current environment)
  /// - [enableCertPinning]: Whether to enable certificate pinning (default: true)
  /// - [interceptors]: Custom interceptors to add (e.g., AuthInterceptor, LoggingInterceptor)
  ///
  /// Certificate pinning is enabled by default for production security.
  /// Set [enableCertPinning] to false only for local development/testing.
  void initialize({
    Environment? environment,
    bool enableCertPinning = true,
    List<Interceptor>? interceptors,
  }) {
    if (_isInitialized) return;
    _isInitialized = true;

    _environment = environment ?? Environment.config;

    dio = Dio(
      BaseOptions(
        baseUrl: _environment.apiBaseUrl,
        connectTimeout: _environment.connectTimeout,
        receiveTimeout: _environment.receiveTimeout,
        // On web, use plain text so _parseResponse always goes through
        // jsonDecode and produces proper Dart Maps.  Dio's ResponseType.json
        // on web uses the browser's native JSON parser, which returns JS
        // objects that may fail Dart Map key lookups.
        responseType: kIsWeb ? ResponseType.plain : ResponseType.json,
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'application/json',
        },
      ),
    );

    // Add custom interceptors
    if (interceptors != null) {
      dio.interceptors.addAll(interceptors);
    }

    // Certificate pinning (CA pinning to Amazon Trust Services roots, mobile only)
    if (enableCertPinning && !kIsWeb) {
      cert_pinning.applyCertPinning(dio);
    }
  }

  /// GET request. Plain GETs (no custom headers, cancel token or parser)
  /// that are identical to one still in flight share its response.
  Future<T> get<T>(
    String path, {
    Map<String, dynamic>? queryParameters,
    Map<String, dynamic>? headers,
    CancelToken? cancelToken,
    T Function(dynamic)? parser,
  }) {
    final coalesce = headers == null && cancelToken == null && parser == null;
    final key = coalesce ? _inflightKey(path, queryParameters) : null;
    if (key != null) {
      final pending = _inflightGets[key];
      if (pending != null) return pending.then((value) => value as T);
    }

    final request = _get<T>(
      path,
      queryParameters: queryParameters,
      headers: headers,
      cancelToken: cancelToken,
      parser: parser,
    );
    if (key != null) {
      _inflightGets[key] = request;
      // The original caller awaits `request`; this chain only cleans up and
      // must not surface the same error a second time.
      request.whenComplete(() => _inflightGets.remove(key)).ignore();
    }
    return request;
  }

  Future<T> _get<T>(
    String path, {
    Map<String, dynamic>? queryParameters,
    Map<String, dynamic>? headers,
    CancelToken? cancelToken,
    T Function(dynamic)? parser,
  }) async {
    try {
      final response = await dio.get<dynamic>(
        path,
        queryParameters: queryParameters,
        options: headers != null ? Options(headers: headers) : null,
        cancelToken: cancelToken,
      );
      return _parseResponse<T>(response, parser);
    } catch (e) {
      throw _handleError(e);
    }
  }

  /// POST request
  Future<T> post<T>(
    String path, {
    dynamic data,
    Map<String, dynamic>? queryParameters,
    Map<String, dynamic>? headers,
    CancelToken? cancelToken,
    T Function(dynamic)? parser,
  }) async {
    try {
      final response = await dio.post<dynamic>(
        path,
        data: data,
        queryParameters: queryParameters,
        options: headers != null ? Options(headers: headers) : null,
        cancelToken: cancelToken,
      );
      return _parseResponse<T>(response, parser);
    } catch (e) {
      throw _handleError(e);
    }
  }

  /// PUT request
  Future<T> put<T>(
    String path, {
    dynamic data,
    Map<String, dynamic>? queryParameters,
    Map<String, dynamic>? headers,
    CancelToken? cancelToken,
    T Function(dynamic)? parser,
  }) async {
    try {
      final response = await dio.put<dynamic>(
        path,
        data: data,
        queryParameters: queryParameters,
        options: headers != null ? Options(headers: headers) : null,
        cancelToken: cancelToken,
      );
      return _parseResponse<T>(response, parser);
    } catch (e) {
      throw _handleError(e);
    }
  }

  /// DELETE request
  Future<T> delete<T>(
    String path, {
    dynamic data,
    Map<String, dynamic>? queryParameters,
    Map<String, dynamic>? headers,
    CancelToken? cancelToken,
    T Function(dynamic)? parser,
  }) async {
    try {
      final response = await dio.delete<dynamic>(
        path,
        data: data,
        queryParameters: queryParameters,
        options: headers != null ? Options(headers: headers) : null,
        cancelToken: cancelToken,
      );
      return _parseResponse<T>(response, parser);
    } catch (e) {
      throw _handleError(e);
    }
  }

  /// PATCH request
  Future<T> patch<T>(
    String path, {
    dynamic data,
    Map<String, dynamic>? queryParameters,
    Map<String, dynamic>? headers,
    CancelToken? cancelToken,
    T Function(dynamic)? parser,
  }) async {
    try {
      final response = await dio.patch<dynamic>(
        path,
        data: data,
        queryParameters: queryParameters,
        options: headers != null ? Options(headers: headers) : null,
        cancelToken: cancelToken,
      );
      return _parseResponse<T>(response, parser);
    } catch (e) {
      throw _handleError(e);
    }
  }

  /// Parse response or return raw data.
  ///
  /// On Flutter web, Dio may return raw JSON strings even with
  /// `ResponseType.json`. This method falls back to manual decoding.
  T _parseResponse<T>(Response<dynamic> response, T Function(dynamic)? parser) {
    if (parser != null) {
      return parser(response.data);
    }
    final data = response.data;
    // On web, response.data may arrive as a String instead of a decoded Map.
    if (data is String && data.isNotEmpty && T != String) {
      return jsonDecode(data) as T;
    }
    return data as T;
  }

  /// Transform Dio errors into custom ApiException types
  ApiException _handleError(Object error) {
    // A rejected certificate chain (pinning) is not a connectivity problem:
    // classify it before the type switch, which would call it "network".
    if (isSecureConnectionFailure(error)) {
      return const SecureConnectionException();
    }

    if (error is DioException) {
      switch (error.type) {
        case DioExceptionType.connectionTimeout:
        case DioExceptionType.sendTimeout:
        case DioExceptionType.receiveTimeout:
          return TimeoutException(error.message ?? 'Request timeout');

        case DioExceptionType.connectionError:
          return NetworkException(error.message ?? 'Network connection failed');

        case DioExceptionType.badResponse:
          final statusCode = error.response?.statusCode;
          final data = error.response?.data;
          final message = _extractErrorMessage(data) ??
              error.response?.statusMessage ??
              'Server error';
          final code = _extractErrorCode(data);

          if (statusCode == 400) {
            return ValidationException(message, statusCode, code);
          } else if (statusCode == 403 &&
              (code == stepUpRequiredCode || code == faydaStepUpRequiredCode)) {
            // Step-up: the session is valid but needs a fresh password
            // (AUTH_040) or Fayda login (AUTH_041) for this money action.
            // Callers re-authenticate and retry; never a logout.
            return StepUpRequiredException(message, statusCode, code);
          } else if (statusCode == 401 || statusCode == 403) {
            return AuthException(message, statusCode, code);
          } else if (statusCode == 404) {
            return NotFoundException(message, statusCode, code);
          } else if (statusCode == 409) {
            return ConflictException(message, statusCode, code);
          } else if (statusCode == 429) {
            return RateLimitException(message, statusCode, code);
          } else {
            return ServerException(message, statusCode, code);
          }

        case DioExceptionType.cancel:
          return const NetworkException('Request cancelled');

        case DioExceptionType.badCertificate:
          return const SecureConnectionException();

        case DioExceptionType.unknown:
          return UnknownException(error.message ?? 'Unknown error occurred');
      }
    }

    return UnknownException(error.toString());
  }

  /// Extract the structured error code (`{"error":{"code":...}}`), if any.
  static String? _extractErrorCode(dynamic data) {
    final map = decodeJsonMap(data);
    final error = map?['error'];
    if (error is Map) {
      final code = error['code'];
      if (code is String) return code;
    }
    return null;
  }

  /// Extract error message from response data
  String? _extractErrorMessage(dynamic data) {
    if (data == null) return null;

    final map = decodeJsonMap(data);
    if (map != null) {
      // Structured error body: {"error": {"code": ..., "message": ...}}.
      // Reading `error` as a String here used to throw a TypeError.
      final error = map['error'];
      if (error is Map) {
        final message = error['message'];
        if (message is String) return message;
      }
      // Try common error message fields
      final message = map['message'];
      if (message is String) return message;
      if (error is String) return error;
      final detail = map['detail'];
      if (detail is String) return detail;
      return null;
    }

    if (data is String) {
      return data;
    }

    return null;
  }
}
