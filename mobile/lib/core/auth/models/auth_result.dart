import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';

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

  /// Authoritative user role resolved from the login response / cached profile
  /// (only present on success). The caller MUST use this rather than inferring
  /// a role from local cache — see F-34.
  final UserRole? role;

  /// User display name (only present on success)
  final String? userName;

  /// User woreda (only present on success)
  final String? woreda;

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

  /// Registration succeeded but the phone must be confirmed with an SMS OTP
  /// before any tokens are issued (register 202). [tokens] is null then.
  final bool otpRequired;

  /// When the pending OTP expires (register 202 / resend-otp), if known.
  final DateTime? otpExpiresAt;

  /// Fayda verification status from the server's `user` object, when the
  /// response carried one (login, register, verify-otp).
  final bool? faydaVerified;

  /// When Fayda verification happened, if the server reported it.
  final DateTime? faydaVerifiedAt;

  /// The server flagged this login as coming from a device it has not seen.
  final bool newDevice;

  const AuthResult._({
    required this.isSuccess,
    this.tokens,
    this.userId,
    this.role,
    this.userName,
    this.woreda,
    this.errorCode,
    this.errorMessage,
    this.retryAfterSeconds,
    this.isOfflineAuth = false,
    this.otpRequired = false,
    this.otpExpiresAt,
    this.faydaVerified,
    this.faydaVerifiedAt,
    this.newDevice = false,
  });

  /// Create a successful authentication result
  factory AuthResult.success(
    AuthTokens tokens, {
    String? userId,
    UserRole? role,
    String? userName,
    String? woreda,
    bool isOfflineAuth = false,
    bool? faydaVerified,
    DateTime? faydaVerifiedAt,
    bool newDevice = false,
  }) {
    return AuthResult._(
      isSuccess: true,
      tokens: tokens,
      userId: userId,
      role: role,
      userName: userName,
      woreda: woreda,
      isOfflineAuth: isOfflineAuth,
      faydaVerified: faydaVerified,
      faydaVerifiedAt: faydaVerifiedAt,
      newDevice: newDevice,
    );
  }

  /// Registration accepted; the phone must be confirmed with an SMS OTP
  /// (register 202). No tokens yet. Also used for a successful resend-otp.
  factory AuthResult.otpRequired({
    String? userId,
    DateTime? otpExpiresAt,
  }) {
    return AuthResult._(
      isSuccess: true,
      userId: userId,
      otpRequired: true,
      otpExpiresAt: otpExpiresAt,
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
      return 'AuthResult.success(userId: $userId, offline: $isOfflineAuth, '
          'otpRequired: $otpRequired)';
    } else {
      return 'AuthResult.failure(code: $errorCode, message: $errorMessage)';
    }
  }
}
