/// Network and API exceptions for error handling
library;

import 'package:dio/dio.dart';

import 'package:wereda_connect/core/network/json_body.dart';

/// Base exception for all API errors
sealed class ApiException implements Exception {
  const ApiException(this.message, [this.statusCode, this.code]);

  final String message;
  final int? statusCode;

  /// The server's machine-readable error code (for example `ESCROW_024`) from
  /// the `{"error": {"code": ..., "message": ...}}` envelope, when the
  /// response carried one. Clients key their copy on it: a status alone
  /// cannot tell "already paid for this delivery" from "funds already held
  /// for this order" (SIM-24).
  final String? code;

  @override
  String toString() =>
      'ApiException: $message${statusCode != null ? ' (HTTP $statusCode)' : ''}';
}

/// Network connectivity error (no internet, timeout, etc.)
final class NetworkException extends ApiException {
  const NetworkException([super.message = 'Network error occurred']);
}

/// Server returned an error response (4xx, 5xx)
final class ServerException extends ApiException {
  const ServerException(super.message, super.statusCode, [super.code]);
}

/// Authentication/authorization error (401, 403)
final class AuthException extends ApiException {
  const AuthException([
    super.message = 'Authentication failed',
    super.statusCode = 401,
    super.code,
  ]);
}

/// Server error code for "step-up required": the ID token's `auth_time` is
/// older than the freshness window for a money action (payments up to
/// 10,000 ETB, large disputes).
const String stepUpRequiredCode = 'AUTH_040';

/// Server error code for "Fayda step-up required": a payment above 10,000 ETB
/// needs a Fayda login within the freshness window (a password is not enough).
const String faydaStepUpRequiredCode = 'AUTH_041';

/// Sync rejection prefix when a payment needs Fayda KYC or a verified phone.
const String orderKycRequiredCode = 'ORDER_KYC_REQUIRED';

/// REST error code (HTTP 410) for a write from an account that is being or
/// has been deleted (account-deletion.md, sync-protocol.md 1.26.0). The body
/// carries the server's `deletionState`.
const String accountGoneCode = 'ACCOUNT_004';

/// The `deletionState` of a deletion the server started but did not finish.
/// It can still abort, so it is the one state the client does not wipe for.
const String deletionInProgressState = 'in_progress';

/// The `deletionState` of a `410 ACCOUNT_004` answer
/// (`{"error":{"code":"ACCOUNT_004","deletionState":"completed",...}}`), or
/// null for any other answer. A refusal without a state reads as `''`.
String? accountGoneState(int? statusCode, Object? body) {
  if (statusCode != 410) return null;
  final error = decodeJsonMap(body)?['error'];
  if (error is! Map || error['code'] != accountGoneCode) return null;
  final state = error['deletionState'];
  return state is String ? state : '';
}

/// The server wants the user to confirm it is them before a money action
/// (REST 403 `AUTH_040` for the password, `AUTH_041` for Fayda). Not an
/// [AuthException]: the session is still valid, so callers must
/// re-authenticate and retry instead of logging out.
final class StepUpRequiredException extends ApiException {
  const StepUpRequiredException([
    super.message = 'Please confirm it is you',
    super.statusCode = 403,
    super.code = stepUpRequiredCode,
  ]);

  /// True when only a fresh Fayda login satisfies the server (AUTH_041).
  bool get requiresFayda => code == faydaStepUpRequiredCode;
}

/// Whether a /sync per-operation error message is a step-up rejection of
/// either kind (`AUTH_040: ...` password, `AUTH_041: ...` Fayda).
bool isStepUpRequiredMessage(String? message) =>
    message != null &&
    (message.startsWith(stepUpRequiredCode) ||
        message.startsWith(faydaStepUpRequiredCode));

/// Whether a /sync per-operation error message asks for a Fayda login
/// (`AUTH_041: ...`).
bool isFaydaStepUpRequiredMessage(String? message) =>
    message != null && message.startsWith(faydaStepUpRequiredCode);

