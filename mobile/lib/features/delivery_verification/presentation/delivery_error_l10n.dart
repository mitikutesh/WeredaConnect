import 'package:wereda_connect/features/delivery_verification/data/delivery_validation_exception.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Map delivery error codes to localized user-facing messages.
///
/// Used by delivery screens to display localized error messages
/// instead of raw English strings from the BLoC.
String localizeDeliveryError(AppLocalizations l10n, String? code) {
  return switch (code) {
    DeliveryErrorCode.invalidId => l10n.deliveryErrorInvalidId,
    DeliveryErrorCode.statusInvalid => l10n.deliveryErrorStatusInvalid,
    DeliveryErrorCode.unauthorized => l10n.deliveryErrorUnauthorized,
    DeliveryErrorCode.disputeExpired => l10n.deliveryErrorDisputeExpired,
    DeliveryErrorCode.disputeReasonRequired =>
      l10n.deliveryErrorDisputeReasonRequired,
    DeliveryErrorCode.gpsInvalid => l10n.deliveryErrorGpsInvalid,
    DeliveryErrorCode.notFound => l10n.deliveryErrorNotFound,
    DeliveryErrorCode.qrPending => l10n.deliveryErrorQrPending,
    _ => l10n.deliveryErrorGeneric,
  };
}
