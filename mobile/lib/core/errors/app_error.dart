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

  /// 401: the session is gone.
  auth,

  /// 403: signed in, but this role may not read the resource (SIM-23).
  forbidden,

  /// TLS/certificate failure while online (SIM-23).
  secureConnection,
  validation,
  notFound,
  conflict,
  server,

  /// The server wants the user to re-enter their password (AUTH_040).
  stepUpRequired,

  /// A payment above 10,000 ETB needs a fresh Fayda login (AUTH_041).
  faydaStepUpRequired,

  /// The payment needs Fayda KYC or a verified phone (ORDER_KYC_REQUIRED).
  kycRequired,

  /// The server refused an order change for good (any other `ORDER_*`
  /// rejection), or a local guard refused it; nothing was changed.
  orderRefused,

  /// The server refused a queued order create, so the local order was
  /// removed (mobile).
  orderNotPlaced,

  /// The rating already exists on the server (web create conflict).
  alreadyRated,

  /// The server refused a delivery cancel because the delivery was already
  /// picked up (`DELIVERY_CANCEL_NOT_ALLOWED`); the local cancel was undone.
  deliveryCancelNotAllowed,

  /// The server refused a queued change (not an order) for good, so it was
  /// never saved there.
  changeNotSaved,
  unknown,
}

/// A local guard refused an order action before anything was written or
/// queued: the caller is not the right party, or the order is in a status
/// that does not allow it. Surfaced as [AppErrorCode.orderRefused].
class OrderActionNotAllowedException implements Exception {
  const OrderActionNotAllowedException(this.message);

  final String message;

  @override
  String toString() => 'OrderActionNotAllowedException: $message';
}

/// The server already holds this rating (one per order and ratee), so the
/// create changed nothing. Surfaced as [AppErrorCode.alreadyRated].
class AlreadyRatedException implements Exception {
  const AlreadyRatedException();

  @override
  String toString() => 'AlreadyRatedException';
}

/// Classify a caught error into a stable [AppErrorCode].
///
/// Ordering matters: [ApiException] is sealed, so the specific subtypes are
/// checked before the generic fallback.
AppErrorCode mapErrorToCode(Object error) {
  return switch (error) {
    StepUpRequiredException(requiresFayda: true) =>
      AppErrorCode.faydaStepUpRequired,
    StepUpRequiredException() => AppErrorCode.stepUpRequired,
    SyncRejectedException(:final message)
        when isFaydaStepUpRequiredMessage(message) =>
      AppErrorCode.faydaStepUpRequired,
    SyncRejectedException(:final message)
        when isStepUpRequiredMessage(message) =>
      AppErrorCode.stepUpRequired,
    SyncRejectedException(:final message) when isKycRequiredMessage(message) =>
      AppErrorCode.kycRequired,
    SyncConflictException() => AppErrorCode.conflict,
    SyncRejectedException(:final message)
        when isRevertableRejectionMessage(message) =>
      AppErrorCode.orderRefused,
    OrderActionNotAllowedException() => AppErrorCode.orderRefused,
    AlreadyRatedException() => AppErrorCode.alreadyRated,
    TimeoutException() => AppErrorCode.timeout,
    NetworkException() => AppErrorCode.network,
    SecureConnectionException() => AppErrorCode.secureConnection,
    AuthException(:final statusCode) when statusCode == 403 =>
      AppErrorCode.forbidden,
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
    AppErrorCode.forbidden => l10n.errorNoPermission,
    AppErrorCode.secureConnection => l10n.errorSecureConnectionFailed,
    AppErrorCode.conflict => l10n.errorConflict,
    AppErrorCode.stepUpRequired => l10n.stepUpRequiredMessage,
    AppErrorCode.faydaStepUpRequired => l10n.faydaStepUpMessage,
    AppErrorCode.kycRequired => l10n.kycRequiredFaydaMessage,
    AppErrorCode.orderRefused => l10n.errorOrderRefused,
    AppErrorCode.orderNotPlaced => l10n.errorOrderNotPlaced,
    AppErrorCode.alreadyRated => l10n.alreadyRated,
    AppErrorCode.deliveryCancelNotAllowed => l10n.deliveryErrorCancelNotAllowed,
    AppErrorCode.changeNotSaved => l10n.errorChangeNotSaved,
    AppErrorCode.validation ||
    AppErrorCode.notFound ||
    AppErrorCode.server ||
    AppErrorCode.unknown =>
      l10n.errorOccurred,
  };
}

/// The reason to show for an operation the sync queue rejected for good
/// (mobile background sync, `SyncOperationRejected`). A refused order create
/// removed the local order; any other refused order change was undone; a
/// delivery cancel refused after pickup was undone (ESC-08); any other refused
/// change was not saved on the server.
AppErrorCode syncRejectionErrorCode({
  required String entityType,
  required String action,
  required String message,
}) {
  if (entityType == 'order') {
    return action == 'create'
        ? AppErrorCode.orderNotPlaced
        : AppErrorCode.orderRefused;
  }
  if (isDeliveryCancelRefusalMessage(message)) {
    return AppErrorCode.deliveryCancelNotAllowed;
  }
  return isRevertableRejectionMessage(message)
      ? AppErrorCode.orderRefused
      : AppErrorCode.changeNotSaved;
}

/// Convenience: classify and localize in one step from a caught error.
String localizeError(Object error, AppLocalizations l10n) =>
    localizeErrorCode(mapErrorToCode(error), l10n);

/// Subtitle for an offline-first list that failed to load. Only a genuine
/// connectivity failure earns the reassuring "will sync when online" copy;
/// a 403, a TLS failure or a server error says what actually happened
/// (SIM-23: a forbidden read was shown as "waiting for network").
String localizeLoadError(AppErrorCode code, AppLocalizations l10n) {
  return switch (code) {
    AppErrorCode.network || AppErrorCode.timeout => l10n.dataWillSyncWhenOnline,
    _ => localizeErrorCode(code, l10n),
  };
}
