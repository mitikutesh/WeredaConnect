import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/models/auth_result.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';

/// Main authentication service for WeredaConnect.
///
/// This service handles:
/// - User registration with Fayda eKYC verification
/// - Online login (AWS Cognito)
/// - Offline login (cached credentials)
/// - Token refresh
/// - Logout
///
/// Flow:
/// 1. Registration: Fayda OIDC verify → register(faydaVerificationToken) → tokens
/// 2. Online login: loginOnline() → cache credentials → success
/// 3. Offline login: loginOffline() → verify cached hash → success
/// 4. Token refresh: Automatic in background when tokens expire
///
/// Ethiopian context:
/// - Phone number as primary identifier (not email)
/// - Fayda eKYC for identity verification
/// - Offline-first: Cached credentials work without connectivity
class AuthService {
  final Dio _dio;
  final OfflineAuthCache _offlineCache;
  final Connectivity _connectivity;

  AuthService({
    Dio? dio,
    OfflineAuthCache? offlineCache,
    Connectivity? connectivity,
  })  : _dio = dio ?? ApiClient().dio,
        _offlineCache = offlineCache ?? OfflineAuthCache(),
        _connectivity = connectivity ?? Connectivity() {
    // Initialize ApiClient with base URL (cert pinning disabled by default)
    if (dio == null) {
      ApiClient().initialize();
    }
  }

  /// Register a new user with phone number, password, and Fayda verification.
  ///
  /// The user must first complete Fayda OIDC verification to obtain a
  /// [faydaVerificationToken]. The server validates this token and creates
  /// the user account, returning auth tokens directly (no OTP step).
  ///
  /// Params:
  /// - [phoneNumber]: Ethiopian phone number (+251XXXXXXXXX)
  /// - [password]: User password (minimum 8 characters, lowercase + digit)
  /// - [name]: User's full name (supports Amharic/Oromo)
  /// - [role]: User role (farmer, trucker, buyer)
  /// - [woreda]: Ethiopian woreda (district)
  /// - [faydaVerificationToken]: Token from Fayda OIDC verification
  /// - [preferredLanguage]: UI language (am, om, en)
  ///
  /// Returns [AuthResult] with tokens on success.
  Future<AuthResult> register({
    required String phoneNumber,
    required String password,
    required String name,
    required UserRole role,
    required String woreda,
    String? faydaVerificationToken,
    String preferredLanguage = 'am',
  }) async {
    if (!Environment.isApiConfigured) {
      return _notConfiguredResult();
    }

    try {
      // Check connectivity
      final connectivityResults = await _connectivity.checkConnectivity();
      if (!connectivityResults
          .any((result) => result != ConnectivityResult.none)) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'Registration requires internet connection',
        );
      }

      // Call registration API, optionally with Fayda verification token
      final data = <String, dynamic>{
        'phoneNumber': phoneNumber,
        'password': password,
        'displayName': name,
        'role': role.name,
        'woreda': woreda,
        'preferredLanguage': preferredLanguage,
      };
      if (faydaVerificationToken != null) {
        data['faydaVerificationToken'] = faydaVerificationToken;
      }

      final response = await _dio.post<Map<String, dynamic>>(
        ApiConfig.registerEndpoint,
        data: data,
      );

      final responseData = response.data as Map<String, dynamic>;

      // Server now returns tokens directly (no OTP step)
      final tokenData = responseData['tokens'] as Map<String, dynamic>;
      final expiresIn = tokenData['expiresIn'] as int? ?? 3600;
      final tokens = AuthTokens(
        accessToken: tokenData['accessToken'] as String,
        refreshToken: tokenData['refreshToken'] as String? ?? '',
        idToken: tokenData['idToken'] as String,
        expiresAt: DateTime.now().add(Duration(seconds: expiresIn)),
      );

      final user = responseData['user'] as Map<String, dynamic>;
      final userId = user['userId'] as String;

