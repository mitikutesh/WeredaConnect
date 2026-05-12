import 'dart:convert' show jsonDecode;

import 'package:flutter/foundation.dart' show kIsWeb;

import 'package:dio/dio.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
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
/// Certificate pinning is ENABLED for production builds.
/// Pins extracted from the dev API Gateway in eu-north-1.
/// TODO: re-extract after deploying dev to eu-north-1 and update these pins.
/// If API Gateway rotates certs, update pins and redeploy.
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

  /// Pinned SHA-256 public key hashes for API Gateway.
  ///
  /// Primary = leaf certificate, Backup = intermediate CA certificate.
  /// Both must be updated when API Gateway rotates certificates.
  static const List<String> certificatePins = [
    'sha256/n+bszuvMQs+fgKHHBnrQp+Oy9jRCE4qgdpaGIACkVo0=', // Primary (leaf cert, eu-north-1, extracted 2026-04-07)
    'sha256/G9LNNAql897egYsabashkzUCTEJkWBzgoEtk8X/678c=', // Backup (Amazon RSA 2048 M04 intermediate CA)
  ];

  /// Initialize Dio client with environment and optional certificate pinning.
  ///
  /// Params:
  /// - [environment]: Environment configuration (defaults to current environment)
  /// - [enableCertPinning]: Whether to enable certificate pinning (default: false)
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

    // Certificate pinning (foundation only - disabled by default, mobile only)
    if (enableCertPinning && !kIsWeb) {
      cert_pinning.applyCertPinning(dio);
    }
  }

  /// GET request
  Future<T> get<T>(
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
          final message = _extractErrorMessage(error.response?.data) ??
              error.response?.statusMessage ??
              'Server error';

          if (statusCode == 400) {
            return ValidationException(message, statusCode);
          } else if (statusCode == 401 || statusCode == 403) {
            return AuthException(message, statusCode);
          } else if (statusCode == 404) {
            return NotFoundException(message, statusCode);
          } else if (statusCode == 409) {
            return ConflictException(message, statusCode);
          } else if (statusCode == 429) {
            return RateLimitException(message, statusCode);
          } else {
            return ServerException(message, statusCode);
          }

        case DioExceptionType.cancel:
          return const NetworkException('Request cancelled');

        case DioExceptionType.badCertificate:
          return const NetworkException('SSL certificate verification failed');

        case DioExceptionType.unknown:
          return UnknownException(error.message ?? 'Unknown error occurred');
      }
    }

    return UnknownException(error.toString());
  }

  /// Extract error message from response data
  String? _extractErrorMessage(dynamic data) {
    if (data == null) return null;

    if (data is Map<String, dynamic>) {
      // Try common error message fields
      return data['message'] as String? ??
          data['error'] as String? ??
          data['detail'] as String?;
    }

    if (data is String) {
      return data;
    }

    return null;
  }
}
