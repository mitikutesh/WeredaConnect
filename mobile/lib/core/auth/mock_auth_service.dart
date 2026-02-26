import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/models/auth_result.dart';
import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/auth/offline_auth_cache.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';

/// Temporary storage for registration data between register() and login().
class _PendingRegistration {
  final String name;
  final UserRole role;
  final String woreda;
  final String password;

  _PendingRegistration({
    required this.name,
    required this.role,
    required this.woreda,
    required this.password,
  });
}

/// Mock authentication service for local development.
///
/// This service simulates the real auth flow without requiring AWS Cognito.
/// It's useful for:
/// - UI development and testing
/// - Offline development without server dependency
/// - Testing auth flows without SMS costs
///
/// IMPORTANT: This should NEVER be used in production!
class MockAuthService {
  final OfflineAuthCache _offlineCache;

  /// Tracks users who registered but haven't completed login yet.
  /// Keyed by phone number. Cleared after first successful login.
  final Map<String, _PendingRegistration> _pendingRegistrations = {};

  MockAuthService({OfflineAuthCache? offlineCache})
      : _offlineCache = offlineCache ?? OfflineAuthCache();

  /// Mock OTP code that's always accepted
  static const String validOtpCode = '123456';

  /// Mock registration - simulates successful registration
  Future<AuthResult> register({
    required String phoneNumber,
    required String password,
    required String name,
    required UserRole role,
    required String woreda,
    String preferredLanguage = 'am',
  }) async {
    // Simulate network delay
    await Future<void>.delayed(const Duration(milliseconds: 500));

    // Check if phone number is already registered
    final hasCached = await _offlineCache.hasCachedCredentials(phoneNumber);
    if (hasCached) {
      return AuthResult.failure(
        AuthErrorCode.phoneAlreadyRegistered,
        'Phone number already registered',
      );
    }

    // Store registration data for use during post-OTP login
    _pendingRegistrations[phoneNumber] = _PendingRegistration(
      name: name,
      role: role,
      woreda: woreda,
      password: password,
    );

    // Generate a mock user ID
    final userId = 'mock_${phoneNumber.replaceAll('+', '')}';

    // Return success - OTP required
    return AuthResult.success(
      const AuthTokens(
        accessToken: '',
        refreshToken: '',
        idToken: '',
      ),
      userId: userId,
    );
  }

  /// Mock OTP verification - only accepts [validOtpCode] ('123456')
  Future<AuthResult> verifyOtp({
    required String phoneNumber,
    required String otp,
  }) async {
    // Simulate network delay
    await Future<void>.delayed(const Duration(milliseconds: 500));

    // Only accept the documented mock OTP code
    if (otp != validOtpCode) {
      return AuthResult.failure(
        AuthErrorCode.invalidOtp,
        'Invalid OTP code. Hint: use $validOtpCode in mock mode.',
      );
    }

    // Match real service behavior: OTP verification returns no tokens.
    // Caller must call login() after verification.
    final userId = 'mock_${phoneNumber.replaceAll('+', '')}';
    return AuthResult.otpVerified(userId: userId);
  }

  /// Mock login - verifies cached credentials or creates first-time session
  Future<AuthResult> login({
    required String phoneNumber,
    required String password,
  }) async {
    // Simulate network delay
    await Future<void>.delayed(const Duration(milliseconds: 500));

    // Check if we have cached credentials
    final hasCached = await _offlineCache.hasCachedCredentials(phoneNumber);

    if (hasCached) {
      // Verify password against cached hash (may throw RateLimitException)
      try {
        final isValid = await _offlineCache.verifyPassword(
          phoneNumber,
          password,
        );

        if (isValid) {
          final user = await _offlineCache.getCachedUser(phoneNumber);
          final tokens = await _offlineCache.getCachedTokens(phoneNumber);

          if (user != null && tokens != null) {
            return AuthResult.success(
              tokens,
              userId: user.userId,
              isOfflineAuth: true,
            );
          }
        }

        // Password mismatch — return failure (don't create new session)
        return AuthResult.failure(
          AuthErrorCode.invalidCredentials,
          'Invalid phone number or password',
        );
      } on RateLimitException catch (e) {
        return AuthResult.failure(
          AuthErrorCode.accountLocked,
          e.message,
          retryAfterSeconds: e.lockoutRemaining.inSeconds,
        );
      }
    }

    // No cached credentials — check if user completed registration
    final pending = _pendingRegistrations[phoneNumber];
    if (pending == null) {
      // User never registered — reject login
      return AuthResult.failure(
        AuthErrorCode.invalidCredentials,
        'Invalid phone number or password',
      );
    }

    // Verify password matches what was used during registration
    if (pending.password != password) {
      return AuthResult.failure(
        AuthErrorCode.invalidCredentials,
        'Invalid phone number or password',
      );
    }

    // Create session with registration data
    final userId = 'mock_${phoneNumber.replaceAll('+', '')}';
    final now = DateTime.now();
    final tokens = AuthTokens(
      accessToken: 'mock_access_token_$userId',
      refreshToken: 'mock_refresh_token_$userId',
      idToken: 'mock_id_token_$userId',
      expiresAt: now.add(const Duration(hours: 1)),
    );

    // Cache credentials with the actual role from registration
    await _offlineCache.cacheCredentials(
      phoneNumber: phoneNumber,
      password: password,
      userId: userId,
      role: pending.role,
      name: pending.name,
      woreda: pending.woreda,
      tokens: tokens,
    );

    // Clear pending data — credentials are now cached
    _pendingRegistrations.remove(phoneNumber);

    return AuthResult.success(tokens, userId: userId);
  }

  /// Mock token refresh
  Future<AuthResult> refreshToken(String refreshToken) async {
    await Future<void>.delayed(const Duration(milliseconds: 300));

    final now = DateTime.now();
    final tokens = AuthTokens(
      accessToken: 'mock_refreshed_access_token',
      refreshToken: refreshToken,
      idToken: 'mock_refreshed_id_token',
      expiresAt: now.add(const Duration(hours: 1)),
    );

    return AuthResult.success(tokens);
  }

  /// Mock logout
  Future<void> logout({
    required String phoneNumber,
    String? accessToken,
    bool clearCache = true,
  }) async {
    await Future<void>.delayed(const Duration(milliseconds: 200));

    if (clearCache) {
      await _offlineCache.clearCredentials(phoneNumber);
    }
  }
}
