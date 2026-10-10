import 'package:wereda_connect/core/network/api_exception.dart';

/// Validation error thrown when delivery data fails client-side checks.
///
/// The [field] and [code] are used by the presentation layer to map
/// to localized error messages via l10n.
class DeliveryValidationException implements Exception {
  final String field;
  final String message;
  final String code;

  DeliveryValidationException(this.field, this.message, {String? code})
      : code = code ?? _deriveCode(field);

  /// Derive a stable error code from the field name.
  static String _deriveCode(String field) {
    return switch (field) {
      'deliveryId' => DeliveryErrorCode.invalidId,
      'status' => DeliveryErrorCode.statusInvalid,
      'truckerId' || 'buyerId' || 'userId' => DeliveryErrorCode.unauthorized,
      'confirmedAt' => DeliveryErrorCode.disputeExpired,
      'disputeReason' => DeliveryErrorCode.disputeReasonRequired,
      'gps' || 'latitude' || 'longitude' => DeliveryErrorCode.gpsInvalid,
      _ => DeliveryErrorCode.generic,
    };
  }

  @override
  String toString() => 'DeliveryValidationException($field): $message';
}

/// Error codes for delivery validation failures.
///
/// Used by the presentation layer to map to localized strings.
class DeliveryErrorCode {
  static const invalidId = 'DELIVERY_INVALID_ID';
  static const statusInvalid = 'DELIVERY_STATUS_INVALID';
  static const unauthorized = 'DELIVERY_UNAUTHORIZED';
  static const disputeExpired = deliveryDisputeExpiredCode;
  static const disputeReasonRequired = 'DELIVERY_DISPUTE_REASON_REQUIRED';
  static const gpsInvalid = 'DELIVERY_GPS_INVALID';
  static const notFound = 'DELIVERY_NOT_FOUND';
  static const qrPending = 'DELIVERY_NONCE_PENDING';

  /// A cancel after pickup, refused locally or by the server (ESC-08).
  static const cancelNotAllowed = deliveryCancelNotAllowedCode;

  /// A dispute the server refused because the payment could not be frozen
  /// (ESC-07): queued for an admin payout, being settled or no longer held.
  static const disputeRefused = deliveryDisputeRefusedCode;
  static const generic = 'DELIVERY_GENERIC';
}