/// Whether a /sync per-operation error message is a KYC gate rejection
/// (`ORDER_KYC_REQUIRED: ...`).
bool isKycRequiredMessage(String? message) =>
    message != null && message.startsWith(orderKycRequiredCode);

/// Sync rejection prefix, and REST error code (`403` on `/escrow/initiate`
/// and `/escrow/order-checkout`), when the account is under review (risk
/// tier CRITICAL, fraud-detection-architecture.md section 17.3): a crop, trip
/// or order create or a payment is refused and nothing is written. The same
/// command gets the same answer until support clears the account.
const String fraudAccountHeldCode = 'FRAUD_ACCOUNT_HELD';

/// Whether a /sync per-operation error message refuses the action because
/// the account is under review (`FRAUD_ACCOUNT_HELD: ...`). Permanent like
/// every `FRAUD_*` rejection.
bool isFraudAccountHeldMessage(String? message) =>
    message != null && message.startsWith(fraudAccountHeldCode);

/// Whether a /sync per-operation error message is a permanent order
/// rejection whose local change the client must undo: every `ORDER_*` code
/// except `ORDER_KYC_REQUIRED`, which keeps its own revert and Fayda prompt
/// (specs/contracts/sync-protocol.md 1.10.0). Replaying the same payload gets
/// the same answer, so these are never retried.
bool isRevertableRejectionMessage(String? message) =>
    message != null &&
    message.startsWith('ORDER_') &&
    !isKycRequiredMessage(message);

/// Sync rejection prefix when the buyer cancels an accepted order before its
/// pickup deadline passed, or after the crops were picked up (ESC-06,
/// sync-protocol.md 1.13.0).
const String orderCancelNotAllowedCode = 'ORDER_CANCEL_NOT_ALLOWED';

/// Whether a /sync per-operation error message refuses an order cancel
/// (`ORDER_CANCEL_NOT_ALLOWED: ...`). Permanent like every `ORDER_*` refusal:
/// the local cancel is undone and the buyer may cancel again once allowed.
bool isOrderCancelNotAllowedMessage(String? message) =>
    message != null && message.startsWith(orderCancelNotAllowedCode);

/// Sync rejection prefix when a party tries to cancel a delivery after pickup
/// (ESC-08, sync-protocol.md 1.11.0).
const String deliveryCancelNotAllowedCode = 'DELIVERY_CANCEL_NOT_ALLOWED';

/// Whether a /sync per-operation error message refuses a delivery cancel
/// (`DELIVERY_CANCEL_NOT_ALLOWED: ...`). Permanent: the delivery was picked
/// up, so the client undoes its local cancel instead of retrying.
bool isDeliveryCancelRefusalMessage(String? message) =>
    message != null && message.startsWith(deliveryCancelNotAllowedCode);

/// Sync rejection prefix when a delivery dispute cannot freeze the payment
/// (ESC-07, sync-protocol.md 1.12.0): it is queued for an admin payout, being
/// settled, no longer held, or not on record. Nothing was written.
const String deliveryDisputeRefusedCode = 'DELIVERY_DISPUTE_REFUSED';

/// Sync rejection prefix when a delivery dispute comes after the 48-hour
/// window.
const String deliveryDisputeExpiredCode = 'DELIVERY_DISPUTE_EXPIRED';

/// Whether a /sync per-operation error message refuses a delivery dispute
/// (`DELIVERY_DISPUTE_REFUSED: ...`). Permanent: the client undoes its local
/// dispute instead of retrying.
bool isDeliveryDisputeRefusalMessage(String? message) =>
    message != null && message.startsWith(deliveryDisputeRefusedCode);

/// Whether a /sync per-operation error message refuses a delivery dispute
/// raised after the 48-hour window (`DELIVERY_DISPUTE_EXPIRED: ...`).
/// Permanent since ESC-07: the client undoes its local dispute.
bool isDeliveryDisputeExpiredMessage(String? message) =>
    message != null && message.startsWith(deliveryDisputeExpiredCode);

