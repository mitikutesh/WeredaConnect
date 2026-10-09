/// Network and API exceptions for error handling
library;

import 'package:dio/dio.dart';

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

/// Whether a /sync per-operation error message is a permanent order
/// rejection whose local change the client must undo: every `ORDER_*` code
/// except `ORDER_KYC_REQUIRED`, which keeps its own revert and Fayda prompt
/// (specs/contracts/sync-protocol.md 1.10.0). Replaying the same payload gets
/// the same answer, so these are never retried.
bool isRevertableRejectionMessage(String? message) =>
    message != null &&
    message.startsWith('ORDER_') &&
    !isKycRequiredMessage(message);

/// Sync rejection prefix when a party tries to cancel a delivery after pickup
/// (ESC-08, sync-protocol.md 1.11.0).
const String deliveryCancelNotAllowedCode = 'DELIVERY_CANCEL_NOT_ALLOWED';

/// Whether a /sync per-operation error message refuses a delivery cancel
/// (`DELIVERY_CANCEL_NOT_ALLOWED: ...`). Permanent: the delivery was picked
/// up, so the client undoes its local cancel instead of retrying.
bool isDeliveryCancelRefusalMessage(String? message) =>
    message != null && message.startsWith(deliveryCancelNotAllowedCode);

/// Whether a /sync per-operation error message is the live gateway's refusal
/// of a payment whose order total one Telebirr payment cannot take
/// (`ORDER_VALIDATION: This order total (X ETB) is outside what one Telebirr
/// payment can take (100 to 500000 ETB).`, order-lifecycle.md 1.7.0).
bool isPaymentTotalOutOfRangeMessage(String? message) =>
    message != null &&
    message.startsWith('ORDER_VALIDATION:') &&
    message.contains('outside what one Telebirr payment can take');

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
  const ConflictException(
    super.message, [
    super.statusCode = 409,
    super.code,
  ]);

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
