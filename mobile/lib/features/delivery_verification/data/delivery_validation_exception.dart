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
  static const disputeExpired = 'DELIVERY_DISPUTE_EXPIRED';
  static const disputeReasonRequired = 'DELIVERY_DISPUTE_REASON_REQUIRED';
  static const gpsInvalid = 'DELIVERY_GPS_INVALID';
  static const notFound = 'DELIVERY_NOT_FOUND';
  static const generic = 'DELIVERY_GENERIC';
}
