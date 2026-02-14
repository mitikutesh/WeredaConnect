import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';

import '../database/schemas/user.dart';
import 'models/auth_error.dart';
import 'models/auth_result.dart';
import 'models/auth_tokens.dart';
import 'offline_auth_cache.dart';

/// Main authentication service for WeredaConnect.
///
/// This service handles:
/// - User registration with OTP verification
/// - Online login (AWS Cognito)
/// - Offline login (cached credentials)
/// - Token refresh
/// - Logout
///
/// Flow:
/// 1. Registration: register() → verifyOtp() → auto-login
/// 2. Online login: loginOnline() → cache credentials → success
/// 3. Offline login: loginOffline() → verify cached hash → success
/// 4. Token refresh: Automatic in background when tokens expire
///
/// Ethiopian context:
/// - Phone number as primary identifier (not email)
/// - SMS OTP via Ethio Telecom
/// - Offline-first: Cached credentials work without connectivity
class AuthService {
  final Dio _dio;
  final OfflineAuthCache _offlineCache;
  final Connectivity _connectivity;

  // API base URL (should come from config)
  static const String _apiBaseUrl =
      'https://api.weredaconnect.com'; // TODO: Use from config

  AuthService({
    Dio? dio,
    OfflineAuthCache? offlineCache,
    Connectivity? connectivity,
  })  : _dio = dio ?? Dio(BaseOptions(baseUrl: _apiBaseUrl)),
        _offlineCache = offlineCache ?? OfflineAuthCache(),
        _connectivity = connectivity ?? Connectivity();

  /// Register a new user with phone number and password.
  ///
  /// This sends an SMS OTP to the user's phone number.
  /// User must call [verifyOtp] with the OTP code to complete registration.
  ///
  /// Params:
  /// - [phoneNumber]: Ethiopian phone number (+251XXXXXXXXX)
  /// - [password]: User password (minimum 6 characters)
  /// - [name]: User's full name (supports Amharic/Oromo)
  /// - [role]: User role (farmer, trucker, buyer)
  /// - [woreda]: Ethiopian woreda (district)
  /// - [preferredLanguage]: UI language (am, om, en)
  ///
  /// Returns [AuthResult] with userId and OTP expiration time.
  Future<AuthResult> register({
    required String phoneNumber,
    required String password,
    required String name,
    required UserRole role,
    required String woreda,
    String preferredLanguage = 'am',
  }) async {
    try {
      // Check connectivity
      final connectivityResult = await _connectivity.checkConnectivity();
      if (connectivityResult.contains(ConnectivityResult.none)) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'Registration requires internet connection',
        );
      }

      // Call registration API
      final response = await _dio.post(
        '/auth/register',
        data: {
          'phoneNumber': phoneNumber,
          'password': password,
          'displayName': name,
          'role': role.name,
          'woreda': woreda,
          'preferredLanguage': preferredLanguage,
        },
      );

      final data = response.data as Map<String, dynamic>;

