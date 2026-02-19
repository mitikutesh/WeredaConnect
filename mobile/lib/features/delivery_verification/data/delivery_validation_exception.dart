/// Validation error thrown when delivery data fails client-side checks.
///
/// The [field] and [message] can be used by the BLoC to display
/// localized error messages to the user.
class DeliveryValidationException implements Exception {
  final String field;
  final String message;

  DeliveryValidationException(this.field, this.message);

  @override
  String toString() => 'DeliveryValidationException($field): $message';
}
