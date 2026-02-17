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
  const AuthException(
      [super.message = 'Authentication failed', super.statusCode = 401]);
}

/// Request validation error (400)
final class ValidationException extends ApiException {
  const ValidationException(super.message, [super.statusCode = 400]);
}

/// Resource not found (404)
final class NotFoundException extends ApiException {
  const NotFoundException(
      [super.message = 'Resource not found', super.statusCode = 404]);
}

/// Conflict error (409) - used for sync conflicts
final class ConflictException extends ApiException {
  const ConflictException(super.message, [super.statusCode = 409]);

  /// Server data that conflicts with local data
  final Map<String, dynamic>? serverData = null;
}

/// Rate limit exceeded (429)
final class RateLimitException extends ApiException {
  const RateLimitException(
      [super.message = 'Rate limit exceeded', super.statusCode = 429]);
}

/// Request timeout
final class TimeoutException extends ApiException {
  const TimeoutException([super.message = 'Request timeout']);
}

/// Generic unknown error
final class UnknownException extends ApiException {
  const UnknownException([super.message = 'Unknown error occurred']);
}
