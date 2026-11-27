/// Dio interceptor for automatic token injection and refresh
library;

import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'package:wereda_connect/core/auth/auth_service.dart' show jwtSubject;
import 'package:wereda_connect/core/auth/session_signals.dart';

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
/// On web every tab shares the token slots. With [sessionSubject] set, a
/// tab never sends, refreshes or retries with another account's tokens
/// (FUP-96); see [sessionSubject].
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
    this.sessionSubject,
    SessionSignals? signals,
  })  : _retryDio = _createRetryDio(dio, retryInterceptors),
        _signals = signals ?? SessionSignals.instance;

  /// Secure storage for reading/writing tokens
  final FlutterSecureStorage storage;

  /// Callback to refresh tokens (returns the new ID token to use as bearer).
  /// Must call POST /auth/refresh with the refresh token and persist the
  /// resulting tokens: the interceptor writes nothing itself, because only
  /// the callback can tell whether the session it refreshed is still the
  /// stored one (FUP-82). Returns null when the server rejects the token.
  final Future<String?> Function(String refreshToken) refreshTokenCallback;

  /// The `sub` of the account this app instance is signed in as, or null
  /// when none (FUP-96). The web app sets it: every tab shares the token
  /// slots, so a tab still showing one account can find another account's
  /// session there after a sign-in in another tab. Sent with that token,
  /// the tab's ids would act under the other account and log
  /// `ROLE_FORBIDDEN` on it.
  ///
  /// When set, a stored or refreshed bearer whose `sub` names another
  /// account is never sent: the request fails locally (an
  /// [OtherAccountSessionException]) and
  /// [SessionSignals.otherAccountSignedIn] fires so the tab signs itself
  /// out. A bearer whose `sub` cannot be read is sent as before (the server
  /// rejects it). Null (mobile) skips the check.
  final String? Function()? sessionSubject;

  final SessionSignals _signals;

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
    // Skip auth injection for public endpoints, and for logout, which
    // brings its own bearer (see [_bringsOwnBearer]).
    if (options.path.contains('/auth/refresh') ||
        options.path.contains('/health') ||
        _bringsOwnBearer(options)) {
      return handler.next(options);
    }

    // Inject the ID token if available (carries custom:role for the server).
    final bearerToken = await storage.read(key: tokenKey);
    if (bearerToken != null && bearerToken.isNotEmpty) {
      if (_isOtherAccount(bearerToken)) {
        _signals.otherAccountSignedIn();
        return handler.reject(_otherAccountError(options));
      }
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

    // Skip refresh for public endpoints (prevent infinite loop / unnecessary
    // refresh), and for logout, which handles its own 401.
    if (err.requestOptions.path.contains('/auth/refresh') ||
        err.requestOptions.path.contains('/health') ||
        _bringsOwnBearer(err.requestOptions)) {
      return handler.next(err);
    }

    try {
      // Another account's session in the slots (another web tab signed in
      // since this request was sent): neither refresh it nor retry with it.
      final storedToken = await storage.read(key: tokenKey);
      if (_isOtherAccount(storedToken)) {
        _signals.otherAccountSignedIn();
        return handler.next(err);
      }

      // A 401 handled earlier in the queue may already have refreshed the
      // token. If the stored bearer differs from the one this request failed
      // with, retry directly instead of refreshing again.
      final failedBearer = _bearerOf(err.requestOptions);
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
        await _clearTokensIfUnchanged(bearer: storedToken);
        return handler.next(err);
      }

      // Call refresh endpoint (returns the new ID token to use as bearer).
      // The callback persists the new tokens, unless the session ended
      // while it ran (FUP-82); nothing is written here.
      final newBearerToken = await refreshTokenCallback(refreshToken);
      if (newBearerToken == null || newBearerToken.isEmpty) {
        // Refresh failed - clear tokens and force re-authentication
        await _clearTokensIfUnchanged(
          bearer: storedToken,
          refreshToken: refreshToken,
        );
        return handler.next(err);
      }
      if (_isOtherAccount(newBearerToken)) {
        _signals.otherAccountSignedIn();
        return handler.next(err);
      }

      // Retry original request with new token
      final retryResponse = await _retry(err.requestOptions, newBearerToken);
      handler.resolve(retryResponse);
    } on Object {
      // Transient error (timeout, network, failed retry): keep the refresh
      // token. The callback only returns null for server rejections
      // (400/401), handled above with _clearTokensIfUnchanged(). Rethrown
      // exceptions are transient: the refresh token is likely still valid.
      handler.next(err);
    }
  }

  /// Request `extra` flag for a request that brings its own bearer, like
  /// logout: `DELETE /user/account` sends the token of the account its
  /// screen was opened for, read and checked right before the send.
  static const ownBearerExtra = 'ownBearer';

  /// POST /auth/logout carries a bearer minted from the leaving session's
  /// own tokens and runs while that session is cleared (SIM-55). It must
  /// never read the token slots, nor refresh or clear them on a 401: by then
  /// they may hold the next session's tokens. The same holds for a request
  /// flagged with [ownBearerExtra]: on web the slots are shared by every
  /// tab, so they may hold another account's tokens.
  static bool _bringsOwnBearer(RequestOptions options) =>
      options.path.contains('/auth/logout') ||
      options.extra[ownBearerExtra] == true;

  /// Whether [token] belongs to another account than [sessionSubject].
  /// False without a signed-in account, without a token, or when its `sub`
  /// cannot be read.
  bool _isOtherAccount(String? token) {
    final expected = sessionSubject?.call();
    if (expected == null || expected.isEmpty) return false;
    if (token == null || token.isEmpty) return false;
    final subject = jwtSubject(token);
    return subject != null && subject != expected;
  }

  DioException _otherAccountError(RequestOptions options) => DioException(
        requestOptions: options,
        error: const OtherAccountSessionException(),
        message: OtherAccountSessionException.text,
      );

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

  /// Clear the token slots this request found dead. A slot rewritten since
  /// it was read (a new sign-in, here or in another web tab) belongs to
  /// another session and is kept.
  Future<void> _clearTokensIfUnchanged({
    required String? bearer,
    String? refreshToken,
  }) async {
    if (await storage.read(key: tokenKey) == bearer) {
      await storage.delete(key: tokenKey);
    }
    if (await storage.read(key: refreshTokenKey) == refreshToken) {
      await storage.delete(key: refreshTokenKey);
    }
  }
}

/// A request refused before it was sent: the stored session belongs to
/// another account than the one this app instance is signed in as (FUP-96).
class OtherAccountSessionException implements Exception {
  const OtherAccountSessionException();

  static const text = 'Signed in as another account in another tab';

  @override
  String toString() => 'OtherAccountSessionException: $text';
}
