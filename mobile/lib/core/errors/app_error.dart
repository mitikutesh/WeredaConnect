import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/security/security_events.dart';
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

  /// A higher-risk payment needs a fresh Fayda login (AUTH_041); at MEDIUM risk
  /// above 10,000 ETB or at HIGH risk (FRD-04). A password prompt may follow.
  faydaStepUpRequired,

  /// The payment needs the buyer's own Fayda KYC or verified phone
  /// (ORDER_KYC_REQUIRED `buyer_fayda`, `buyer_phone`), so the buyer can fix
  /// it by verifying with Fayda.
  kycRequired,

  /// The payment needs the farmer's verified phone or Fayda ID
  /// (ORDER_KYC_REQUIRED `farmer_phone`, `farmer_fayda`). The buyer cannot
  /// fix it, so the text names the farmer and offers no Fayda (SIM-49).
  kycFarmerRequired,

  /// The KYC gate could not read the parties' profiles (ORDER_KYC_REQUIRED
  /// `unavailable`). Nobody has to verify; the buyer tries again later.
  kycUnavailable,

  /// The account is under review (`FRAUD_ACCOUNT_HELD`): a create or a
  /// payment was refused and nothing was written. Only support can lift it,
  /// so it is never retried.
  accountHeld,

  /// The server refused an order change for good (any other `ORDER_*`
  /// rejection), or a local guard refused it; nothing was changed.
  orderRefused,

  /// The live gateway refused the payment: one Telebirr payment takes 100 to
  /// 500,000 ETB and the order total is outside that (ESC-44).
  paymentTotalOutOfRange,

  /// The live gateway refused the chosen payment method (a bank transfer, or
  /// anything but Telebirr): `ORDER_PAYMENT_METHOD_UNAVAILABLE`, FUP-20;
  /// nothing was changed.
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

  /// A response to an order came after its deadline
  /// (`ORDER_DEADLINE_PASSED`); nothing changed, or the local change was
  /// undone.
  deadlinePassed,

  /// ESC-15: the farmer's or trucker's answer is not open
  /// (`ORDER_RESPONSE_NOT_PENDING`): the farmer has not accepted yet, or the
  /// request was already answered; the local change was undone.
  responseNotPending,

  /// The trip an order names cannot take it (`ORDER_TRIP_NOT_ELIGIBLE`): the
  /// buyer chooses again.
  tripNotEligible,

  /// No more trucks may be tried for the order
  /// (`ORDER_TRANSPORT_ATTEMPTS_EXCEEDED`): collect or cancel.
  transportAttemptsExceeded,

  /// The trip an order names has no room left (`TRIP_CAPACITY_EXCEEDED`).
  tripCapacityExceeded,

  /// The acting account is restricted by reputation (`REPUTATION_RESTRICTED`
  /// on a crop create, trip create or order accept); the change was not saved.
  reputationRestricted,

  /// The listing's farmer or the trip's trucker is restricted
  /// (`REPUTATION_RESTRICTED` on a buyer's order create, spec A7): the order
  /// was not placed, choose another.
  counterpartyRestricted,

  /// A trip with booked orders cannot change its date, price, route or drop
  /// its capacity (`TRIP_HAS_BOOKINGS`); the local change was undone.
  tripHasBookings,

  /// A listing with active orders cannot be removed or reduced
  /// (`CROP_HAS_ACTIVE_ORDERS`); the local change was undone.
  cropHasActiveOrders,

  /// This app build is below the server's minimum (`APP_UPDATE_REQUIRED`,
  /// ESC-25); the change was not saved and the user must update (an order
  /// command on mobile, anything on web).
  appUpdateRequired,

  /// `APP_UPDATE_REQUIRED` on a queued change other than an order command
  /// (mobile): it is kept on the phone and sent after the update.
  appUpdateRequiredKept,

  /// FUP-49: crop or trip text that buyers read held a phone number
  /// (`CROP_CONTACT_IN_TEXT` / `TRIP_CONTACT_IN_TEXT`); nothing was saved.
  contactInText,

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
      _kycErrorCode(message),
    SyncRejectedException(:final message)
        when isFraudAccountHeldMessage(message) =>
      AppErrorCode.accountHeld,
    // REST answers it as a 403 with the code (escrow routes).
    ApiException(code: fraudAccountHeldCode) => AppErrorCode.accountHeld,
    SyncRejectedException(:final message)
        when isPaymentTotalOutOfRangeMessage(message) =>
      AppErrorCode.paymentTotalOutOfRange,
    SyncRejectedException(:final message)
        when isPaymentMethodUnavailableMessage(message) =>
      AppErrorCode.paymentMethodUnavailable,
    SyncConflictException() => AppErrorCode.conflict,
    // Codes with their own text (spec section 8) come before the ORDER_
    // prefix rule. There is no entity or action here, so REPUTATION_RESTRICTED
    // reads as the actor's restriction; the web order create maps it to the
    // owner's (OrderBloc).
    SyncRejectedException(:final message)
        when isAppUpdateRequiredMessage(message) =>
      AppErrorCode.appUpdateRequired,
    SyncRejectedException(:final message)
        when isReputationRestrictedMessage(message) =>
      AppErrorCode.reputationRestricted,
    SyncRejectedException(:final message)
        when isTripHasBookingsMessage(message) =>
      AppErrorCode.tripHasBookings,
    SyncRejectedException(:final message)
        when isTripCapacityExceededMessage(message) =>
      AppErrorCode.tripCapacityExceeded,
    SyncRejectedException(:final message)
        when isCropHasActiveOrdersMessage(message) =>
      AppErrorCode.cropHasActiveOrders,
    SyncRejectedException(:final message)
        when isOrderDeadlinePassedMessage(message) =>
      AppErrorCode.deadlinePassed,
    SyncRejectedException(:final message)
        when isOrderResponseNotPendingMessage(message) =>
      AppErrorCode.responseNotPending,
    SyncRejectedException(:final message)
        when isOrderTripNotEligibleMessage(message) =>
      AppErrorCode.tripNotEligible,
    SyncRejectedException(:final message)
        when isOrderTransportAttemptsExceededMessage(message) =>
      AppErrorCode.transportAttemptsExceeded,
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
    SyncRejectedException(:final message)
        when isContactInTextMessage(message) =>
      AppErrorCode.contactInText,
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

