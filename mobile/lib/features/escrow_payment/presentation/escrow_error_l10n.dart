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
    'ESCROW_005' => l10n.escrowErrorAuthRequired,
    'ESCROW_009' => l10n.escrowErrorConflict,
    'ESCROW_011' => l10n.escrowErrorRateLimit,
    'ESCROW_013' => l10n.escrowErrorGateway,
    'ESCROW_014' => l10n.escrowErrorServiceUnavailable,
    'ESCROW_016' => l10n.escrowPaymentNotFound,
    'ESCROW_016_DELIVERY' => l10n.escrowPaymentNotFoundForDelivery,
    'ESCROW_OFFLINE' => l10n.escrowErrorOffline,
    _ => l10n.escrowErrorGeneric,
  };
}
