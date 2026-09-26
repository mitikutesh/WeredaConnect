import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Map auth error codes to localized user-facing messages.
///
/// Centralized mapper following the same pattern as
/// [localizeDeliveryError] and [localizeEscrowError].
///
/// The [retryAfterSeconds] parameter is used for AUTH_015 (account locked)
/// to show the lockout duration.
String localizeAuthError(
  AppLocalizations l10n,
  String code, {
  int? retryAfterSeconds,
}) {
  if (code == AuthErrorCode.accountLocked && retryAfterSeconds != null) {
    final minutes = (retryAfterSeconds / 60).ceil();
    return l10n.authAccountLocked('$minutes min');
  }

  if (code == AuthErrorCode.rateLimitExceeded &&
      retryAfterSeconds != null &&
      retryAfterSeconds > 0) {
    return l10n.authRateLimitRetryIn(retryAfterSeconds);
  }

  return switch (code) {
    // Registration errors
    AuthErrorCode.invalidPhoneFormat => l10n.invalidPhoneFormat,
    AuthErrorCode.invalidPassword => l10n.passwordRequirements,
    AuthErrorCode.invalidRole => l10n.genericError,
    AuthErrorCode.woredaRequired => l10n.genericError,
    AuthErrorCode.phoneAlreadyRegistered => l10n.phoneAlreadyRegistered,
    AuthErrorCode.rateLimitExceeded => l10n.authRateLimitExceeded,
    AuthErrorCode.internalServerError => l10n.authServerError,
    AuthErrorCode.cognitoUnavailable => l10n.authServiceUnavailable,

    // Fayda errors
    AuthErrorCode.faydaVerificationFailed => l10n.faydaVerificationFailed,
    AuthErrorCode.faydaTokenExpired => l10n.faydaStateMismatch,
    AuthErrorCode.faydaServiceUnavailable => l10n.faydaUnavailable,
    AuthErrorCode.faydaAlreadyLinked => l10n.faydaAlreadyLinked,
    AuthErrorCode.invalidFaydaToken => l10n.faydaVerificationFailed,

    // Password reset errors (AUTH_022–024)
    AuthErrorCode.resetCodeInvalid => l10n.resetCodeInvalid,
    AuthErrorCode.resetCodeExpired => l10n.resetCodeExpired,
    AuthErrorCode.resetPasswordRequirements => l10n.passwordRequirements,

    // Registration OTP errors
    AuthErrorCode.otpInvalidFormat => l10n.invalidOtp,
    AuthErrorCode.otpInvalidOrExpired => l10n.otpInvalidOrExpired,

    // Step-up
    AuthErrorCode.stepUpRequired => l10n.stepUpRequiredMessage,

    // Login errors
    AuthErrorCode.invalidCredentials => l10n.invalidCredentials,
    AuthErrorCode.accountLocked => l10n.authAccountDisabled,
    AuthErrorCode.accountSuspended => l10n.authAccountDisabled,
    AuthErrorCode.userNotConfirmed => l10n.authErrorUserNotConfirmed,
    AuthErrorCode.userNotFound => l10n.invalidCredentials,

    // Token errors
    AuthErrorCode.missingRefreshToken => l10n.authErrorTokenExpired,
    AuthErrorCode.refreshTokenExpired => l10n.authErrorTokenExpired,
    AuthErrorCode.refreshTokenRevoked => l10n.authErrorTokenExpired,
    AuthErrorCode.invalidToken => l10n.authErrorTokenExpired,

    // Mobile-only errors
    AuthErrorCode.apiNotConfigured => l10n.authApiNotConfigured,
    AuthErrorCode.networkError => l10n.authNetworkError,
    AuthErrorCode.offlineNotAvailable => l10n.authOfflineNotAvailable,
    AuthErrorCode.secureStorageError => l10n.authErrorSecureStorage,
    AuthErrorCode.databaseError => l10n.authErrorDatabase,
    _ => l10n.genericError,
  };
}
