import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/app_notification.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/notifications/data/notification_api.dart';
import 'package:wereda_connect/features/reputation/presentation/reputation_event_text.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Every notification type the server emits (`NotificationTypes.All`,
/// notifications-api.md section 3). Each has its own text below; a type not
/// in this list renders the generic "Order update" line.
const List<String> notificationTypes = [
  'payment_confirmed',
  'order_request',
  'farmer_accepted',
  'transport_request',
  'order_confirmed',
  'order_rejected',
  'transport_failed',
  'transport_failed_keep_crops',
  'request_expired',
  'request_on_hold',
  'order_cancelled',
  'order_expired_unpaid',
  'payment_failed',
  'request_reminder',
  'pickup_overdue',
  'pickup_missed',
  'delivery_scheduled',
  'buyer_collecting',
  'picked_up',
  'handoff_recorded',
  'in_transit',
  'truck_nearby',
  'delivered',
  'delivered_payout_pending',
  'order_settled',
  'refund_sent',
  'payout_sent',
  'payout_blocked',
  'payout_processing',
  'dispute_opened',
  'dispute_received',
  'dispute_resolved',
  'under_review',
  // ESC-31 (notifications-api.md 1.5.0): the party's own reputation.
  'penalty_recorded',
  'tier_changed',
  // FUP-286 and FUP-338 (notifications-api.md 1.6.0): the check-ins.
  'pickup_reminder',
  'trucker_arrived',
  'buyer_arrived',
  'crops_ready',
  'delivery_arrived',
  'delivery_confirm_reminder',
];

/// ESC-31: the types about the recipient's own reputation. They name no
/// order: tapping one opens My reputation.
const Set<String> reputationNotificationTypes = {
  'penalty_recorded',
  'tier_changed',
};

/// Types that ask the recipient to act (`NotificationTypes.ActionRequired`).
const Set<String> actionRequiredNotificationTypes = {
  'order_request',
  'transport_request',
  'transport_failed',
  'payment_failed',
  'delivered',
  'pickup_overdue',
  'payout_blocked',
  'request_reminder',
  'pickup_reminder',
  'delivery_arrived',
  'delivery_confirm_reminder',
};