/// Sync rejection prefix when a message goes into a thread whose contact is
/// not open (ESC-48, sync-protocol.md 1.15.0): any crop or trip thread, or an
/// order or delivery thread before the farmer accepted the paid order or after
/// the money went back. The server never stored the message.
const String contactLockedCode = 'MESSAGE_CONTACT_LOCKED';

/// Whether a /sync per-operation error message refuses a message because the
/// thread's contact is not open (`MESSAGE_CONTACT_LOCKED: ...`). Permanent:
/// the client removes its local copy instead of retrying.
bool isContactLockedMessage(String? message) =>
    message != null && message.startsWith(contactLockedCode);

/// Sync rejection prefixes when crop or trip text that buyers read carries a
/// phone number (FUP-49, sync-protocol.md 1.23.0). Nothing was written.
const String cropContactInTextCode = 'CROP_CONTACT_IN_TEXT';
const String tripContactInTextCode = 'TRIP_CONTACT_IN_TEXT';

/// Whether a /sync per-operation error message refuses a crop listing or a
/// trip because its text holds a phone number. Permanent: the same payload
/// always gets the same answer, so it is never resent.
bool isContactInTextMessage(String? message) =>
    message != null &&
    (message.startsWith(cropContactInTextCode) ||
        message.startsWith(tripContactInTextCode));

/// Whether a /sync per-operation error message refuses a delivery change for
/// good and the client must put the delivery back from the server: a cancel
/// after pickup (ESC-08) or a dispute that was refused or too late (ESC-07).
bool isRevertableDeliveryRejectionMessage(String? message) =>
    isDeliveryCancelRefusalMessage(message) ||
    isDeliveryDisputeRefusalMessage(message) ||
    isDeliveryDisputeExpiredMessage(message);

/// Sync rejection prefix when a farmer's or trucker's response to an order
/// comes after its deadline (three-party commitments, spec section 8).
/// Permanent under the `ORDER_*` rule; has its own text.
const String orderDeadlinePassedCode = 'ORDER_DEADLINE_PASSED';

/// Sync rejection prefix when the trip an order names cannot take it
/// (route, date, room or owner). Permanent under the `ORDER_*` rule.
const String orderTripNotEligibleCode = 'ORDER_TRIP_NOT_ELIGIBLE';

/// Sync rejection prefix when no more replacement trucks may be tried for an
/// order after transport failures. Permanent under the `ORDER_*` rule.
const String orderTransportAttemptsExceededCode =
    'ORDER_TRANSPORT_ATTEMPTS_EXCEEDED';

/// Sync rejection prefix when a farmer's or trucker's answer is not open:
/// the farmer has not accepted yet (the trucker waits), or the request was
/// already answered or closed (ESC-15, sync-protocol.md 1.29.0). Permanent
/// under the `ORDER_*` rule; has its own text.
const String orderResponseNotPendingCode = 'ORDER_RESPONSE_NOT_PENDING';

/// Whether a /sync per-operation error message refuses an answer that is not
/// open (`ORDER_RESPONSE_NOT_PENDING: ...`).
bool isOrderResponseNotPendingMessage(String? message) =>
    message != null && message.startsWith(orderResponseNotPendingCode);

/// Whether a /sync per-operation error message is a response after the
/// order's deadline (`ORDER_DEADLINE_PASSED: ...`).
bool isOrderDeadlinePassedMessage(String? message) =>
    message != null && message.startsWith(orderDeadlinePassedCode);

/// Whether a /sync per-operation error message says the named trip cannot
/// take the order (`ORDER_TRIP_NOT_ELIGIBLE: ...`).
bool isOrderTripNotEligibleMessage(String? message) =>
    message != null && message.startsWith(orderTripNotEligibleCode);

/// Whether a /sync per-operation error message says no more trucks may be
/// tried for the order (`ORDER_TRANSPORT_ATTEMPTS_EXCEEDED: ...`).
bool isOrderTransportAttemptsExceededMessage(String? message) =>
    message != null && message.startsWith(orderTransportAttemptsExceededCode);

