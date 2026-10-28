import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/app_notification.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/notifications/data/notification_api.dart';
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
];

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
    _ => (title: l10n.notifGenericTitle, body: l10n.notifGenericBody),
  };
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
    'order_request' || 'transport_request' || 'request_reminder' => (
        icon: Icons.assignment_late_outlined,
        color: AppTheme.accentGold
      ),
    'transport_failed' ||
    'transport_failed_keep_crops' ||
    'payment_failed' ||
    'pickup_overdue' ||
    'pickup_missed' ||
    'payout_blocked' ||
    'order_rejected' =>
      (icon: Icons.error_outline, color: AppTheme.statusError),
    'delivery_scheduled' ||
    'buyer_collecting' ||
    'picked_up' ||
    'handoff_recorded' ||
    'in_transit' =>
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
      (icon: Icons.gavel_outlined, color: AppTheme.accentGold),
    _ => (icon: Icons.notifications_outlined, color: AppTheme.statusInfo),
  };
}