/// The inbox title and body of [notification] in the app's language. Names
/// come from the entry's params; a missing name reads as the role.
({String title, String body}) notificationText(
  AppLocalizations l10n,
  AppNotification notification,
) {
  final p = notificationParams(notification);
  String name(String key, String role) {
    final value = partyDisplayName(p[key], l10n).trim();
    return value.isEmpty ? role : value;
  }

  // FUP-286 and FUP-338: a judged pickup or drop-off says what was found.
  final judged = _judgedText(l10n, notification.type, p['reason']);
  if (judged != null) return judged;

  final buyer = name('buyerName', l10n.buyer);
  final farmer = name('farmerName', l10n.farmer);
  final trucker = name('truckerName', l10n.trucker);
  return switch (notification.type) {
    'payment_confirmed' => (
        title: l10n.notifPaymentConfirmedTitle,
        body: l10n.notifPaymentConfirmedBody,
      ),
    'order_request' => (
        title: l10n.notifOrderRequestTitle,
        body: l10n.notifOrderRequestBody(buyer),
      ),
    'farmer_accepted' => (
        title: l10n.notifFarmerAcceptedTitle,
        body: l10n.notifFarmerAcceptedBody(farmer),
      ),
    'transport_request' => (
        title: l10n.notifTransportRequestTitle,
        body: l10n.notifTransportRequestBody(farmer),
      ),
    'order_confirmed' => (
        title: l10n.notifOrderConfirmedTitle,
        body: l10n.notifOrderConfirmedBody,
      ),
    'order_rejected' => (
        title: l10n.notifOrderRejectedTitle,
        body: l10n.notifOrderRejectedBody(farmer),
      ),
    'transport_failed' => (
        title: l10n.notifTransportFailedTitle,
        body: l10n.notifTransportFailedBody,
      ),
    'transport_failed_keep_crops' => (
        title: l10n.notifTransportFailedKeepCropsTitle,
        body: l10n.notifTransportFailedKeepCropsBody,
      ),
    'request_expired' => (
        title: l10n.notifRequestExpiredTitle,
        body: l10n.notifRequestExpiredBody,
      ),
    'request_on_hold' => (
        title: l10n.notifRequestOnHoldTitle,
        body: l10n.notifRequestOnHoldBody,
      ),
    'order_cancelled' => (
        title: l10n.notifOrderCancelledTitle,
        body: l10n.notifOrderCancelledBody,
      ),
    'order_expired_unpaid' => (
        title: l10n.notifOrderExpiredUnpaidTitle,
        body: l10n.notifOrderExpiredUnpaidBody,
      ),
    'payment_failed' => (
        title: l10n.notifPaymentFailedTitle,
        body: l10n.notifPaymentFailedBody,
      ),
    'request_reminder' => (
        title: l10n.notifRequestReminderTitle,
        body: l10n.notifRequestReminderBody,
      ),
    'pickup_overdue' => (
        title: l10n.notifPickupOverdueTitle,
        body: l10n.notifPickupOverdueBody,
      ),
    'pickup_missed' => (
        title: l10n.notifPickupMissedTitle,
        body: l10n.notifPickupMissedBody,
      ),
    'delivery_scheduled' => (
        title: l10n.notifDeliveryScheduledTitle,
        body: l10n.notifDeliveryScheduledBody(trucker),
      ),
    'buyer_collecting' => (
        title: l10n.notifBuyerCollectingTitle,
        body: l10n.notifBuyerCollectingBody(buyer),
      ),
    'picked_up' => (
        title: l10n.notifPickedUpTitle,
        body: l10n.notifPickedUpBody,
      ),
    'handoff_recorded' => (
        title: l10n.notifHandoffRecordedTitle,
        body: l10n.notifHandoffRecordedBody,
      ),
    'in_transit' => (
        title: l10n.notifInTransitTitle,
        body: l10n.notifInTransitBody,
      ),
    'truck_nearby' => (
        title: l10n.notifTruckNearbyTitle,
        body: l10n.notifTruckNearbyBody(trucker),
      ),
    'delivered' => (
        title: l10n.notifDeliveredTitle,
        body: l10n.notifDeliveredBody,
      ),
    'delivered_payout_pending' => (
        title: l10n.notifDeliveredPayoutPendingTitle,
        body: l10n.notifDeliveredPayoutPendingBody,
      ),
    'order_settled' => (
        title: l10n.notifOrderSettledTitle,
        body: l10n.notifOrderSettledBody,
      ),
    'refund_sent' => (
        title: l10n.notifRefundSentTitle,
        body: l10n.notifRefundSentBody,
      ),
    'payout_sent' => (
        title: l10n.notifPayoutSentTitle,
        body: l10n.notifPayoutSentBody,
      ),
    'payout_blocked' => (
        title: l10n.notifPayoutBlockedTitle,
        body: l10n.notifPayoutBlockedBody(_payoutHint(l10n, p['reason'])),
      ),
    'payout_processing' => (
        title: l10n.notifPayoutProcessingTitle,
        body: l10n.notifPayoutProcessingBody,
      ),
    'dispute_opened' => (
        title: l10n.notifDisputeOpenedTitle,
        body: l10n.notifDisputeOpenedBody,
      ),
    'dispute_received' => (
        title: l10n.notifDisputeReceivedTitle,
        body: l10n.notifDisputeReceivedBody,
      ),
    'dispute_resolved' => (
        title: l10n.notifDisputeResolvedTitle,
        body: switch (p['resolution']) {
          'farmer_wins' => l10n.notifDisputeResolvedFarmerWins,
          'buyer_wins' => l10n.notifDisputeResolvedBuyerWins,
          _ => l10n.notifDisputeResolvedBody,
        },
      ),
    'under_review' => (
        title: l10n.notifUnderReviewTitle,
        body: l10n.notifUnderReviewBody,
      ),
    'penalty_recorded' => (
        title: l10n.notifPenaltyRecordedTitle,
        body: l10n.notifPenaltyRecordedBody(
          reputationEventTitle(l10n, p['code']),
          p['points'] ?? '',
        ),
      ),
    'tier_changed' => (
        title: l10n.notifTierChangedTitle,
        body: _tierChangedBody(l10n, p),
      ),
    'pickup_reminder' => (
        title: l10n.notifPickupReminderTitle,
        body: l10n.notifPickupReminderBody,
      ),
    'trucker_arrived' => (
        title: l10n.notifTruckerArrivedTitle,
        body: l10n.notifTruckerArrivedBody(trucker),
      ),
    'buyer_arrived' => (
        title: l10n.notifBuyerArrivedTitle,
        body: l10n.notifBuyerArrivedBody(buyer),
      ),
    'crops_ready' => (
        title: l10n.notifCropsReadyTitle,
        body: l10n.notifCropsReadyBody(farmer),
      ),
    'delivery_arrived' => (
        title: l10n.notifDeliveryArrivedTitle,
        body: l10n.notifDeliveryArrivedBody(trucker),
      ),
    'delivery_confirm_reminder' => (
        title: l10n.notifDeliveryConfirmReminderTitle,
        body: l10n.notifDeliveryConfirmReminderBody,
      ),
    _ => (title: l10n.notifGenericTitle, body: l10n.notifGenericBody),
  };
}