/// Sync rejection prefix when the acting account, or on a buyer's order
/// create the listing's or trip's owner, is restricted by reputation
/// (ESC-29, sync-protocol.md 1.17.0): crop create, trip create, order accept
/// and order create. Permanent; the local change is undone.
const String reputationRestrictedCode = 'REPUTATION_RESTRICTED';

/// Sync rejection prefix when a trip with booked orders is changed in a way
/// its bookings forbid (ESC-17): date, price, route or a capacity below what
/// is booked. Permanent; the local trip goes back to the server's row.
const String tripHasBookingsCode = 'TRIP_HAS_BOOKINGS';

/// Sync rejection prefix when an order names a trip that has no room left
/// (ESC-17). An order refusal: permanent, the local order is undone.
const String tripCapacityExceededCode = 'TRIP_CAPACITY_EXCEEDED';

/// Sync rejection prefix when a listing with funded or accepted orders is
/// removed, or its quantity dropped below what those orders hold (ESC-18).
/// Permanent; the local listing goes back to the server's row.
const String cropHasActiveOrdersCode = 'CROP_HAS_ACTIVE_ORDERS';

/// Sync rejection prefix when this app build is below the server's minimum
/// (ESC-25, the minimum-version gate on order commands, spec K1). Permanent
/// for an order: the local order is undone like any order refusal. Any other
/// operation is still valid, so it is kept pending for the updated build and
/// never undone. Either way the user is asked to update.
const String appUpdateRequiredCode = 'APP_UPDATE_REQUIRED';

/// Whether a /sync per-operation error message is a reputation refusal
/// (`REPUTATION_RESTRICTED: ...`).
bool isReputationRestrictedMessage(String? message) =>
    message != null && message.startsWith(reputationRestrictedCode);

/// Whether a /sync per-operation error message refuses a trip change because
/// of its bookings (`TRIP_HAS_BOOKINGS: ...`).
bool isTripHasBookingsMessage(String? message) =>
    message != null && message.startsWith(tripHasBookingsCode);

/// Whether a /sync per-operation error message refuses an order because its
/// trip is full (`TRIP_CAPACITY_EXCEEDED: ...`).
bool isTripCapacityExceededMessage(String? message) =>
    message != null && message.startsWith(tripCapacityExceededCode);

/// Whether a /sync per-operation error message refuses a listing change
/// because of its active orders (`CROP_HAS_ACTIVE_ORDERS: ...`).
bool isCropHasActiveOrdersMessage(String? message) =>
    message != null && message.startsWith(cropHasActiveOrdersCode);

/// Whether a /sync per-operation error message says the app is too old
/// (`APP_UPDATE_REQUIRED: ...`).
bool isAppUpdateRequiredMessage(String? message) =>
    message != null && message.startsWith(appUpdateRequiredCode);

/// Whether a /sync per-operation error message refuses a crop listing change
/// for good and the client must put the listing back from the server (or
/// remove a refused create): the farmer is restricted or the listing has
/// active orders (sync-protocol.md 1.17.0). Only these codes: `CROP_VALIDATION`
/// also covers a failed profile read on the server, so a `CROP_` prefix is not
/// deterministic and keeps retrying. `APP_UPDATE_REQUIRED` is not one either:
/// the same listing goes through once the app is updated.
bool isRevertableListingRejectionMessage(String? message) =>
    isReputationRestrictedMessage(message) ||
    isCropHasActiveOrdersMessage(message);

/// Whether a /sync per-operation error message refuses a trip change for
/// good and the client must put the trip back from the server (or remove a
/// refused create): the trucker is restricted or the trip has bookings
/// (sync-protocol.md 1.17.0). Only these codes, for the same reasons as
/// [isRevertableListingRejectionMessage].
bool isRevertableTripRejectionMessage(String? message) =>
    isReputationRestrictedMessage(message) || isTripHasBookingsMessage(message);

