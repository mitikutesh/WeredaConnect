import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Stable, localizable error categories surfaced by BLoCs to the UI.
///
/// BLoCs have no [BuildContext], so they cannot resolve localized strings. They
/// instead map a caught exception to one of these codes via [mapErrorToCode];
/// screens then render a localized, actionable message via [localizeErrorCode].
/// This keeps raw exception text (Dio internals, stack info, server codes) out
/// of the user-facing UI for the 3-language, low-literacy audience.
enum AppErrorCode {
  network,
  timeout,
  auth,
  validation,
  notFound,
  conflict,
  server,

  /// The server wants the user to re-enter their password (AUTH_040).
  stepUpRequired,

  /// The payment needs Fayda KYC or a verified phone (ORDER_KYC_REQUIRED).
  kycRequired,
  unknown,
}

/// Classify a caught error into a stable [AppErrorCode].
///
/// Ordering matters: [ApiException] is sealed, so the specific subtypes are
/// checked before the generic fallback.
AppErrorCode mapErrorToCode(Object error) {
  return switch (error) {
    StepUpRequiredException() => AppErrorCode.stepUpRequired,
    SyncRejectedException(:final message)
        when isStepUpRequiredMessage(message) =>
      AppErrorCode.stepUpRequired,
    SyncRejectedException(:final message) when isKycRequiredMessage(message) =>
      AppErrorCode.kycRequired,
    TimeoutException() => AppErrorCode.timeout,
    NetworkException() => AppErrorCode.network,
    AuthException() => AppErrorCode.auth,
    ValidationException() => AppErrorCode.validation,
    NotFoundException() => AppErrorCode.notFound,
    ConflictException() => AppErrorCode.conflict,
    ServerException() => AppErrorCode.server,
    ApiException() => AppErrorCode.server, // any other typed API failure
    _ => AppErrorCode.unknown,
  };
}

/// A localized, user-facing, actionable message for an [AppErrorCode].
///
/// Codes are collapsed onto a small set of phrased messages: connectivity and
/// auth/conflict get their own actionable text; everything else falls back to
/// the generic [AppLocalizations.errorOccurred].
String localizeErrorCode(AppErrorCode code, AppLocalizations l10n) {
  return switch (code) {
    AppErrorCode.network || AppErrorCode.timeout => l10n.noInternetConnection,
    AppErrorCode.auth => l10n.errorSessionExpired,
    AppErrorCode.conflict => l10n.errorConflict,
    AppErrorCode.stepUpRequired => l10n.stepUpRequiredMessage,
    AppErrorCode.kycRequired => l10n.kycRequiredFaydaMessage,
    AppErrorCode.validation ||
    AppErrorCode.notFound ||
    AppErrorCode.server ||
    AppErrorCode.unknown =>
      l10n.errorOccurred,
  };
}

/// Convenience: classify and localize in one step from a caught error.
String localizeError(Object error, AppLocalizations l10n) =>
    localizeErrorCode(mapErrorToCode(error), l10n);