/// FUP-286 and FUP-338 (notifications-api.md 1.6.0): the words of a judged
/// pickup or drop-off, by the entry's type and `reason`, as the push says
/// them. Plain and without blame: what the check-ins show. Null keeps the
/// type's own words.
({String title, String body})? _judgedText(
  AppLocalizations l10n,
  String type,
  String? reason,
) {
  String? title;
  String? body;
  switch ('$type#$reason') {
    case 'transport_failed#trucker_no_show':
      title = l10n.notifTruckerNoShowTitle;
      body = l10n.notifTruckerNoShowBuyerBody;
    case 'transport_failed_keep_crops#trucker_no_show':
      title = l10n.notifTruckerNoShowTitle;
      body = l10n.notifTruckerNoShowFarmerBody;
    case 'pickup_missed#trucker_no_show':
      title = l10n.notifPickupCheckInMissedTitle;
      body = l10n.notifPickupCheckInMissedBody;
    case 'order_cancelled#farmer_no_show':
      title = l10n.notifOrderCancelledTitle;
      body = l10n.notifFarmerNoShowBody;
    case 'order_cancelled#buyer_no_show':
      title = l10n.notifOrderCancelledTitle;
      body = l10n.notifBuyerNoShowBody;
    case 'pickup_overdue#both_present_no_scan':
    case 'pickup_overdue#unchecked_check_in':
    case 'pickup_missed#both_present_no_scan':
    case 'pickup_missed#unchecked_check_in':
      title = l10n.notifPickupReviewTitle;
      body = l10n.notifPickupReviewBody;
    case 'delivered#auto_confirmed':
      title = l10n.notifAutoConfirmedTitle;
      body = l10n.notifAutoConfirmedBuyerBody;
    case 'delivered_payout_pending#auto_confirmed':
      title = l10n.notifDeliveredPayoutPendingTitle;
      body = l10n.notifAutoConfirmedPayeeBody;
    case 'dispute_opened#delivery_overdue':
      title = l10n.notifDeliveryReviewTitle;
      body = l10n.notifDeliveryOverdueBody;
    case 'dispute_opened#silent_buyer':
      title = l10n.notifDeliveryReviewTitle;
      body = l10n.notifSilentBuyerBody;
    case 'dispute_opened#dropoff_out_of_range':
      title = l10n.notifDeliveryReviewTitle;
      body = l10n.notifDropoffOutOfRangeBody;
  }
  if (title == null || body == null) return null;
  return (title: title, body: body);
}

/// What the party's new tier means for them, in the words My reputation
/// uses. Only their own tier ever reaches them.
String _tierChangedBody(AppLocalizations l10n, Map<String, String> p) {
  switch (p['toTier']) {
    case 'restricted':
      final until = DateTime.tryParse(p['restrictedUntil'] ?? '');
      return until == null
          // No end date: an admin review is open.
          ? l10n.reputationRestrictedReview
          : l10n.reputationRestrictedUntil(
              DateFormat.yMMMd().format(dayInEthiopia(until.toUtc())),
            );
    case 'low':
      return l10n.reputationTierLowBody;
    case 'warning':
      return l10n.notifTierWarningBody;
    default:
      return l10n.notifTierGoodBody;
  }
}

