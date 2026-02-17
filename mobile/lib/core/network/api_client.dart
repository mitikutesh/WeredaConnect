import 'dart:io';

import 'package:dio/dio.dart';
import 'package:dio/io.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/network/api_exception.dart';

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
/// Certificate pinning is currently DISABLED (foundation only).
/// Before production deployment:
/// 1. Deploy F-16 Lambda API Gateway
/// 2. Extract actual certificate SHA-256 hashes
/// 3. Update [certificatePins] with real values
/// 4. Uncomment certificate pinning code in [initialize]
/// 5. Test with production API
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

  /// Pinned SHA-256 certificate hashes for API Gateway.
  ///
  /// TODO: Replace with actual certificate hashes after F-16 deployment.
  /// See /specs/decisions/cert-pinning.md for extraction procedure.
  ///
  /// Format: 'sha256/BASE64_ENCODED_HASH'
  /// Example: 'sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='
  static const List<String> certificatePins = [
    'sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=', // Primary cert (placeholder)
    'sha256/BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=', // Backup cert (placeholder)
  ];

  /// Initialize Dio client with environment and optional certificate pinning.
  ///
  /// Params:
  /// - [environment]: Environment configuration (defaults to current environment)
  /// - [enableCertPinning]: Whether to enable certificate pinning (default: false)
  /// - [interceptors]: Custom interceptors to add (e.g., AuthInterceptor, LoggingInterceptor)
  ///
  /// IMPORTANT: Do not enable cert pinning until real certificates are deployed!
  void initialize({
    Environment? environment,
    bool enableCertPinning = false,
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

    // Certificate pinning (foundation only - disabled by default)
    if (enableCertPinning) {
      dio.httpClientAdapter = IOHttpClientAdapter(
        createHttpClient: () {
          final client = HttpClient();
          client.badCertificateCallback = (cert, host, port) {
            // TODO: Implement actual certificate pinning verification
            // This requires:
            // 1. Extract certificate DER bytes: cert.der
            // 2. Compute SHA-256 hash using package:crypto
            // 3. Base64 encode the hash
            // 4. Compare with certificatePins
            //
            // For now, return false to reject all certificates
            // when pinning is enabled (prevents accidental production use)
            return false;
          };
          return client;
        },
      );
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

  /// Parse response or return raw data
  T _parseResponse<T>(Response<dynamic> response, T Function(dynamic)? parser) {
    if (parser != null) {
      return parser(response.data);
    }
    return response.data as T;
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
