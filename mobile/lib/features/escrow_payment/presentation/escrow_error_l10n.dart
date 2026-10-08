import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Map escrow error codes to localized user-facing messages.
///
/// Used by escrow screens to display localized error messages
/// instead of raw English strings from the BLoC.
String localizeEscrowError(
  AppLocalizations l10n,
  String code,
  String fallback,
) {
  return switch (code) {
    'ESCROW_001' => l10n.escrowErrorInvalidAmount,
    'ESCROW_002' => l10n.escrowErrorInvalidDelivery,
    'ESCROW_003' => l10n.escrowErrorGeneric, // idempotency key - internal
    'ESCROW_004' => l10n.escrowErrorGeneric, // currency must be ETB - internal
    'ESCROW_005' => l10n.escrowErrorAuthRequired,
    'ESCROW_006' => l10n.escrowErrorUnauthorized,
    'ESCROW_007' => l10n.escrowErrorFaydaRequired,
    'ESCROW_008' => l10n.escrowErrorDeliveryNotFound,
    'ESCROW_009' => l10n.escrowErrorAlreadyExists,
    'ESCROW_010' => l10n.escrowErrorInvalidDeliveryState,
    'ESCROW_011' => l10n.escrowErrorRateLimit,
    'ESCROW_012' => l10n.escrowErrorGeneric, // internal server error
    'ESCROW_013' => l10n.escrowErrorGateway,
    'ESCROW_014' => l10n.escrowErrorServiceUnavailable,
    'ESCROW_016' => l10n.escrowPaymentNotFound,
    'ESCROW_016_DELIVERY' => l10n.escrowPaymentNotFoundForDelivery,
    'ESCROW_017' => l10n.escrowErrorInvalidDisputeReason,
    'ESCROW_018' => l10n.escrowErrorDisputeWindowExpired,
    'ESCROW_019' => l10n.escrowErrorUnauthorized,
    'ESCROW_020' => l10n.escrowErrorDisputeAlreadyExists,
    'ESCROW_022' => l10n.escrowErrorUnauthorized,
    'ESCROW_024' => l10n.escrowErrorOrderAlreadyFunded,
    'ESCROW_025' => l10n.escrowErrorUnauthorized,
    'ESCROW_026' => l10n.escrowErrorUnauthorized,
    'ESCROW_028' => l10n.escrowErrorInvalidAmount,
    'ESCROW_038' => l10n.escrowErrorPayoutInProgress,
    'ESCROW_INVALID_TRANSITION' => l10n.escrowErrorInvalidTransition,
    'ESCROW_OFFLINE' => l10n.escrowErrorOffline,
    _ => l10n.escrowErrorGeneric,
  };
}
