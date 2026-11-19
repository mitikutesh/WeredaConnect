import 'package:intl/intl.dart';
import 'package:wereda_connect/core/utils/date_locale.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
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
    // Order checkout (ESC-44). ESCROW_030 has three causes; the checkout
    // screen tells them apart (order_checkout_l10n.dart), this is the
    // generic one.
    'ESCROW_030' => l10n.orderCheckoutNotWaiting,
    'ESCROW_031' => l10n.orderCheckoutConfirmedAutomatically,
    'ESCROW_032' => l10n.orderCheckoutAlreadyOpen,
    'ESCROW_033' => l10n.orderCheckoutOrderMissing,
    'ESCROW_038' => l10n.escrowErrorPayoutInProgress,
    // FUP-36: live Telebirr refuses the legacy delivery payment; the order is
    // paid from its order page instead.
    'ESCROW_039' => l10n.escrowErrorDeliveryPaymentUnavailable,
    // FUP-16: a dispute before the goods are picked up.
    'ESCROW_041' => l10n.escrowErrorDisputeBeforePickup,
    'ESCROW_INVALID_TRANSITION' => l10n.escrowErrorInvalidTransition,
    // FUP-77: a dispute refused because the buyer's refund is scheduled
    // (escrowCodeOf reads it from the server's text).
    'ESCROW_INVALID_TRANSITION_REFUND_SCHEDULED' =>
      l10n.escrowErrorDisputeRefundScheduled,
    // Persona simulation BUG-8: a buyer's "not delivered" dispute while the
    // goods are on the road (escrowCodeOf reads it from the server's text).
    'ESCROW_INVALID_TRANSITION_GOODS_ON_THE_WAY' =>
      _goodsOnTheWay(l10n, fallback),
    'ESCROW_OFFLINE' => l10n.escrowErrorOffline,
    // The account is under review (403 on initiate and order checkout).
    'FRAUD_ACCOUNT_HELD' => l10n.fraudAccountHeld,
    _ => l10n.escrowErrorGeneric,
  };
}

/// The time in the server's "The goods are still on the way" refusal
/// ("... after 2026-11-22 08:46 UTC. ..."), or null when it is not there.
final RegExp _opensAtPattern =
    RegExp(r'after (\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2}) UTC');

/// BUG-8: the refusal with the time it opens, shown in Ethiopia time like
/// every server timestamp in the app (FUP-58); the text without a time when
/// the server's message holds none that can be read.
String _goodsOnTheWay(AppLocalizations l10n, String serverMessage) {
  final opensAt = goodsOnTheWayOpensAt(serverMessage);
  if (opensAt == null) return l10n.escrowErrorDisputeGoodsOnTheWay;
  // The day through the dual-date helper (FUP-545: the Ethiopian date first in
  // Amharic and Afaan Oromoo), the time as the 24-hour Ethiopia time.
  final time = '${formatMonthDay(dayInEthiopia(opensAt))} '
      '${DateFormat.Hm().format(instantInEthiopia(opensAt))}';
  return l10n.escrowErrorDisputeGoodsOnTheWayAfter(time);
}

/// The UTC instant named in the server's on-the-way refusal, or null.
DateTime? goodsOnTheWayOpensAt(String serverMessage) {
  final match = _opensAtPattern.firstMatch(serverMessage);
  if (match == null) return null;
  final parts = [for (var i = 1; i <= 5; i++) int.parse(match.group(i)!)];
  final instant =
      DateTime.utc(parts[0], parts[1], parts[2], parts[3], parts[4]);
  // DateTime.utc rolls an impossible date over (month 13): not a real time.
  if (instant.year != parts[0] ||
      instant.month != parts[1] ||
      instant.day != parts[2] ||
      instant.hour != parts[3] ||
      instant.minute != parts[4]) {
    return null;
  }
  return instant;
}
