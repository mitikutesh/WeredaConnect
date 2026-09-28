/// Dio interceptor for automatic token injection and refresh
library;

import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Interceptor that automatically injects the ID token and refreshes on 401
///
/// This interceptor:
/// - Adds Authorization header to all requests (if a token exists)
/// - Catches 401 responses and attempts token refresh
/// - Retries failed request with new token
///
/// Concurrency: [QueuedInterceptor] already serializes onError callbacks,
/// so each 401 is handled one at a time with no shared mutable state. When
/// several requests fail together (e.g. a burst of polls after token
/// expiry), the first one refreshes; the rest see that the stored token
/// changed since their request was sent and simply retry with it. Every
/// handler is completed exactly once — a handler left incomplete would jam
/// the queue and hang every later request that receives an error response.
///
/// The bearer token is the Cognito **ID token**, not the access token:
/// custom attributes such as `custom:role` (used for server-side write
/// authorization) are only present on the ID token, never on the access
/// token. Sending the access token causes the server to see a null role and
/// reject every create operation with `*_UNAUTHORIZED`.
///
/// Ethiopian context:
/// - Tokens are stored in secure storage (encrypted on device)
/// - Refresh logic handles offline scenarios gracefully
class AuthInterceptor extends QueuedInterceptor {
  AuthInterceptor({
    required this.storage,
    required this.refreshTokenCallback,
    required Dio dio,
    this.tokenKey = 'id_token',
    this.refreshTokenKey = 'refresh_token',
    List<Interceptor> retryInterceptors = const [],
  }) : _retryDio = _createRetryDio(dio, retryInterceptors);

  /// Secure storage for reading/writing tokens
  final FlutterSecureStorage storage;

  /// Callback to refresh tokens (returns the new ID token to use as bearer).
  /// Should call POST /auth/refresh with the refresh token and persist the
  /// resulting tokens.
  final Future<String?> Function(String refreshToken) refreshTokenCallback;

  /// Dio instance for retrying requests, shares the parent's base config
  /// and httpClientAdapter (cert pinning) but without the AuthInterceptor
  /// to avoid QueuedInterceptor deadlock.
  final Dio _retryDio;

  /// Storage key for the bearer token (Cognito ID token).
  final String tokenKey;

  /// Storage key for refresh token
  final String refreshTokenKey;

  /// Create a Dio clone that shares the parent's configuration and
  /// httpClientAdapter (preserving certificate pinning) but without
  /// the AuthInterceptor to avoid QueuedInterceptor deadlock.
  /// [interceptors] are safe, non-auth extras (e.g. data-usage metering)
  /// so retried requests are still observed.
  static Dio _createRetryDio(Dio parent, List<Interceptor> interceptors) {
    final retryDio = Dio(
      BaseOptions(
        baseUrl: parent.options.baseUrl,
        connectTimeout: parent.options.connectTimeout,
        receiveTimeout: parent.options.receiveTimeout,
        sendTimeout: parent.options.sendTimeout,
        headers: Map.from(parent.options.headers),
      ),
    );
    retryDio.httpClientAdapter = parent.httpClientAdapter;
    retryDio.interceptors.addAll(interceptors);
    return retryDio;
  }

  @override
  Future<void> onRequest(
    RequestOptions options,
    RequestInterceptorHandler handler,
  ) async {
    // Skip auth injection for public endpoints
    if (options.path.contains('/auth/refresh') ||
        options.path.contains('/health')) {
      return handler.next(options);
    }

    // Inject the ID token if available (carries custom:role for the server).
    final bearerToken = await storage.read(key: tokenKey);
    if (bearerToken != null && bearerToken.isNotEmpty) {
      options.headers['Authorization'] = 'Bearer $bearerToken';
    }

    handler.next(options);
  }

  @override
  Future<void> onError(
    DioException err,
    ErrorInterceptorHandler handler,
  ) async {
    // Only handle 401 Unauthorized errors
    if (err.response?.statusCode != 401) {
      return handler.next(err);
    }

    // Skip refresh for public endpoints (prevent infinite loop / unnecessary refresh)
    if (err.requestOptions.path.contains('/auth/refresh') ||
        err.requestOptions.path.contains('/health')) {
      return handler.next(err);
    }

    try {
      // A 401 handled earlier in the queue may already have refreshed the
      // token. If the stored bearer differs from the one this request failed
      // with, retry directly instead of refreshing again.
      final failedBearer = _bearerOf(err.requestOptions);
      final storedToken = await storage.read(key: tokenKey);
      if (storedToken != null &&
          storedToken.isNotEmpty &&
          storedToken != failedBearer) {
        try {
          final retryResponse = await _retry(err.requestOptions, storedToken);
          return handler.resolve(retryResponse);
        } on DioException catch (retryError) {
          if (retryError.response?.statusCode != 401) {
            return handler.next(retryError);
          }
          // Stored token is stale too — fall through to a fresh refresh.
        }
      }

      final refreshToken = await storage.read(key: refreshTokenKey);
      if (refreshToken == null || refreshToken.isEmpty) {
        // No refresh token available - user must re-authenticate
        await _clearTokens();
        return handler.next(err);
      }

      // Call refresh endpoint (returns the new ID token to use as bearer)
      final newBearerToken = await refreshTokenCallback(refreshToken);
      if (newBearerToken == null || newBearerToken.isEmpty) {
        // Refresh failed - clear tokens and force re-authentication
        await _clearTokens();
        return handler.next(err);
      }

      // Save new bearer (ID) token
      await storage.write(key: tokenKey, value: newBearerToken);

      // Retry original request with new token
      final retryResponse = await _retry(err.requestOptions, newBearerToken);
      handler.resolve(retryResponse);
    } on Object {
      // Transient error (timeout, network, failed retry) — keep refresh
      // token intact. The callback only returns null for server rejections
      // (400/401), which is handled above with _clearTokens(). Rethrown
      // exceptions are transient — the refresh token is likely still valid.
      handler.next(err);
    }
  }

  /// Extract the bearer token the failed request was sent with, if any.
  String? _bearerOf(RequestOptions options) {
    final auth = options.headers['Authorization'];
    if (auth is String && auth.startsWith('Bearer ')) {
      return auth.substring('Bearer '.length);
    }
    return null;
  }

  /// Retry a request with new access token.
  ///
  /// Uses [_retryDio] which shares the parent Dio's base config and
  /// httpClientAdapter (cert pinning) but without AuthInterceptor,
  /// avoiding QueuedInterceptor deadlock.
  Future<Response<dynamic>> _retry(
    RequestOptions requestOptions,
    String newBearerToken,
  ) async {
    final options = Options(
      method: requestOptions.method,
      headers: {
        ...requestOptions.headers,
        'Authorization': 'Bearer $newBearerToken',
      },
      sendTimeout: requestOptions.sendTimeout,
      receiveTimeout: requestOptions.receiveTimeout,
    );

    return _retryDio.request(
      requestOptions.path,
      data: requestOptions.data,
      queryParameters: requestOptions.queryParameters,
      options: options,
    );
  }

  /// Clear all tokens from secure storage
  Future<void> _clearTokens() async {
    await storage.delete(key: tokenKey);
    await storage.delete(key: refreshTokenKey);
  }
}
