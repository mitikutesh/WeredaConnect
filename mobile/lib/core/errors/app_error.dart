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

  /// The live gateway refused the payment: one Telebirr payment takes 100 to
  /// 500,000 ETB and the order total is outside that (ESC-44).
  paymentTotalOutOfRange,

  /// The live gateway refused the payment method: bank transfer is not
  /// available yet, only Telebirr is (`ORDER_PAYMENT_METHOD_UNAVAILABLE`,
  /// FUP-20); nothing was changed.
  paymentMethodUnavailable,

  /// The server refused a queued order create, so the local order was
  /// removed (mobile).
  orderNotPlaced,

  /// The rating already exists on the server (web create conflict).
  alreadyRated,

  /// The server refused a delivery cancel because the delivery was already
  /// picked up (`DELIVERY_CANCEL_NOT_ALLOWED`); the local cancel was undone.
  deliveryCancelNotAllowed,

  /// The server refused a delivery dispute because the payment could not be
  /// frozen (`DELIVERY_DISPUTE_REFUSED`, ESC-07); the local dispute was undone.
  deliveryDisputeRefused,

  /// The server refused a delivery dispute raised after the 48-hour window
  /// (`DELIVERY_DISPUTE_EXPIRED`); the local dispute was undone.
  deliveryDisputeExpired,

  /// ESC-06: an accepted order may be cancelled only after a transport
  /// failure (`ORDER_CANCEL_NOT_ALLOWED`, or the local guard); nothing changed.
  orderCancelNotAllowed,

  /// ESC-48: a message went into a thread whose contact is not open
  /// (`MESSAGE_CONTACT_LOCKED`); it was not sent.
  contactLocked,

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

/// ESC-06: the local guard refused to cancel an accepted order whose pickup
/// deadline has not passed. Surfaced as [AppErrorCode.orderCancelNotAllowed].
class OrderCancelNotAllowedException extends OrderActionNotAllowedException {
  const OrderCancelNotAllowedException(super.message);
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
    SyncRejectedException(:final message)
        when isPaymentTotalOutOfRangeMessage(message) =>
      AppErrorCode.paymentTotalOutOfRange,
    SyncRejectedException(:final message)
        when isPaymentMethodUnavailableMessage(message) =>
      AppErrorCode.paymentMethodUnavailable,
    SyncConflictException() => AppErrorCode.conflict,
    SyncRejectedException(:final message)
        when isOrderCancelNotAllowedMessage(message) =>
      AppErrorCode.orderCancelNotAllowed,
    SyncRejectedException(:final message)
        when isRevertableRejectionMessage(message) =>
      AppErrorCode.orderRefused,
    SyncRejectedException(:final message)
        when isDeliveryDisputeRefusalMessage(message) =>
      AppErrorCode.deliveryDisputeRefused,
    SyncRejectedException(:final message)
        when isDeliveryDisputeExpiredMessage(message) =>
      AppErrorCode.deliveryDisputeExpired,
    SyncRejectedException(:final message)
        when isContactLockedMessage(message) =>
      AppErrorCode.contactLocked,
    OrderCancelNotAllowedException() => AppErrorCode.orderCancelNotAllowed,
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
    AppErrorCode.paymentTotalOutOfRange => l10n.paymentTotalOutOfRange,
    AppErrorCode.paymentMethodUnavailable => l10n.paymentMethodUnavailable,
    AppErrorCode.orderNotPlaced => l10n.errorOrderNotPlaced,
    AppErrorCode.alreadyRated => l10n.alreadyRated,
    AppErrorCode.deliveryCancelNotAllowed => l10n.deliveryErrorCancelNotAllowed,
    AppErrorCode.deliveryDisputeRefused => l10n.deliveryErrorDisputeRefused,
    AppErrorCode.deliveryDisputeExpired => l10n.deliveryErrorDisputeExpired,
    AppErrorCode.orderCancelNotAllowed => l10n.errorOrderCancelNotAllowed,
    AppErrorCode.contactLocked => l10n.contactClosedNote,
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
/// delivery cancel refused after pickup was undone (ESC-08), and so was a
/// delivery dispute that was refused or too late (ESC-07); any other refused
/// change was not saved on the server.
AppErrorCode syncRejectionErrorCode({
  required String entityType,
  required String action,
  required String message,
}) {
  if (entityType == 'order') {
    if (action == 'create') return AppErrorCode.orderNotPlaced;
    if (isOrderCancelNotAllowedMessage(message)) {
      return AppErrorCode.orderCancelNotAllowed;
    }
    if (isPaymentMethodUnavailableMessage(message)) {
      return AppErrorCode.paymentMethodUnavailable;
    }
    return isPaymentTotalOutOfRangeMessage(message)
        ? AppErrorCode.paymentTotalOutOfRange
        : AppErrorCode.orderRefused;
  }
  if (isDeliveryCancelRefusalMessage(message)) {
    return AppErrorCode.deliveryCancelNotAllowed;
  }
  if (isDeliveryDisputeRefusalMessage(message)) {
    return AppErrorCode.deliveryDisputeRefused;
  }
  if (isDeliveryDisputeExpiredMessage(message)) {
    return AppErrorCode.deliveryDisputeExpired;
  }
  if (isContactLockedMessage(message)) {
    return AppErrorCode.contactLocked;
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