/// Sync rejection prefix when the live gateway refuses a payment whose order
/// total one Telebirr payment cannot take (FUP-22, sync-protocol.md 1.24.0).
/// Nothing was written.
const String orderPaymentTotalOutOfRangeCode =
    'ORDER_PAYMENT_TOTAL_OUT_OF_RANGE';

/// Whether a /sync per-operation error message is the live gateway's refusal
/// of a payment whose order total one Telebirr payment cannot take:
/// `ORDER_PAYMENT_TOTAL_OUT_OF_RANGE: ...` since sync-protocol.md 1.24.0, or,
/// from servers before it, `ORDER_VALIDATION: This order total (X ETB) is
/// outside what one Telebirr payment can take (100 to 500000 ETB).`
/// (order-lifecycle.md 1.7.0). The old text is matched for one more release.
bool isPaymentTotalOutOfRangeMessage(String? message) =>
    message != null &&
    (message.startsWith(orderPaymentTotalOutOfRangeCode) ||
        (message.startsWith('ORDER_VALIDATION:') &&
            message.contains('outside what one Telebirr payment can take')));

/// Sync rejection prefix when the live gateway refuses a payment method it
/// cannot fund (FUP-20, sync-protocol.md 1.16.0): a bank transfer
/// (`cbe_transfer`) until ESC-46 builds CBE verification, or any method other
/// than `telebirr`. Nothing was written; the buyer pays with Telebirr.
const String orderPaymentMethodUnavailableCode =
    'ORDER_PAYMENT_METHOD_UNAVAILABLE';

/// Whether a /sync per-operation error message refuses the payment method
/// (`ORDER_PAYMENT_METHOD_UNAVAILABLE: ...`). Permanent like every `ORDER_*`
/// refusal: the local payment is undone.
bool isPaymentMethodUnavailableMessage(String? message) =>
    message != null && message.startsWith(orderPaymentMethodUnavailableCode);

/// Prefix of every `location_ping` refusal (live-trip-api.md section 1,
/// sync-protocol.md 1.31.0).
const String locationCodePrefix = 'LOCATION_';

/// The one `location_ping` answer that is not a refusal: the server sends it
/// with status `deferred` while no delivery of the trip is on the road yet, so
/// the client re-queues the ping without spending a retry.
const String locationNotActiveCode = 'LOCATION_NOT_ACTIVE';

/// Refusal of a ping captured after every delivery of the trip left the road:
/// the phone also stops sharing for that trip.
const String locationSharingEndedCode = 'LOCATION_SHARING_ENDED';

/// Whether a /sync per-operation error message refuses a check-in for good
/// (`LOCATION_VALIDATION`, `LOCATION_NOT_TRUCKER`, `LOCATION_SHARING_ENDED`,
/// `LOCATION_RATE_LIMITED`, `LOCATION_STALE`). Every `LOCATION_*` code is
/// permanent except `LOCATION_NOT_ACTIVE`; an unprefixed error (a failed read
/// on the server) is retried.
bool isLocationRejectionMessage(String? message) =>
    message != null &&
    message.startsWith(locationCodePrefix) &&
    !message.startsWith(locationNotActiveCode);

/// Whether a /sync per-operation error message ends sharing for the ping's
/// trip (`LOCATION_SHARING_ENDED: ...`).
bool isLocationSharingEndedMessage(String? message) =>
    message != null && message.startsWith(locationSharingEndedCode);

/// Request validation error (400)
final class ValidationException extends ApiException {
  const ValidationException(
    super.message, [
    super.statusCode = 400,
    super.code,
  ]);
}

/// Resource not found (404)
final class NotFoundException extends ApiException {
  const NotFoundException([
    super.message = 'Resource not found',
    super.statusCode = 404,
    super.code,
  ]);
}

/// Conflict error (409) - used for sync conflicts
final class ConflictException extends ApiException {
  const ConflictException(super.message, [super.statusCode = 409, super.code]);

