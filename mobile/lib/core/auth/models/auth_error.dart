/// Authentication error codes matching server API contract.
///
/// See specs/contracts/auth-api.md for complete error documentation.
///
/// These error codes are returned by the server and used in the mobile app
/// to show localized error messages to users.
class AuthErrorCode {
  // Registration errors (AUTH_001 - AUTH_010)
  static const invalidPhoneFormat = 'AUTH_001';
  static const invalidPassword = 'AUTH_002';
  static const invalidRole = 'AUTH_003';
  static const woredaRequired = 'AUTH_004';
  static const phoneAlreadyRegistered = 'AUTH_005';
  static const rateLimitExceeded = 'AUTH_006';
  static const internalServerError = 'AUTH_007';
  static const cognitoUnavailable = 'AUTH_008';

  // OTP verification errors (AUTH_011 - AUTH_020)
  static const invalidOtp = 'AUTH_011';
  static const otpExpired = 'AUTH_012';
  static const otpAttemptsExceeded = 'AUTH_013';
  static const userNotFound = 'AUTH_014';

  static const accountLocked = 'AUTH_015';

  // Login errors (AUTH_021 - AUTH_030)
  static const invalidCredentials = 'AUTH_021';
  static const userNotConfirmed = 'AUTH_022';
  static const accountDisabled = 'AUTH_023';
  static const passwordResetRequired = 'AUTH_024';

  // Token errors (AUTH_031 - AUTH_040)
  static const invalidToken = 'AUTH_031';
  static const tokenExpired = 'AUTH_032';
  static const refreshTokenExpired = 'AUTH_033';

  // Mobile-specific errors (not from server)
  static const apiNotConfigured = 'API_NOT_CONFIGURED';
  static const networkError = 'NETWORK_ERROR';
  static const offlineNotAvailable = 'OFFLINE_NOT_AVAILABLE';
  static const secureStorageError = 'SECURE_STORAGE_ERROR';
  static const databaseError = 'DATABASE_ERROR';
}

/// Extension methods for mapping error codes to user-friendly messages.
///
/// These messages are used as fallbacks when localized strings are not available.
/// Proper localization should be done in .arb files.
extension AuthErrorCodeExtension on String {
  /// Get a user-friendly error message for this error code.
  /// This is a fallback - use localized strings in production.
  String get userMessage {
    switch (this) {
      // Registration errors
      case AuthErrorCode.invalidPhoneFormat:
        return 'Invalid phone number format. Use Ethiopian format: +251XXXXXXXXX';
      case AuthErrorCode.invalidPassword:
        return 'Password must be at least 8 characters with a lowercase letter and a number';
      case AuthErrorCode.invalidRole:
        return 'Please select a role: farmer, trucker, or buyer';
      case AuthErrorCode.woredaRequired:
        return 'Please enter your woreda (district)';
      case AuthErrorCode.phoneAlreadyRegistered:
        return 'This phone number is already registered';
      case AuthErrorCode.rateLimitExceeded:
        return 'Too many attempts. Please try again later';

      // OTP errors
      case AuthErrorCode.invalidOtp:
        return 'Invalid verification code';
      case AuthErrorCode.otpExpired:
        return 'Verification code expired. Request a new one';
      case AuthErrorCode.otpAttemptsExceeded:
        return 'Too many failed attempts. Request a new code';
      case AuthErrorCode.userNotFound:
        return 'User not found. Please register first';

      // Login errors
      case AuthErrorCode.invalidCredentials:
        return 'Invalid phone number or password';
      case AuthErrorCode.userNotConfirmed:
        return 'Please verify your phone number first';
      case AuthErrorCode.accountDisabled:
        return 'Your account has been disabled. Contact support';
      case AuthErrorCode.passwordResetRequired:
        return 'Password reset required. Please reset your password';

      // Token errors
      case AuthErrorCode.invalidToken:
        return 'Invalid authentication token. Please log in again';
      case AuthErrorCode.tokenExpired:
        return 'Session expired. Please log in again';
      case AuthErrorCode.refreshTokenExpired:
        return 'Session expired. Please log in again';

      // Mobile-specific errors
      case AuthErrorCode.apiNotConfigured:
        return 'Server not configured. Use mock auth or provide API credentials via --dart-define.';
      case AuthErrorCode.networkError:
        return 'No internet connection. Please try offline mode';
      case AuthErrorCode.offlineNotAvailable:
        return 'Please log in online first to enable offline mode';
      case AuthErrorCode.secureStorageError:
        return 'Error accessing secure storage. Please restart the app';
      case AuthErrorCode.databaseError:
        return 'Database error. Please restart the app';

      // Generic errors
      case AuthErrorCode.internalServerError:
        return 'Server error. Please try again later';
      case AuthErrorCode.cognitoUnavailable:
        return 'Authentication service unavailable. Please try again later';

      default:
        return 'An error occurred. Please try again';
    }
  }

  /// Check if this error code is recoverable (user can retry)
  bool get isRecoverable {
    switch (this) {
      case AuthErrorCode.networkError:
      case AuthErrorCode.cognitoUnavailable:
      case AuthErrorCode.internalServerError:
      case AuthErrorCode.rateLimitExceeded:
        return true;

      case AuthErrorCode.apiNotConfigured:
      case AuthErrorCode.accountDisabled:
      case AuthErrorCode.phoneAlreadyRegistered:
        return false;

      default:
        return true;
    }
  }

  /// Check if this error should trigger offline mode
  bool get shouldTryOffline {
    switch (this) {
      case AuthErrorCode.networkError:
      case AuthErrorCode.cognitoUnavailable:
        return true;
      default:
        return false;
    }
  }
}
