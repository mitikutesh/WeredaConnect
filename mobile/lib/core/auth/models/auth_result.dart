import 'package:wereda_connect/core/auth/models/auth_tokens.dart';

/// Result type for authentication operations.
///
/// This follows the Railway Oriented Programming pattern:
/// - Success: Contains tokens and user data
/// - Failure: Contains error code and message
///
/// Usage:
/// ```dart
/// final result = await authService.login(phone, password);
/// if (result.isSuccess) {
///   // Navigate to home screen
///   final tokens = result.tokens!;
/// } else {
///   // Show error message
///   showError(result.errorMessage);
/// }
/// ```
class AuthResult {
  /// Whether the operation succeeded
  final bool isSuccess;

  /// Authentication tokens (only present on success)
  final AuthTokens? tokens;

  /// User ID from Cognito (only present on success)
  final String? userId;

  /// Error code matching server API contract (only present on failure)
  /// See specs/contracts/auth-api.md for error code reference
  final String? errorCode;

  /// Human-readable error message (only present on failure)
  final String? errorMessage;

  /// Seconds until account lockout expires (only for AUTH_015)
  final int? retryAfterSeconds;

  /// Whether this was an offline authentication
  /// (used to show "offline mode" indicator in UI)
  final bool isOfflineAuth;

  const AuthResult._({
    required this.isSuccess,
    this.tokens,
    this.userId,
    this.errorCode,
    this.errorMessage,
    this.retryAfterSeconds,
    this.isOfflineAuth = false,
  });

  /// Create a successful authentication result
  factory AuthResult.success(
    AuthTokens tokens, {
    String? userId,
    bool isOfflineAuth = false,
  }) {
    return AuthResult._(
      isSuccess: true,
      tokens: tokens,
      userId: userId,
      isOfflineAuth: isOfflineAuth,
    );
  }

  /// Create a failed authentication result
  factory AuthResult.failure(
    String errorCode,
    String errorMessage, {
    int? retryAfterSeconds,
  }) {
    return AuthResult._(
      isSuccess: false,
      errorCode: errorCode,
      errorMessage: errorMessage,
      retryAfterSeconds: retryAfterSeconds,
    );
  }

  /// Create a network error result (offline and no cached credentials)
  factory AuthResult.networkError() {
    return AuthResult.failure(
      'NETWORK_ERROR',
      'No internet connection and no cached credentials available',
    );
  }

  /// Create an offline-not-available result
  factory AuthResult.offlineNotAvailable() {
    return AuthResult.failure(
      'OFFLINE_NOT_AVAILABLE',
      'Please log in online first to enable offline mode',
    );
  }

  @override
  String toString() {
    if (isSuccess) {
      return 'AuthResult.success(userId: $userId, offline: $isOfflineAuth)';
    } else {
      return 'AuthResult.failure(code: $errorCode, message: $errorMessage)';
    }
  }
}