  /// Server data that conflicts with local data
  final Map<String, dynamic>? serverData = null;
}

/// Rate limit exceeded (429)
final class RateLimitException extends ApiException {
  const RateLimitException([
    super.message = 'Rate limit exceeded',
    super.statusCode = 429,
    super.code,
  ]);
}

/// Request timeout
final class TimeoutException extends ApiException {
  const TimeoutException([super.message = 'Request timeout']);
}

/// The TLS handshake failed: the server's certificate chain did not lead to
/// a bundled root (certificate pinning) or the connection was tampered with.
/// Deliberately not a [NetworkException]: the device IS online, so the UI
/// must not say "no internet" or offer offline mode (SIM-23).
final class SecureConnectionException extends ApiException {
  const SecureConnectionException([
    super.message = 'Secure connection to the server failed',
  ]);
}

/// Whether [error] (a Dio error, or the raw error it wrapped) is a TLS or
/// certificate failure. Dio reports a rejected pin as `badCertificate` only
/// when its own validator ran; a dart:io HandshakeException from the pinned
/// HttpClient arrives as `unknown` (or `connectionError`), so the wrapped
/// error is matched by name, which keeps this file free of dart:io for web.
bool isSecureConnectionFailure(Object error) {
  if (error is DioException) {
    if (error.type == DioExceptionType.badCertificate) return true;
    final inner = error.error;
    return inner != null && _looksLikeTlsFailure(inner);
  }
  return _looksLikeTlsFailure(error);
}

bool _looksLikeTlsFailure(Object error) {
  final text = '${error.runtimeType} $error';
  return text.contains('HandshakeException') ||
      text.contains('TlsException') ||
      text.contains('CertificateException') ||
      text.contains('CERTIFICATE_VERIFY_FAILED') ||
      text.contains('SSL certificate');
}

/// Generic unknown error
final class UnknownException extends ApiException {
  const UnknownException([super.message = 'Unknown error occurred']);
}

/// A /sync batch operation was rejected by the server.
///
/// The /sync endpoint returns HTTP 200 with a per-operation `results` array;
/// an individual operation can still fail (e.g. `CROP_UNAUTHORIZED`). Web
/// repositories POST directly to /sync, so they must surface these rejections
/// instead of silently treating the HTTP 200 as success.
final class SyncRejectedException extends ApiException {
  const SyncRejectedException(super.message);
}

/// The server postponed a /sync operation (`status == "deferred"`), e.g. a
/// pickup whose farmer QR nonce has not synced yet. Nothing was written. The
/// native sync queue retries these; web has no queue, so it is a failure there.
final class SyncDeferredException extends SyncRejectedException {
  const SyncDeferredException(super.message);
}

/// The server answered a /sync operation with `status == "conflict"`: the
/// version the client sent is behind (someone changed the entity first), or
/// a create named an entity that already exists. Nothing was written. The
/// native sync queue resolves these server-wins; web has no queue, so it is a
/// failure there.
final class SyncConflictException extends SyncRejectedException {
  const SyncConflictException(super.message);
}

/// Throws [SyncRejectedException] if any operation in a /sync batch response
/// reports `status == "error"`, [SyncDeferredException] for `"deferred"`, or
/// [SyncConflictException] for `"conflict"`.
///
/// No-op when the response shape is unexpected (e.g. an empty body) so it
/// never masks an otherwise-successful write.
void ensureSyncSucceeded(Map<String, dynamic> syncResponse) {
  final results = syncResponse['results'];
  if (results is! List) return;
  for (final result in results) {
    if (result is! Map) continue;
    final message = result['message'] as String?;
    if (result['status'] == 'error') {
      throw SyncRejectedException(message ?? 'Sync operation was rejected.');
    }
    if (result['status'] == 'deferred') {
      throw SyncDeferredException(message ?? 'Sync operation was deferred.');
    }
    if (result['status'] == 'conflict') {
      throw SyncConflictException(message ?? 'Sync operation conflicted.');
    }
  }
}