      // Return success with userId (user must verify OTP next)
      return AuthResult.success(
        AuthTokens(
          accessToken: '', // Not available until OTP verified
          refreshToken: '',
          idToken: '',
        ),
        userId: data['userId'] as String,
      );
    } on DioException catch (e) {
      return _handleDioError(e);
    } catch (e) {
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        e.toString(),
      );
    }
  }

  /// Verify OTP code sent during registration.
  ///
  /// This completes the registration process and returns authentication tokens.
  ///
  /// Params:
  /// - [phoneNumber]: Phone number used during registration
  /// - [otp]: 6-digit OTP code from SMS
  ///
  /// Returns [AuthResult] with tokens on success.
  Future<AuthResult> verifyOtp({
    required String phoneNumber,
    required String otp,
  }) async {
    try {
      // Check connectivity
      final connectivityResult = await _connectivity.checkConnectivity();
      if (connectivityResult.contains(ConnectivityResult.none)) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'OTP verification requires internet connection',
        );
      }

      // Call OTP verification API
      final response = await _dio.post(
        '/auth/verify-otp',
        data: {
          'phoneNumber': phoneNumber,
          'otp': otp,
        },
      );

      final data = response.data as Map<String, dynamic>;

      // Extract tokens
      final tokens = AuthTokens(
        accessToken: data['accessToken'] as String,
        refreshToken: data['refreshToken'] as String,
        idToken: data['idToken'] as String,
        expiresAt: DateTime.parse(data['expiresAt'] as String),
      );

      // Cache credentials for offline use
      // Note: We don't have the password here, so we can't cache it
      // User must log in at least once to enable offline mode

      return AuthResult.success(tokens, userId: data['userId'] as String);
    } on DioException catch (e) {
      return _handleDioError(e);
    } catch (e) {
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        e.toString(),
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
    try {
      // Check connectivity
      final connectivityResult = await _connectivity.checkConnectivity();
      if (connectivityResult.contains(ConnectivityResult.none)) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'Online login requires internet connection',
        );
      }

      // Call login API
      final response = await _dio.post(
        '/auth/login',
        data: {
          'phoneNumber': phoneNumber,
          'password': password,
        },
      );

      final data = response.data as Map<String, dynamic>;

      // Extract tokens
      final tokens = AuthTokens(
        accessToken: data['accessToken'] as String,
        refreshToken: data['refreshToken'] as String,
        idToken: data['idToken'] as String,
        expiresAt: DateTime.parse(data['expiresAt'] as String),
      );

      final user = data['user'] as Map<String, dynamic>;

      // Cache credentials for offline use
      await _offlineCache.cacheCredentials(
        phoneNumber: phoneNumber,
        password: password,
        userId: user['userId'] as String,
        role: UserRole.values.firstWhere(
          (r) => r.name == user['role'],
        ),
        name: user['name'] as String,
        woreda: user['woreda'] as String,
        tokens: tokens,
      );

      return AuthResult.success(tokens, userId: user['userId'] as String);
    } on DioException catch (e) {
      return _handleDioError(e);
    } catch (e) {
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        e.toString(),
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
    } catch (e) {
      return AuthResult.failure(
        AuthErrorCode.databaseError,
        e.toString(),
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
    final connectivityResult = await _connectivity.checkConnectivity();
    final isOnline = !connectivityResult.contains(ConnectivityResult.none);

    if (isOnline) {
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
    try {
      // Check connectivity
      final connectivityResult = await _connectivity.checkConnectivity();
      if (connectivityResult.contains(ConnectivityResult.none)) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'Token refresh requires internet connection',
        );
      }

      // Call refresh API
      final response = await _dio.post(
        '/auth/refresh',
        data: {
          'refreshToken': refreshToken,
        },
      );

      final data = response.data as Map<String, dynamic>;

      // Extract new tokens
      final tokens = AuthTokens(
        accessToken: data['accessToken'] as String,
        refreshToken: data['refreshToken'] as String,
        idToken: data['idToken'] as String,
        expiresAt: DateTime.parse(data['expiresAt'] as String),
      );

      return AuthResult.success(tokens);
    } on DioException catch (e) {
      return _handleDioError(e);
    } catch (e) {
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        e.toString(),
      );
    }
  }

  /// Logout user and clear cached credentials.
  ///
  /// Params:
  /// - [phoneNumber]: User's phone number
  /// - [accessToken]: Current access token (for server-side logout)
  /// - [clearCache]: Whether to clear cached credentials (default: true)
  ///
  /// If [clearCache] is false, user can still log in offline.
  Future<void> logout({
    required String phoneNumber,
    String? accessToken,
    bool clearCache = true,
  }) async {
    try {
      // Try to logout on server (invalidate tokens)
      if (accessToken != null) {
        final connectivityResult = await _connectivity.checkConnectivity();
        final isOnline = !connectivityResult.contains(ConnectivityResult.none);

        if (isOnline) {
          await _dio.post(
            '/auth/logout',
            options: Options(
              headers: {
                'Authorization': 'Bearer $accessToken',
              },
            ),
          );
        }
      }
    } catch (e) {
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

  /// Handle DioException and map to AuthResult with error codes.
  AuthResult _handleDioError(DioException e) {
    if (e.response != null) {
      final data = e.response!.data;

      if (data is Map<String, dynamic> && data.containsKey('error')) {
        final error = data['error'] as Map<String, dynamic>;
        return AuthResult.failure(
          error['code'] as String? ?? AuthErrorCode.internalServerError,
          error['message'] as String? ?? 'Server error',
        );
      }
    }

    // Network errors
    if (e.type == DioExceptionType.connectionTimeout ||
        e.type == DioExceptionType.receiveTimeout ||
        e.type == DioExceptionType.sendTimeout) {
      return AuthResult.failure(
        AuthErrorCode.networkError,
        'Network timeout. Please check your connection',
      );
    }

    if (e.type == DioExceptionType.unknown) {
      return AuthResult.failure(
        AuthErrorCode.networkError,
        'No internet connection',
      );
    }

    return AuthResult.failure(
      AuthErrorCode.internalServerError,
      e.message ?? 'Unknown error',
    );
  }
}
