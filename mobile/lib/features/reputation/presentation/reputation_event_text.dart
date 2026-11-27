import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// ESC-31: the plain line for a reputation event [code]
/// (reputation-api.md 1.1), shared by My reputation and the inbox. Says
/// what happened, never what the person is. A code this build does not
/// know reads as a neutral order event.
String reputationEventTitle(AppLocalizations l10n, String? code) =>
    switch (code) {
      'DECLINED_REQUEST' => l10n.reputationEventDeclined,
      'RESPONSE_EXPIRED' => l10n.reputationEventExpired,
      'CANCELLED_AFTER_ACCEPT' => l10n.reputationEventCancelled,
      'LATE_CANCEL' => l10n.reputationEventLateCancel,
      'TRIP_CANCELLED_WITH_BOOKINGS' => l10n.reputationEventTripCancelled,
      'NO_SHOW' => l10n.reputationEventNoShow,
      'DELIVERY_OVERDUE' => l10n.reputationEventOverdue,
      'CANCELLED_TRANSPORT_FAILED' => l10n.reputationEventTransportFailedCancel,
      'ORDER_COMPLETED' => l10n.reputationEventCompleted,
      _ => l10n.reputationEventOther,
    };
