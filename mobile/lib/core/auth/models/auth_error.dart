/// Authentication error codes matching server API contract.
///
/// See specs/contracts/auth-api.md for complete error documentation.
///
/// These error codes are returned by the server and used in the mobile app
/// to show localized error messages to users.
///
/// IMPORTANT: These values MUST match the codes sent by the server.
/// The server is the source of truth — see server/src/Services/CognitoAuthService.cs
/// and server/src/RateLimiting/RateLimitService.cs.
class AuthErrorCode {
  // Registration errors (AUTH_001 - AUTH_008)
  static const invalidPhoneFormat = 'AUTH_001';
  static const invalidPassword = 'AUTH_002';
  static const invalidRole = 'AUTH_003';
  static const woredaRequired = 'AUTH_004';
  static const phoneAlreadyRegistered = 'AUTH_005';
  static const rateLimitExceeded = 'AUTH_006';
  static const internalServerError = 'AUTH_007';
  static const cognitoUnavailable = 'AUTH_008';

  // Fayda verification errors (FAYDA_001 - FAYDA_004)
  // Server is source of truth — see server/src/Services/FaydaOidcService.cs
  // and server/src/Functions/FaydaVerifyFunction.cs
  static const faydaVerificationFailed = 'FAYDA_001';
  static const faydaTokenExpired = 'FAYDA_002';
  static const faydaServiceUnavailable = 'FAYDA_003';
  static const faydaAlreadyLinked = 'FAYDA_004';

  // Legacy (AUTH_013 still used by login)
  static const userNotFound = 'AUTH_013';

  // Login errors (AUTH_014 - AUTH_016)
  static const invalidCredentials = 'AUTH_014';
  static const accountLocked = 'AUTH_015';
  static const userNotConfirmed = 'AUTH_016';

  // Token errors (AUTH_017 - AUTH_020)
  static const missingRefreshToken = 'AUTH_017';
  static const refreshTokenExpired = 'AUTH_018';
  static const refreshTokenRevoked = 'AUTH_019';
  static const invalidToken = 'AUTH_020';

  // Registration: Fayda token validation
  static const invalidFaydaToken = 'AUTH_021';

  // Aliases for backward compatibility within mobile code
  static const tokenExpired = 'AUTH_018';
  static const accountDisabled = 'AUTH_015'; // Reuse accountLocked code
  static const passwordResetRequired = 'AUTH_014'; // Redirect to login

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
        return 'Invalid phone number format. Use +251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX';
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

      // Fayda errors
      case AuthErrorCode.faydaVerificationFailed:
        return 'Fayda verification failed. Please try again';
      case AuthErrorCode.faydaTokenExpired:
        return 'Fayda verification expired. Please verify again';
      case AuthErrorCode.faydaAlreadyLinked:
        return 'This Fayda ID is already linked to another account';
      case AuthErrorCode.faydaServiceUnavailable:
        return 'Fayda service is currently unavailable. Please try later';
      case AuthErrorCode.invalidFaydaToken:
        return 'Invalid or expired Fayda verification. Please verify again';
      case AuthErrorCode.userNotFound:
        return 'User not found. Please register first';

      // Login errors
      case AuthErrorCode.invalidCredentials:
        return 'Invalid phone number or password';
      case AuthErrorCode.accountLocked:
        return 'Account locked due to too many failed attempts. Try again later';
      case AuthErrorCode.userNotConfirmed:
        return 'Please verify your phone number first';

      // Token errors
      case AuthErrorCode.missingRefreshToken:
        return 'Session data missing. Please log in again';
      case AuthErrorCode.refreshTokenExpired:
        return 'Session expired. Please log in again';
      case AuthErrorCode.refreshTokenRevoked:
        return 'Session revoked. Please log in again';
      case AuthErrorCode.invalidToken:
        return 'Invalid authentication token. Please log in again';

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
