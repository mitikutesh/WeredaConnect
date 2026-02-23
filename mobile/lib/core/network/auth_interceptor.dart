/// Dio interceptor for automatic token injection and refresh
library;

import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Interceptor that automatically injects access tokens and refreshes on 401
///
/// This interceptor:
/// - Adds Authorization header to all requests (if token exists)
/// - Catches 401 responses and attempts token refresh
/// - Retries failed request with new token
/// - Handles concurrent refresh attempts (prevents multiple refresh calls)
///
/// Ethiopian context:
/// - Tokens are stored in secure storage (encrypted on device)
/// - Refresh logic handles offline scenarios gracefully
/// - Concurrent requests during refresh are queued and retried together
class AuthInterceptor extends QueuedInterceptor {
  AuthInterceptor({
    required this.storage,
    required this.refreshTokenCallback,
    this.tokenKey = 'access_token',
    this.refreshTokenKey = 'refresh_token',
  });

  /// Secure storage for reading/writing tokens
  final FlutterSecureStorage storage;

  /// Callback to refresh tokens (returns new access token)
  /// Should call POST /auth/refresh with refresh token
  final Future<String?> Function(String refreshToken) refreshTokenCallback;

  /// Storage key for access token
  final String tokenKey;

  /// Storage key for refresh token
  final String refreshTokenKey;

  /// Flag to prevent concurrent refresh attempts
  bool _isRefreshing = false;

  /// Queue of requests waiting for token refresh
  final List<_PendingRequest> _pendingRequests = [];

  @override
  Future<void> onRequest(
    RequestOptions options,
    RequestInterceptorHandler handler,
  ) async {
    // Skip auth injection for refresh endpoint (prevent infinite loop)
    if (options.path.contains('/auth/refresh')) {
      return handler.next(options);
    }

    // Inject access token if available
    final accessToken = await storage.read(key: tokenKey);
    if (accessToken != null && accessToken.isNotEmpty) {
      options.headers['Authorization'] = 'Bearer $accessToken';
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

    // Skip refresh for the refresh endpoint itself (prevent infinite loop)
    if (err.requestOptions.path.contains('/auth/refresh')) {
      return handler.next(err);
    }

    // If already refreshing, queue this request
    if (_isRefreshing) {
      final pendingRequest = _PendingRequest(
        requestOptions: err.requestOptions,
        handler: handler,
      );
      _pendingRequests.add(pendingRequest);
      return;
    }

    // Attempt token refresh
    _isRefreshing = true;

    try {
      final refreshToken = await storage.read(key: refreshTokenKey);

      if (refreshToken == null || refreshToken.isEmpty) {
        // No refresh token available - user must re-authenticate
        await _clearTokens();
        _rejectPendingRequests(err);
        return handler.next(err);
      }

      // Call refresh endpoint
      final newAccessToken = await refreshTokenCallback(refreshToken);

      if (newAccessToken == null || newAccessToken.isEmpty) {
        // Refresh failed - clear tokens and force re-authentication
        await _clearTokens();
        _rejectPendingRequests(err);
        return handler.next(err);
      }

      // Save new access token
      await storage.write(key: tokenKey, value: newAccessToken);

      // Retry original request with new token
      final retryResponse = await _retry(err.requestOptions, newAccessToken);
      handler.resolve(retryResponse);

      // Retry all pending requests
      await _retryPendingRequests(newAccessToken);
    } on Object {
      // Refresh failed - clear tokens
      await _clearTokens();
      _rejectPendingRequests(err);
      handler.next(err);
    } finally {
      _isRefreshing = false;
      _pendingRequests.clear();
    }
  }

  /// Retry a request with new access token.
  ///
  /// SECURITY: Reconstructs the full URI from the original request options
  /// (baseUrl + path) instead of creating a bare Dio() instance. This
  /// preserves timeouts and avoids bypassing security configuration.
  Future<Response<dynamic>> _retry(
    RequestOptions requestOptions,
    String newAccessToken,
  ) async {
    final options = Options(
      method: requestOptions.method,
      headers: {
        ...requestOptions.headers,
        'Authorization': 'Bearer $newAccessToken',
      },
      sendTimeout: requestOptions.sendTimeout,
      receiveTimeout: requestOptions.receiveTimeout,
    );

    final dio = Dio(
      BaseOptions(
        baseUrl: requestOptions.baseUrl,
        connectTimeout: requestOptions.connectTimeout,
      ),
    );
    return dio.request(
      requestOptions.path,
      data: requestOptions.data,
      queryParameters: requestOptions.queryParameters,
      options: options,
    );
  }

  /// Retry all pending requests with new token
  Future<void> _retryPendingRequests(String newAccessToken) async {
    for (final pendingRequest in _pendingRequests) {
      try {
        final response = await _retry(
          pendingRequest.requestOptions,
          newAccessToken,
        );
        pendingRequest.handler.resolve(response);
      } on Object catch (e) {
        pendingRequest.handler.next(
          DioException(
            requestOptions: pendingRequest.requestOptions,
            error: e,
          ),
        );
      }
    }
  }

  /// Reject all pending requests with the original error
  void _rejectPendingRequests(DioException originalError) {
    for (final pendingRequest in _pendingRequests) {
      pendingRequest.handler.next(originalError);
    }
  }

  /// Clear all tokens from secure storage
  Future<void> _clearTokens() async {
    await storage.delete(key: tokenKey);
    await storage.delete(key: refreshTokenKey);
  }
}

/// Pending request waiting for token refresh
class _PendingRequest {
  _PendingRequest({
    required this.requestOptions,
    required this.handler,
  });

  final RequestOptions requestOptions;
  final ErrorInterceptorHandler handler;
}
