import 'dart:convert';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/auth/device_identity.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/models/auth_result.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart'
    if (dart.library.html) 'package:wereda_connect/core/auth/offline_auth_cache_web.dart';
import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';
import 'package:wereda_connect/core/network/api_exception.dart'
    show isSecureConnectionFailure;
import 'package:wereda_connect/core/network/json_body.dart';

/// Resolve the authoritative [UserRole] from a server-provided role string.
///
/// F-34: matches case-insensitively against [UserRole.values] and returns
/// `null` for an unrecognized or missing role, so the caller fails the login
/// loudly rather than silently defaulting to farmer. Extracted from
/// [AuthService.loginOnline] so this fail-closed logic stays unit-testable
/// without the `--dart-define`-gated online login path.
@visibleForTesting
UserRole? resolveServerRole(String? rawRole) {
  final roleStr = rawRole?.toLowerCase();
  for (final r in UserRole.values) {
    if (r.name.toLowerCase() == roleStr) {
      return r;
    }
  }
  return null;
}

/// Parse the server's `user.faydaVerifiedAt` (ISO-8601) into UTC.
@visibleForTesting
DateTime? parseServerTimestamp(Object? raw) {
  if (raw is! String || raw.isEmpty) return null;
  return DateTime.tryParse(raw)?.toUtc();
}

/// Whether the JWT [token] has expired, by its `exp` claim, allowing [skew]
/// for clock drift. A token that cannot be decoded counts as not expired:
/// the server is the judge, and a refusal is handled by the caller.
@visibleForTesting
bool isJwtExpired(
  String token, {
  DateTime? now,
  Duration skew = const Duration(seconds: 30),
}) {
  final parts = token.split('.');
  if (parts.length != 3) return false;
  try {
    final payload = jsonDecode(
      utf8.decode(base64Url.decode(base64Url.normalize(parts[1]))),
    );
    final exp = payload is Map<String, dynamic> ? payload['exp'] : null;
    if (exp is! num) return false;
    final expiresAt =
        DateTime.fromMillisecondsSinceEpoch(exp.toInt() * 1000, isUtc: true);
    return !expiresAt.isAfter((now ?? DateTime.now()).toUtc().add(skew));
  } on Object {
    return false;
  }
}

/// Decode a response body that may arrive as a raw JSON String (web).
Map<String, dynamic> _asMap(Object? data) =>
    requireJsonMap(data, what: 'auth response');