/// Whose verification the KYC refusal [message] says is missing, as a code:
/// only a buyer-side reason may tell the buyer to verify (SIM-49).
AppErrorCode _kycErrorCode(String message) =>
    switch (KycReason.fromMessage(message)) {
      KycReason.buyerPhone || KycReason.buyerFayda => AppErrorCode.kycRequired,
      KycReason.farmerPhone ||
      KycReason.farmerFayda =>
        AppErrorCode.kycFarmerRequired,
      KycReason.unavailable => AppErrorCode.kycUnavailable,
    };

/// Whether [code] is a KYC gate refusal (ORDER_KYC_REQUIRED) of any party.
/// The payment screen explains these in the KYC dialog, which reads the exact
/// reason from the message.
bool isKycErrorCode(AppErrorCode code) =>
    code == AppErrorCode.kycRequired ||
    code == AppErrorCode.kycFarmerRequired ||
    code == AppErrorCode.kycUnavailable;

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
    AppErrorCode.kycFarmerRequired => l10n.kycFarmerNotVerifiedMessage,
    AppErrorCode.kycUnavailable => l10n.kycUnavailableMessage,
    AppErrorCode.accountHeld => l10n.fraudAccountHeld,
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
    AppErrorCode.deadlinePassed => l10n.errorDeadlinePassed,
    AppErrorCode.responseNotPending => l10n.errorResponseNotPending,
    AppErrorCode.tripNotEligible => l10n.errorTripNotEligible,
    AppErrorCode.transportAttemptsExceeded =>
      l10n.errorTransportAttemptsExceeded,
    AppErrorCode.tripCapacityExceeded => l10n.errorTripCapacityExceeded,
    AppErrorCode.reputationRestricted => l10n.errorReputationRestricted,
    AppErrorCode.counterpartyRestricted => l10n.errorCounterpartyRestricted,
    AppErrorCode.tripHasBookings => l10n.errorTripHasBookings,
    AppErrorCode.cropHasActiveOrders => l10n.errorCropHasActiveOrders,
    AppErrorCode.appUpdateRequired => l10n.errorAppUpdateRequired,
    AppErrorCode.appUpdateRequiredKept => l10n.errorAppUpdateRequiredKept,
    AppErrorCode.contactInText => l10n.errorContactInText,
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
/// delivery dispute that was refused or too late (ESC-07); a crop or trip
/// change refused by reputation, bookings or active orders was undone (ESC-12
/// part B); any other refused change was not saved on the server.
///
/// A code with its own text wins over the generic create text: "choose
/// another truck" or "update the app" already says the order must be placed
/// again.
AppErrorCode syncRejectionErrorCode({
  required String entityType,
  required String action,
  required String message,
}) {
  if (isAppUpdateRequiredMessage(message)) {
    // Only an order command is refused for the app version; anything else
    // stays queued for the updated build (sync-protocol.md 1.17.0).
    return entityType == 'order'
        ? AppErrorCode.appUpdateRequired
        : AppErrorCode.appUpdateRequiredKept;
  }
  // The real reason for any entity and action: the account is under review.
  if (isFraudAccountHeldMessage(message)) return AppErrorCode.accountHeld;
  if (entityType == 'order') {
    if (isReputationRestrictedMessage(message)) {
      // On a create the restricted party is the listing's or trip's owner
      // (spec A7), not the buyer who ordered.
      return action == 'create'
          ? AppErrorCode.counterpartyRestricted
          : AppErrorCode.reputationRestricted;
    }
    if (isTripCapacityExceededMessage(message)) {
      return AppErrorCode.tripCapacityExceeded;
    }
    if (isOrderTripNotEligibleMessage(message)) {
      return AppErrorCode.tripNotEligible;
    }
    if (action == 'create') return AppErrorCode.orderNotPlaced;
    if (isOrderCancelNotAllowedMessage(message)) {
      return AppErrorCode.orderCancelNotAllowed;
    }
    if (isPaymentMethodUnavailableMessage(message)) {
      return AppErrorCode.paymentMethodUnavailable;
    }
    if (isOrderDeadlinePassedMessage(message)) {
      return AppErrorCode.deadlinePassed;
    }
    if (isOrderResponseNotPendingMessage(message)) {
      return AppErrorCode.responseNotPending;
    }
    if (isOrderTransportAttemptsExceededMessage(message)) {
      return AppErrorCode.transportAttemptsExceeded;
    }
    return isPaymentTotalOutOfRangeMessage(message)
        ? AppErrorCode.paymentTotalOutOfRange
        : AppErrorCode.orderRefused;
  }
  if (isReputationRestrictedMessage(message)) {
    return AppErrorCode.reputationRestricted;
  }
  if (isTripHasBookingsMessage(message)) {
    return AppErrorCode.tripHasBookings;
  }
  if (isCropHasActiveOrdersMessage(message)) {
    return AppErrorCode.cropHasActiveOrders;
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
  if (isContactInTextMessage(message)) {
    return AppErrorCode.contactInText;
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