      // Cache credentials for offline use
      try {
        await _offlineCache.cacheCredentials(
          phoneNumber: phoneNumber,
          password: password,
          userId: userId,
          role: role,
          name: name,
          woreda: woreda,
          tokens: tokens,
        );
      } on Exception catch (e) {
        debugPrint('[AuthService] Failed to cache credentials: $e');
      }

      return AuthResult.success(tokens, userId: userId);
    } on DioException catch (e) {
      return _handleDioError(e);
    } on Exception {
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Something went wrong. Please try again.',
      );
    }
  }

  /// Login with phone number and password (online mode).
  ///
  /// This authenticates with AWS Cognito and caches credentials
  /// for future offline logins.
  ///
  /// Params:
  /// - [phoneNumber]: User's phone number
  /// - [password]: User's password
  ///
  /// Returns [AuthResult] with tokens on success.
  Future<AuthResult> loginOnline({
    required String phoneNumber,
    required String password,
  }) async {
    if (!Environment.isApiConfigured) {
      return _notConfiguredResult();
    }

    try {
      // Check connectivity
      final connectivityResults = await _connectivity.checkConnectivity();
      if (!connectivityResults
          .any((result) => result != ConnectivityResult.none)) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'Online login requires internet connection',
        );
      }

      // Call login API
      final response = await _dio.post<Map<String, dynamic>>(
        ApiConfig.loginEndpoint,
        data: {
          'phoneNumber': phoneNumber,
          'password': password,
        },
      );

      final data = response.data as Map<String, dynamic>;

      // Server wraps tokens under "tokens" key with expiresIn (seconds)
      final tokenData = data['tokens'] as Map<String, dynamic>;
      final expiresIn = tokenData['expiresIn'] as int? ?? 3600;
      final tokens = AuthTokens(
        accessToken: tokenData['accessToken'] as String,
        refreshToken: tokenData['refreshToken'] as String? ?? '',
        idToken: tokenData['idToken'] as String,
        expiresAt: DateTime.now().add(Duration(seconds: expiresIn)),
      );

      final user = data['user'] as Map<String, dynamic>;
      final userId = user['userId'] as String;

      // Cache credentials for offline use (best-effort — don't block login)
      try {
        await _offlineCache.cacheCredentials(
          phoneNumber: phoneNumber,
          password: password,
          userId: userId,
          role: UserRole.values.firstWhere(
            (r) =>
                r.name.toLowerCase() == (user['role'] as String).toLowerCase(),
            orElse: () => UserRole.farmer,
          ),
          name: (user['displayName'] as String?) ?? '',
          woreda: user['woreda'] as String,
          tokens: tokens,
        );
      } on Exception catch (e) {
        debugPrint('[AuthService] Failed to cache credentials: $e');
      }

      return AuthResult.success(tokens, userId: userId);
    } on DioException catch (e) {
      return _handleDioError(e);
    } on Exception catch (e, stackTrace) {
      debugPrint('[AuthService] loginOnline exception: $e');
      debugPrint('[AuthService] stackTrace: $stackTrace');
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Something went wrong. Please try again.',
      );
    }
  }

  /// Login with cached credentials (offline mode).
  ///
  /// This verifies the password against the cached PBKDF2 hash and
  /// returns cached tokens.
  ///
  /// Requires:
  /// - User must have logged in online at least once
  /// - Password must match cached hash
  ///
  /// Params:
  /// - [phoneNumber]: User's phone number
  /// - [password]: User's password
  ///
  /// Returns [AuthResult] with cached tokens on success.
  Future<AuthResult> loginOffline({
    required String phoneNumber,
    required String password,
  }) async {
    try {
      // Check if credentials are cached
      final hasCached = await _offlineCache.hasCachedCredentials(phoneNumber);
      if (!hasCached) {
        return AuthResult.offlineNotAvailable();
      }

      // Verify password against cached hash
      final isValid = await _offlineCache.verifyPassword(
        phoneNumber,
        password,
      );

      if (!isValid) {
        return AuthResult.failure(
          AuthErrorCode.invalidCredentials,
          'Invalid phone number or password',
        );
      }

      // Get cached user and tokens
      final user = await _offlineCache.getCachedUser(phoneNumber);
      final tokens = await _offlineCache.getCachedTokens(phoneNumber);

      if (user == null || tokens == null) {
        return AuthResult.failure(
          AuthErrorCode.databaseError,
          'Cached user data not found',
        );
      }

      return AuthResult.success(
        tokens,
        userId: user.userId,
        isOfflineAuth: true,
      );
    } on RateLimitException catch (e) {
      return AuthResult.failure(
        AuthErrorCode.accountLocked,
        e.message,
        retryAfterSeconds: e.lockoutRemaining.inSeconds,
      );
    } on Exception {
      return AuthResult.failure(
        AuthErrorCode.databaseError,
        'Something went wrong. Please try again.',
      );
    }
  }

  /// Login with automatic online/offline detection.
  ///
  /// This attempts online login first, then falls back to offline login
  /// if no connectivity is available.
  ///
  /// Params:
  /// - [phoneNumber]: User's phone number
  /// - [password]: User's password
  ///
  /// Returns [AuthResult] with tokens on success.
  Future<AuthResult> login({
    required String phoneNumber,
    required String password,
  }) async {
    // Check connectivity
    final connectivityResults = await _connectivity.checkConnectivity();
    final isOnline =
        connectivityResults.any((result) => result != ConnectivityResult.none);

    if (isOnline) {
      // Online login requires API config
      if (!Environment.isApiConfigured) {
        // Fall back to offline login if cached credentials exist
        final offlineResult = await loginOffline(
          phoneNumber: phoneNumber,
          password: password,
        );
        if (offlineResult.isSuccess) return offlineResult;

        return _notConfiguredResult();
      }

      // Try online login
      final result = await loginOnline(
        phoneNumber: phoneNumber,
        password: password,
      );

      // If online login fails due to network, try offline
      if (!result.isSuccess && result.errorCode == AuthErrorCode.networkError) {
        return await loginOffline(
          phoneNumber: phoneNumber,
          password: password,
        );
      }

      return result;
    } else {
      // No connectivity - try offline login
      return await loginOffline(
        phoneNumber: phoneNumber,
        password: password,
      );
    }
  }

  /// Refresh access token using refresh token.
  ///
  /// This should be called automatically when access token expires.
  ///
  /// Params:
  /// - [refreshToken]: Current refresh token
  ///
  /// Returns [AuthResult] with new tokens on success.
  Future<AuthResult> refreshToken(String refreshToken) async {
    if (!Environment.isApiConfigured) {
      return _notConfiguredResult();
    }

    try {
      // Check connectivity
      final connectivityResults = await _connectivity.checkConnectivity();
      if (!connectivityResults
          .any((result) => result != ConnectivityResult.none)) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'Token refresh requires internet connection',
        );
      }

      // Call refresh API
      final response = await _dio.post<Map<String, dynamic>>(
        ApiConfig.refreshEndpoint,
        data: {
          'refreshToken': refreshToken,
        },
      );

      final data = response.data as Map<String, dynamic>;

      // Server wraps tokens under "tokens" key with expiresIn (seconds)
      final tokenData = data['tokens'] as Map<String, dynamic>;
      final expiresIn = tokenData['expiresIn'] as int? ?? 3600;
      final tokens = AuthTokens(
        accessToken: tokenData['accessToken'] as String,
        refreshToken: tokenData['refreshToken'] as String? ?? refreshToken,
        idToken: tokenData['idToken'] as String,
        expiresAt: DateTime.now().add(Duration(seconds: expiresIn)),
      );

      return AuthResult.success(tokens);
    } on DioException catch (e) {
      return _handleDioError(e);
    } on Exception {
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Something went wrong. Please try again.',
      );
    }
  }

  /// Logout user and clear cached credentials.
  ///
  /// Params:
  /// - [phoneNumber]: User's phone number
  /// - [accessToken]: Current access token (for server-side logout)
  /// - [refreshToken]: Refresh token to revoke on server
  /// - [clearCache]: Whether to clear cached credentials (default: true)
  ///
  /// If [clearCache] is false, user can still log in offline.
  Future<void> logout({
    required String phoneNumber,
    String? accessToken,
    String? refreshToken,
    bool clearCache = true,
  }) async {
    try {
      // Try to logout on server (invalidate tokens)
      if (accessToken != null && refreshToken != null) {
        final connectivityResults = await _connectivity.checkConnectivity();
        final isOnline = connectivityResults
            .any((result) => result != ConnectivityResult.none);

        if (isOnline) {
          await _dio.post<Map<String, dynamic>>(
            ApiConfig.logoutEndpoint,
            data: {'refreshToken': refreshToken},
            options: Options(
              headers: {
                'Authorization': 'Bearer $accessToken',
              },
            ),
          );
        }
      }
    } on Exception {
      // Ignore logout errors (tokens will expire anyway)
    } finally {
      // Clear cached credentials if requested
      if (clearCache) {
        await _offlineCache.clearCredentials(phoneNumber);
      }
    }
  }

  // ============================================================================
  // Private methods
  // ============================================================================

  /// Return an error when API credentials are missing.
  static AuthResult _notConfiguredResult() => AuthResult.failure(
        AuthErrorCode.apiNotConfigured,
        'Server not configured. Provide API_BASE_URL, COGNITO_USER_POOL_ID, '
        'COGNITO_CLIENT_ID, and COGNITO_REGION via --dart-define.',
      );

  /// Handle DioException and map to AuthResult with error codes.
  AuthResult _handleDioError(DioException e) {
    if (e.response != null) {
      final statusCode = e.response!.statusCode;
      final data = e.response!.data;

      if (data is Map<String, dynamic>) {
        // Handle structured error response: {"error": {"code": ..., "message": ..., "retryAfterSeconds": ...}}
        if (data.containsKey('error') &&
            data['error'] is Map<String, dynamic>) {
          final error = data['error'] as Map<String, dynamic>;
          final retryAfter = error['retryAfterSeconds'];
          return AuthResult.failure(
            error['code'] as String? ?? AuthErrorCode.internalServerError,
            error['message'] as String? ?? 'Server error',
            retryAfterSeconds: retryAfter is int ? retryAfter : null,
          );
        }

        // Handle simple error response: {"message": "..."}
        if (data.containsKey('message')) {
          final message = data['message'] as String?;
          // Don't expose raw "Internal Server Error" — use friendly message
          if (statusCode != null && statusCode >= 500) {
            return AuthResult.failure(
              AuthErrorCode.internalServerError,
              'Server is temporarily unavailable. Please try again.',
            );
          }
          return AuthResult.failure(
            AuthErrorCode.internalServerError,
            message ?? 'Server error',
          );
        }
      }

      // Generic server error for 5xx without parseable body
      if (statusCode != null && statusCode >= 500) {
        return AuthResult.failure(
          AuthErrorCode.internalServerError,
          'Server is temporarily unavailable. Please try again.',
        );
      }
    }

    // Network errors
    if (e.type == DioExceptionType.connectionTimeout ||
        e.type == DioExceptionType.receiveTimeout ||
        e.type == DioExceptionType.sendTimeout) {
      return AuthResult.failure(
        AuthErrorCode.networkError,
        'Network timeout. Please check your connection.',
      );
    }

    if (e.type == DioExceptionType.connectionError ||
        e.type == DioExceptionType.unknown) {
      return AuthResult.failure(
        AuthErrorCode.networkError,
        'No internet connection.',
      );
    }

    return AuthResult.failure(
      AuthErrorCode.internalServerError,
      'Something went wrong. Please try again.',
    );
  }
}