String _payoutHint(AppLocalizations l10n, String? reason) {
  final code = reason ?? '';
  if (code.contains('fayda')) return l10n.notifPayoutHintFayda;
  if (code.contains('phone')) return l10n.notifPayoutHintPhone;
  if (code.contains('profile')) return l10n.notifPayoutHintProfile;
  return l10n.notifPayoutHintOther;
}

final _amountFormat = NumberFormat('#,##0.00', 'en_US');

/// The facts line under an inbox entry: crop and quantity, the amount, and
/// for a type that asks the user to act, when it is due (Ethiopia time on
/// the viewer's [clock], LT-03). Null when the entry carries none of them.
ClockLabel? notificationDetailLine(
  AppLocalizations l10n,
  ClockFormat clock,
  AppNotification notification,
) {
  final p = notificationParams(notification);
  final parts = <String>[];
  final crop = p['cropType'];
  final quantity = p['quantityKg'];
  if (crop != null && crop.isNotEmpty) {
    parts.add(CropType.labelFor(crop, l10n));
  }
  if (quantity != null && quantity.isNotEmpty) {
    parts.add(l10n.quantityKg(quantity));
  }
  final amount = double.tryParse(p['amountEtb'] ?? '');
  if (amount != null) parts.add(l10n.etbCurrency(_amountFormat.format(amount)));
  final facts = parts.join(' · ');
  final deadline = DateTime.tryParse(p['deadlineAt'] ?? '');
  if (deadline != null &&
      actionRequiredNotificationTypes.contains(notification.type)) {
    final due = clock.deadline(
      deadline,
      date: DateFormat.MMMd(),
      sentence: l10n.actionDueBy,
    );
    return facts.isEmpty ? due : due.prefixed('$facts · ');
  }
  return facts.isEmpty ? null : ClockLabel.text(facts);
}

/// The icon and colour of an inbox entry's type.
({IconData icon, Color color}) notificationStyle(String type) {
  return switch (type) {
    'order_request' ||
    'transport_request' ||
    'request_reminder' ||
    'pickup_reminder' ||
    'delivery_confirm_reminder' =>
      (icon: Icons.assignment_late_outlined, color: AppTheme.statusWarning),
    'transport_failed' ||
    'transport_failed_keep_crops' ||
    'payment_failed' ||
    'pickup_overdue' ||
    'pickup_missed' ||
    'payout_blocked' ||
    'order_rejected' =>
      (icon: Icons.error_outline, color: AppTheme.statusError),
    'penalty_recorded' || 'tier_changed' => (
        icon: Icons.workspace_premium_outlined,
        color: AppTheme.statusWarning
      ),
    'delivery_scheduled' ||
    'buyer_collecting' ||
    'picked_up' ||
    'handoff_recorded' ||
    'in_transit' ||
    'truck_nearby' ||
    'trucker_arrived' ||
    'buyer_arrived' ||
    'crops_ready' ||
    'delivery_arrived' =>
      (icon: Icons.local_shipping_outlined, color: AppTheme.statusInfo),
    'delivered' ||
    'delivered_payout_pending' ||
    'order_confirmed' ||
    'order_settled' =>
      (icon: Icons.check_circle_outline, color: AppTheme.statusSuccess),
    'payment_confirmed' ||
    'refund_sent' ||
    'payout_sent' ||
    'payout_processing' =>
      (
        icon: Icons.account_balance_wallet_outlined,
        color: AppTheme.statusSuccess,
      ),
    'dispute_opened' ||
    'dispute_received' ||
    'dispute_resolved' ||
    'under_review' =>
      (icon: Icons.gavel_outlined, color: AppTheme.statusWarning),
    _ => (icon: Icons.notifications_outlined, color: AppTheme.statusInfo),
  };
}
