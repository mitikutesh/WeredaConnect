/// Network and API exceptions for error handling
library;

/// Base exception for all API errors
sealed class ApiException implements Exception {
  const ApiException(this.message, [this.statusCode]);

  final String message;
  final int? statusCode;

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
  const ServerException(super.message, super.statusCode);
}

/// Authentication/authorization error (401, 403)
final class AuthException extends ApiException {
  const AuthException([
    super.message = 'Authentication failed',
    super.statusCode = 401,
  ]);
}

/// Server error code for "step-up required": the ID token's `auth_time` is
/// older than the freshness window for a high-value money action.
const String stepUpRequiredCode = 'AUTH_040';

/// Sync rejection prefix when a payment needs Fayda KYC or a verified phone.
const String orderKycRequiredCode = 'ORDER_KYC_REQUIRED';

/// The server wants the user to re-enter their password before a money
/// action (REST 403 `AUTH_040`). Not an [AuthException]: the session is still
/// valid, so callers must re-authenticate and retry instead of logging out.
final class StepUpRequiredException extends ApiException {
  const StepUpRequiredException([
    super.message = 'Please confirm it is you',
    super.statusCode = 403,
  ]);
}

/// Whether a /sync per-operation error message is a step-up rejection
/// (`AUTH_040: ...`).
bool isStepUpRequiredMessage(String? message) =>
    message != null && message.startsWith(stepUpRequiredCode);

/// Whether a /sync per-operation error message is a KYC gate rejection
/// (`ORDER_KYC_REQUIRED: ...`).
bool isKycRequiredMessage(String? message) =>
    message != null && message.startsWith(orderKycRequiredCode);

/// Request validation error (400)
final class ValidationException extends ApiException {
  const ValidationException(super.message, [super.statusCode = 400]);
}

/// Resource not found (404)
final class NotFoundException extends ApiException {
  const NotFoundException([
    super.message = 'Resource not found',
    super.statusCode = 404,
  ]);
}

/// Conflict error (409) - used for sync conflicts
final class ConflictException extends ApiException {
  const ConflictException(super.message, [super.statusCode = 409]);

  /// Server data that conflicts with local data
  final Map<String, dynamic>? serverData = null;
}

/// Rate limit exceeded (429)
final class RateLimitException extends ApiException {
  const RateLimitException([
    super.message = 'Rate limit exceeded',
    super.statusCode = 429,
  ]);
}

/// Request timeout
final class TimeoutException extends ApiException {
  const TimeoutException([super.message = 'Request timeout']);
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

/// Throws [SyncRejectedException] if any operation in a /sync batch response
/// reports `status == "error"`.
///
/// No-op when the response shape is unexpected (e.g. an empty body) so it
/// never masks an otherwise-successful write.
void ensureSyncSucceeded(Map<String, dynamic> syncResponse) {
  final results = syncResponse['results'];
  if (results is! List) return;
  for (final result in results) {
    if (result is Map && result['status'] == 'error') {
      throw SyncRejectedException(
        (result['message'] as String?) ?? 'Sync operation was rejected.',
      );
    }
  }
}