/// Parse the `tokens` object shared by login, register, verify-otp, refresh.
AuthTokens _parseTokens(Map<String, dynamic> data, {String? fallbackRefresh}) {
  final tokenData = data['tokens'] as Map<String, dynamic>;
  final expiresIn = (tokenData['expiresIn'] as num?)?.toInt() ?? 3600;
  return AuthTokens(
    accessToken: tokenData['accessToken'] as String,
    refreshToken: tokenData['refreshToken'] as String? ?? fallbackRefresh ?? '',
    idToken: tokenData['idToken'] as String,
    expiresAt: DateTime.now().add(Duration(seconds: expiresIn)),
  );
}

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
  final DeviceIdentity _deviceIdentity;
  final bool? _apiConfiguredOverride;

  /// Timeout for the best-effort push token unregister on logout.
  static const deviceTokenUnregisterTimeout = Duration(seconds: 3);

  AuthService({
    Dio? dio,
    OfflineAuthCache? offlineCache,
    Connectivity? connectivity,
    DeviceIdentity? deviceIdentity,
    @visibleForTesting bool? apiConfigured,
  })  : _dio = dio ?? ApiClient().dio,
        _offlineCache = offlineCache ?? OfflineAuthCache(),
        _connectivity = connectivity ?? Connectivity(),
        _deviceIdentity = deviceIdentity ?? DeviceIdentity(),
        _apiConfiguredOverride = apiConfigured {
    // Initialize ApiClient with base URL (cert pinning disabled by default)
    if (dio == null) {
      ApiClient().initialize();
    }
  }

  bool get _isApiConfigured =>
      _apiConfiguredOverride ?? Environment.isApiConfigured;

  Future<bool> _isOnline() async {
    final results = await _connectivity.checkConnectivity();
    return results.any((result) => result != ConnectivityResult.none);
  }

  Future<Options> _deviceOptions() async =>
      Options(headers: await _deviceIdentity.headers());

  /// Register a new user with phone number, password, and Fayda verification.
  ///
  /// When a [faydaVerificationToken] is supplied and Fayda's phone matches,
  /// the server answers 201 with tokens. Otherwise it answers 202 and the
  /// phone must be confirmed with an SMS OTP: the result then has
  /// [AuthResult.otpRequired] set and no tokens (call [verifyOtp] next).
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
    String? drivingLicensePath,
    String? buyerType,
    String? companyName,
  }) async {
    if (!_isApiConfigured) {
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
      // F-32: buyer identity fields are persisted server-side. The trucker
      // driving-license is intentionally NOT sent — it is a local device file
      // path kept only in Isar until an image-upload pipeline exists (scope A).
      if (buyerType != null) {
        data['buyerType'] = buyerType;
      }
      if (companyName != null) {
        data['companyName'] = companyName;
      }

      final response = await _dio.post<dynamic>(
        ApiConfig.registerEndpoint,
        data: data,
        options: await _deviceOptions(),
      );

      final responseData = _asMap(response.data);

      // 202: account created but the phone must be confirmed by SMS OTP.
      // No tokens are issued yet, so nothing is cached here.
      if (response.statusCode == 202 || responseData['otpRequired'] == true) {
        return AuthResult.otpRequired(
          userId: responseData['userId'] as String?,
          otpExpiresAt: parseServerTimestamp(responseData['otpExpiresAt']),
        );
      }

      // 201: Fayda-verified registration returns tokens directly.
      final tokens = _parseTokens(responseData);

      final user = responseData['user'] as Map<String, dynamic>;
      final userId = user['userId'] as String;
      final fullName = user['fullName'] as String?;
      final faydaVerified = user['faydaVerified'] as bool?;
      final faydaVerifiedAt = parseServerTimestamp(user['faydaVerifiedAt']);

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
          fullName: fullName,
          displayName: name,
          drivingLicensePath: drivingLicensePath,
          buyerType: buyerType,
          companyName: companyName,
          faydaVerified: faydaVerified,
          faydaVerifiedAt: faydaVerifiedAt,
        );
      } on Object catch (e) {
        debugPrint('[AuthService] Failed to cache credentials: $e');
      }

      return AuthResult.success(
        tokens,
        userId: userId,
        role: role,
        userName: name,
        woreda: woreda,
        faydaVerified: faydaVerified,
        faydaVerifiedAt: faydaVerifiedAt,
      );
    } on DioException catch (e) {
      return _handleDioError(e);
    } on Object catch (e) {
      debugPrint('[AuthService] register exception: $e');
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
    if (!_isApiConfigured) {
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
      final response = await _dio.post<dynamic>(
        ApiConfig.loginEndpoint,
        data: {
          'phoneNumber': phoneNumber,
          'password': password,
        },
        options: await _deviceOptions(),
      );

      return await _completeSignIn(
        _asMap(response.data),
        phoneNumber: phoneNumber,
        password: password,
      );
    } on DioException catch (e) {
      return _handleDioError(e);
    } on Object catch (e, stackTrace) {
      debugPrint('[AuthService] loginOnline exception: $e');
      debugPrint('[AuthService] stackTrace: $stackTrace');
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Something went wrong. Please try again.',
      );
    }
  }

  /// Confirm the registration SMS OTP (POST /auth/verify-otp).
  ///
  /// Returns tokens exactly like a login. [password] (kept in memory by the
  /// caller since the register or login call, never persisted) is sent with
  /// the code because the server signs in with it, and it also builds the
  /// offline credential cache. Without it only the profile and tokens are
  /// cached. The optional
  /// role-specific fields are the local-only registration extras.
  Future<AuthResult> verifyOtp({
    required String phoneNumber,
    required String otpCode,
    String? password,
    String? drivingLicensePath,
    String? buyerType,
    String? companyName,
  }) async {
    if (!_isApiConfigured) {
      return _notConfiguredResult();
    }

    try {
      if (!await _isOnline()) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'Verification requires internet connection',
        );
      }

      final response = await _dio.post<dynamic>(
        ApiConfig.verifyOtpEndpoint,
        // The server signs the user in with the password once the OTP
        // confirms, so it must travel with the code.
        data: {
          'phoneNumber': phoneNumber,
          'otpCode': otpCode,
          if (password != null) 'password': password,
        },
        options: await _deviceOptions(),
      );

      return await _completeSignIn(
        _asMap(response.data),
        phoneNumber: phoneNumber,
        password: password,
        drivingLicensePath: drivingLicensePath,
        buyerType: buyerType,
        companyName: companyName,
      );
    } on DioException catch (e) {
      return _handleDioError(e);
    } on Object catch (e) {
      debugPrint('[AuthService] verifyOtp exception: $e');
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Something went wrong. Please try again.',
      );
    }
  }

  /// Ask the server to send a fresh registration OTP (POST /auth/resend-otp).
  ///
  /// On success the result has [AuthResult.otpRequired] set and carries the
  /// new [AuthResult.otpExpiresAt].
  Future<AuthResult> resendOtp({required String phoneNumber}) async {
    if (!_isApiConfigured) {
      return _notConfiguredResult();
    }

    try {
      if (!await _isOnline()) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'Resending the code requires internet connection',
        );
      }

      final response = await _dio.post<dynamic>(
        ApiConfig.resendOtpEndpoint,
        data: {'phoneNumber': phoneNumber},
      );
      final data = _asMap(response.data);
      return AuthResult.otpRequired(
        otpExpiresAt: parseServerTimestamp(data['otpExpiresAt']),
      );
    } on DioException catch (e) {
      return _handleDioError(e);
    } on Object catch (e) {
      debugPrint('[AuthService] resendOtp exception: $e');
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Something went wrong. Please try again.',
      );
    }
  }

  /// Best-effort: tell the server to stop sending pushes for [token]
  /// (DELETE /device-tokens). Bounded by [deviceTokenUnregisterTimeout];
  /// every failure is swallowed because logout must never depend on it.
  Future<void> unregisterDeviceToken(String token) async {
    if (!_isApiConfigured || token.isEmpty) return;
    try {
      await _dio
          .delete<dynamic>(
            ApiConfig.deviceTokensEndpoint,
            data: {'token': token},
            options: Options(
              sendTimeout: deviceTokenUnregisterTimeout,
              receiveTimeout: deviceTokenUnregisterTimeout,
            ),
          )
          .timeout(deviceTokenUnregisterTimeout);
    } on Object catch (e) {
      debugPrint('[AuthService] device token unregister skipped: $e');
    }
  }

  /// Shared tail of login and verify-otp: parse tokens + `user`, resolve the
  /// role (fail closed), cache for offline use, and build the result.
  Future<AuthResult> _completeSignIn(
    Map<String, dynamic> data, {
    required String phoneNumber,
    String? password,
    String? drivingLicensePath,
    String? buyerType,
    String? companyName,
  }) async {
    final tokens = _parseTokens(data);

    final user = data['user'] as Map<String, dynamic>;
    final userId = user['userId'] as String;
    final fullName = user['fullName'] as String?;
    final displayName = (user['displayName'] as String?) ?? '';
    final woreda = user['woreda'] as String;
    final faydaVerified = user['faydaVerified'] as bool?;
    final faydaVerifiedAt = parseServerTimestamp(user['faydaVerifiedAt']);
    final newDevice = data['newDevice'] == true;
    if (newDevice) {
      debugPrint('[AuthService] server reports a sign-in from a new device');
    }

    // F-34: resolve the authoritative role from the server response BEFORE
    // caching. An unrecognized/missing role must fail the login loudly, not
    // be swallowed by the best-effort cache block below, which previously let
    // the BLoC silently default the role to farmer.
    final role = resolveServerRole(user['role'] as String?);
    if (role == null) {
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Login failed: the server returned an unrecognized account role.',
      );
    }

    // Cache for offline use (best-effort, never blocks login)
    try {
      if (password != null) {
        await _offlineCache.cacheCredentials(
          phoneNumber: phoneNumber,
          password: password,
          userId: userId,
          role: role,
          name: displayName,
          woreda: woreda,
          tokens: tokens,
          fullName: fullName,
          displayName: displayName,
          drivingLicensePath: drivingLicensePath,
          buyerType: buyerType ?? user['buyerType'] as String?,
          companyName: companyName ?? user['companyName'] as String?,
          faydaVerified: faydaVerified,
          faydaVerifiedAt: faydaVerifiedAt,
        );
      } else {
        // No password in memory (e.g. OTP confirmed after an app restart):
        // keep the profile and tokens so the session survives, offline
        // password login becomes available after the next online login.
        await _offlineCache.saveUserToIsar(
          userId: userId,
          phoneNumber: phoneNumber,
          name: displayName,
          role: role,
          woreda: woreda,
          fullName: fullName,
          displayName: displayName,
          drivingLicensePath: drivingLicensePath,
          buyerType: buyerType ?? user['buyerType'] as String?,
          companyName: companyName ?? user['companyName'] as String?,
          faydaVerified: faydaVerified,
          faydaVerifiedAt: faydaVerifiedAt,
        );
        await _offlineCache.updateTokens(phoneNumber, tokens);
      }
    } on Object catch (e) {
      debugPrint('[AuthService] Failed to cache credentials: $e');
    }

    return AuthResult.success(
      tokens,
      userId: userId,
      role: role,
      userName: displayName,
      woreda: woreda,
      faydaVerified: faydaVerified,
      faydaVerifiedAt: faydaVerifiedAt,
      newDevice: newDevice,
    );
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
        role: user.role,
        userName: user.name,
        woreda: user.woreda,
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
    try {
      // Check connectivity
      final connectivityResults = await _connectivity.checkConnectivity();
      final isOnline = connectivityResults
          .any((result) => result != ConnectivityResult.none);

      if (isOnline) {
        // Online login requires API config
        if (!_isApiConfigured) {
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
        if (!result.isSuccess &&
            result.errorCode == AuthErrorCode.networkError) {
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
    } on Object catch (e) {
      debugPrint('[AuthService] login failed unexpectedly: $e');
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Something went wrong. Please try again.',
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
    if (!_isApiConfigured) {
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
      final response = await _dio.post<dynamic>(
        ApiConfig.refreshEndpoint,
        data: {
          'refreshToken': refreshToken,
        },
      );

      final data = _asMap(response.data);

      // Server wraps tokens under "tokens" key with expiresIn (seconds)
      final tokens = _parseTokens(data, fallbackRefresh: refreshToken);

      return AuthResult.success(tokens);
    } on DioException catch (e) {
      return _handleDioError(e);
    } on Object {
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Something went wrong. Please try again.',
      );
    }
  }

  /// Revoke the session's refresh token on the server, then clear the
  /// offline credentials if [clearCache] is true (false keeps offline login).
  ///
  /// Self-contained (SIM-55): it never reads or writes the stored token
  /// slots, and the AuthInterceptor leaves `/auth/logout` alone, so the
  /// caller can clear the local session while this runs. The gateway answers
  /// 401 to an expired bearer, so an [accessToken] that is missing or past its
  /// `exp` is replaced by one minted from [refreshToken] (POST /auth/refresh,
  /// nothing stored). A 401 or AUTH_026 (the bearer's user does not own
  /// [refreshToken]) is retried once the same way: a bearer minted from the
  /// refresh token always belongs to its owner. When the mint is refused,
  /// the token is already dead and nothing is left to revoke.
  ///
  /// Offline, or without a refresh token, no request is made. Every failure
  /// is swallowed: logout must never depend on the network.
  Future<void> logout({
    required String phoneNumber,
    String? accessToken,
    String? refreshToken,
    bool clearCache = true,
  }) async {
    try {
      if (refreshToken != null &&
          refreshToken.isNotEmpty &&
          await _isOnline()) {
        await _revokeRefreshToken(refreshToken, accessToken);
      }
    } on Object catch (e) {
      // Ignore logout errors (the refresh token expires on its own). `on
      // Object` so a TypeError (an Error, not an Exception) cannot escape
      // and abort the caller's logout flow.
      debugPrint('[AuthService] server-side logout failed: $e');
    } finally {
      // Clear cached credentials if requested
      if (clearCache) {
        await _offlineCache.clearCredentials(phoneNumber);
      }
    }
  }

  Future<void> _revokeRefreshToken(
    String refreshToken,
    String? accessToken,
  ) async {
    if (accessToken != null &&
        accessToken.isNotEmpty &&
        !isJwtExpired(accessToken)) {
      try {
        await _postLogout(bearer: accessToken, refreshToken: refreshToken);
        return;
      } on DioException catch (e) {
        if (!_bearerCannotRevoke(e)) rethrow;
      }
    }

    final minted = await this.refreshToken(refreshToken);
    if (!minted.isSuccess) {
      debugPrint(
        '[AuthService] logout: no bearer from the refresh token '
        '(${minted.errorCode}), nothing revoked',
      );
      return;
    }
    await _postLogout(
      bearer: minted.tokens!.accessToken,
      refreshToken: refreshToken,
    );
  }

  /// A 401 (expired or rejected bearer) or AUTH_026 (bearer of another user):
  /// a bearer minted from the refresh token can still revoke it.
  bool _bearerCannotRevoke(DioException e) =>
      e.response?.statusCode == 401 ||
      _handleDioError(e).errorCode == AuthErrorCode.refreshTokenNotOwned;

  Future<void> _postLogout({
    required String bearer,
    required String refreshToken,
  }) =>
      _dio.post<dynamic>(
        ApiConfig.logoutEndpoint,
        data: {'refreshToken': refreshToken},
        options: Options(headers: {'Authorization': 'Bearer $bearer'}),
      );

  /// Request password reset — sends verification code to user's phone.
  Future<AuthResult> forgotPassword({
    required String phoneNumber,
  }) async {
    if (!_isApiConfigured) {
      return _notConfiguredResult();
    }

    try {
      final connectivityResults = await _connectivity.checkConnectivity();
      if (!connectivityResults
          .any((result) => result != ConnectivityResult.none)) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'Password reset requires internet connection',
        );
      }

      await _dio.post<Map<String, dynamic>>(
        ApiConfig.forgotPasswordEndpoint,
        data: {'phoneNumber': phoneNumber},
      );

      return AuthResult.success(
        const AuthTokens(
          accessToken: '',
          refreshToken: '',
          idToken: '',
        ),
      );
    } on DioException catch (e) {
      return _handleDioError(e);
    } on Exception {
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Something went wrong. Please try again.',
      );
    }
  }

  /// Reset password with verification code.
  Future<AuthResult> resetPassword({
    required String phoneNumber,
    required String confirmationCode,
    required String newPassword,
  }) async {
    if (!_isApiConfigured) {
      return _notConfiguredResult();
    }

    try {
      final connectivityResults = await _connectivity.checkConnectivity();
      if (!connectivityResults
          .any((result) => result != ConnectivityResult.none)) {
        return AuthResult.failure(
          AuthErrorCode.networkError,
          'Password reset requires internet connection',
        );
      }

      await _dio.post<Map<String, dynamic>>(
        ApiConfig.resetPasswordEndpoint,
        data: {
          'phoneNumber': phoneNumber,
          'confirmationCode': confirmationCode,
          'newPassword': newPassword,
        },
      );

      return AuthResult.success(
        const AuthTokens(
          accessToken: '',
          refreshToken: '',
          idToken: '',
        ),
      );
    } on DioException catch (e) {
      return _handleDioError(e);
    } on Exception {
      return AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Something went wrong. Please try again.',
      );
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
      Object? data = e.response!.data;
      if (data is String) {
        try {
          data = _asMap(data);
        } on Object {
          // Not JSON: fall through to the status-code handling below.
        }
      }

      if (data is Map<String, dynamic>) {
        // Handle structured error response: {"error": {"code": ..., "message": ..., "retryAfterSeconds": ...}}
        if (data.containsKey('error') &&
            data['error'] is Map<String, dynamic>) {
          final error = data['error'] as Map<String, dynamic>;
          final retryAfter = error['retryAfterSeconds'];
          return AuthResult.failure(
            error['code'] as String? ?? AuthErrorCode.internalServerError,
            error['message'] as String? ?? 'Server error',
            retryAfterSeconds: retryAfter is num ? retryAfter.toInt() : null,
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

    // A rejected certificate chain (pinning) arrives as `unknown` wrapping a
    // HandshakeException. It used to read as "no internet", which sent
    // login() into the offline fallback and the user saw "log in online
    // first" while online (SIM-23). It is its own, non-fallback failure.
    if (isSecureConnectionFailure(e)) {
      return AuthResult.failure(
        AuthErrorCode.secureConnectionFailed,
        'Secure connection to the server failed.',
      );
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
