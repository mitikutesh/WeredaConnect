import 'dart:convert';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/app_notification.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/features/notifications/presentation/notification_text.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

AppNotification _entry(String type, [Map<String, String> params = const {}]) =>
    AppNotification()
      ..notificationId = 'id-$type'
      ..userId = 'user-1'
      ..type = type
      ..entity = 'order'
      ..entityId = 'ord-1'
      ..orderId = 'ord-1'
      ..params = jsonEncode(params)
      ..createdAt = DateTime.utc(2026, 10, 26, 8);

/// ESC-21: every type the server emits has an inbox text in all three
/// languages (notifications-api.md invariant 7).
void main() {
  const locales = [Locale('en'), Locale('am'), Locale('om')];

  test('the type list matches the server (41 types, no repeats)', () {
    // 33 order types, since ESC-31 the two reputation types, and since
    // FUP-286 and FUP-338 the six check-in types.
    expect(notificationTypes, hasLength(41));
    expect(notificationTypes.toSet(), hasLength(41));
    expect(
      notificationTypes.toSet().containsAll(reputationNotificationTypes),
      isTrue,
    );
    expect(
      notificationTypes.toSet().containsAll(actionRequiredNotificationTypes),
      isTrue,
    );
  });

  for (final locale in locales) {
    final l10n = lookupAppLocalizations(locale);
    final code = locale.languageCode;

    test('$code: every type has its own title and a body', () {
      for (final type in notificationTypes) {
        final text = notificationText(
          l10n,
          _entry(type, const {
            'buyerName': 'Abebe',
            'farmerName': 'Chaltu',
            'truckerName': 'Dawit',
          }),
        );
        expect(text.title, isNot(l10n.notifGenericTitle), reason: type);
        expect(text.title.trim(), isNotEmpty, reason: type);
        expect(text.body.trim(), isNotEmpty, reason: type);
        expect(text.body, isNot(contains('{')), reason: type);
      }
    });

    test('$code: an unknown type reads as a generic order update', () {
      final text = notificationText(l10n, _entry('something_new'));
      expect(text.title, l10n.notifGenericTitle);
      expect(text.body, l10n.notifGenericBody);
    });

    test('$code: a missing name reads as the role', () {
      final text = notificationText(l10n, _entry('order_request'));
      expect(text.body, contains(l10n.buyer));
    });
  }

  // ESC-31 (notifications-api.md 1.5.0): the party's own reputation.
  group('reputation types', () {
    final en = lookupAppLocalizations(const Locale('en'));

    test('a penalty says what happened and how many points it added', () {
      final text = notificationText(
        en,
        _entry('penalty_recorded', {
          'eventId': 'CANCEL#ord-7f3a',
          'code': 'LATE_CANCEL',
          'points': '4',
          'activePoints': '7',
        }),
      );
      expect(text.title, 'Penalty points added');
      expect(
        text.body,
        'Cancelled less than a day before pickup. Points added: 4. Open My '
        'reputation to see the details or appeal.',
      );
    });

    test('a penalty with a code this build does not know stays neutral', () {
      final text = notificationText(
        en,
        _entry('penalty_recorded', {'code': 'SOMETHING_NEW', 'points': '2'}),
      );
      expect(text.body, startsWith('Order event. Points added: 2.'));
    });

    for (final (params, body) in <(Map<String, String>, String)>[
      (
        {'fromTier': 'good', 'toTier': 'warning'},
        'You have penalty points. Buyers see nothing yet. Open My '
            'reputation for the details.',
      ),
      (
        {'fromTier': 'warning', 'toTier': 'low'},
        'Buyers see a Low reputation label on your listings and trips, '
            'and are asked to confirm before they order.',
      ),
      (
        {
          'fromTier': 'low',
          'toTier': 'restricted',
          'restrictedUntil': '2026-11-21T09:00:00.000Z',
        },
        "You can't take new orders until Nov 21, 2026. Your current "
            'orders continue.',
      ),
      (
        // No end date: an admin review is open.
        {'fromTier': 'good', 'toTier': 'restricted'},
        "You can't take new orders while our team reviews a late "
            'delivery. Your current orders continue.',
      ),
      (
        {'fromTier': 'warning', 'toTier': 'good'},
        'You are back in good standing.',
      ),
    ]) {
      test('tier changed to ${params['toTier']}: "$body"', () {
        final text = notificationText(en, _entry('tier_changed', params));
        expect(text.title, 'Your reputation changed');
        expect(text.body, body);
      });
    }

    test('they are not order types and ask for no order action', () {
      expect(
        reputationNotificationTypes,
        {'penalty_recorded', 'tier_changed'},
      );
      expect(
        actionRequiredNotificationTypes
            .intersection(reputationNotificationTypes),
        isEmpty,
      );
    });
  });

  test('names come from the params', () {
    final en = lookupAppLocalizations(const Locale('en'));
    expect(
      notificationText(en, _entry('order_request', {'buyerName': 'Abebe T.'}))
          .body,
      startsWith('Abebe T. paid'),
    );
    expect(
      notificationText(
        en,
        _entry('delivery_scheduled', {'truckerName': 'Dawit'}),
      ).body,
      'Dawit will carry the order.',
    );
  });

  group('LT-07: truck_nearby', () {
    for (final locale in locales) {
      final l10n = lookupAppLocalizations(locale);
      final code = locale.languageCode;

      test('$code: its own text, naming the trucker, with no minutes', () {
        final text = notificationText(
          l10n,
          _entry('truck_nearby', {
            'truckerName': 'Dawit',
            'distanceKm': '3.2',
            'minutes': '25',
          }),
        );
        expect(text.title, l10n.notifTruckNearbyTitle);
        expect(text.title, isNot(l10n.notifGenericTitle));
        expect(text.body, l10n.notifTruckNearbyBody('Dawit'));
        expect(text.body, contains('Dawit'));
        // The entry stays in the history: no "in 25 minutes", no clock.
        expect(text.body, isNot(contains('25')));
        expect(text.body, isNot(matches(RegExp(r'\d{1,2}:\d{2}'))));
      });

      test('$code: a missing name reads as the role', () {
        final text = notificationText(l10n, _entry('truck_nearby'));
        expect(text.body, contains(l10n.trucker));
      });
    }

    test('English reads as a whole sentence', () {
      final en = lookupAppLocalizations(const Locale('en'));
      expect(
        notificationText(en, _entry('truck_nearby', {'truckerName': 'Dawit'}))
            .body,
        'Dawit is less than 5 km from you. '
        'Please be ready to receive the crops.',
      );
    });

    test('a delivery entry: the truck icon', () {
      expect(
        notificationStyle('truck_nearby').icon,
        notificationStyle('in_transit').icon,
      );
    });
  });

  test('a held payout says what is needed', () {
    final en = lookupAppLocalizations(const Locale('en'));
    expect(
      notificationText(
        en,
        _entry('payout_blocked', {'reason': 'fayda_required'}),
      ).body,
      contains(en.notifPayoutHintFayda),
    );
    expect(
      notificationText(en, _entry('payout_blocked')).body,
      contains(en.notifPayoutHintOther),
    );
  });

  test('a dispute decision names who gets the money', () {
    final en = lookupAppLocalizations(const Locale('en'));
    expect(
      notificationText(
        en,
        _entry('dispute_resolved', {'resolution': 'buyer_wins'}),
      ).body,
      en.notifDisputeResolvedBuyerWins,
    );
    expect(
      notificationText(en, _entry('dispute_resolved')).body,
      en.notifDisputeResolvedBody,
    );
  });

  group('detail line', () {
    final en = lookupAppLocalizations(const Locale('en'));
    final am = lookupAppLocalizations(const Locale('am'));
    final enClock = ClockFormat.resolve(en, ClockPreference.auto);
    final amClock = ClockFormat.resolve(am, ClockPreference.auto);
    final orderRequest = _entry('order_request', {
      'cropType': 'teff',
      'quantityKg': '500',
      'amountEtb': '42500.00',
      'deadlineAt': '2026-10-27T08:15:02Z',
    });

    test('crop, quantity, amount and, for an act type, when it is due', () {
      final line = notificationDetailLine(en, enClock, orderRequest)!;
      final text = line.toString();
      expect(text, contains('500 kg'));
      expect(text, contains('42,500.00 ETB'));
      // 08:15 UTC is 11:15 in Ethiopia, on the 24-hour clock in English.
      expect(line.time, 'Oct 27 11:15');
      expect(line.time24, isNull);
      expect(text, endsWith(en.actionDueBy('Oct 27 11:15')));
    });

    test('LT-03: Amharic shows the Ethiopian clock, the 24-hour time beside',
        () {
      final line = notificationDetailLine(am, amClock, orderRequest)!;
      expect(line.time, 'Oct 27 ከጠዋቱ 5:15');
      expect(line.time24, '11:15');
      expect(
        '${line.before}${line.time}${line.after}',
        endsWith(am.actionDueBy('Oct 27 ከጠዋቱ 5:15')),
      );
    });

    test('no deadline on a type that asks nothing', () {
      final line = notificationDetailLine(
        en,
        enClock,
        _entry('order_confirmed', {'deadlineAt': '2026-10-27T08:15:02Z'}),
      );
      expect(line, isNull);
    });

    test('facts alone when there is no deadline', () {
      final line = notificationDetailLine(
        en,
        enClock,
        _entry('order_confirmed', {'quantityKg': '500'}),
      )!;
      expect(line.toString(), en.quantityKg('500'));
      expect(line.time24, isNull);
    });

    test('FUP-338: the drop-off arrival shows T9 as its due time', () {
      final line = notificationDetailLine(
        en,
        enClock,
        _entry('delivery_arrived', {'deadlineAt': '2026-11-24T10:00:00Z'}),
      )!;
      expect(line.toString(), en.actionDueBy('Nov 24 13:00'));
    });
  });

  // FUP-286 and FUP-338 (notifications-api.md 1.6.0): the check-ins and the
  // judged outcomes, in the three languages.
  group('check-in types and judged outcomes', () {
    const names = {
      'buyerName': 'Abebe',
      'farmerName': 'Chaltu',
      'truckerName': 'Dawit',
    };

    test('each check-in type names the counterpart', () {
      final en = lookupAppLocalizations(const Locale('en'));
      String body(String type) =>
          notificationText(en, _entry(type, names)).body;

      expect(body('trucker_arrived'), en.notifTruckerArrivedBody('Dawit'));
      expect(body('buyer_arrived'), en.notifBuyerArrivedBody('Abebe'));
      expect(body('crops_ready'), en.notifCropsReadyBody('Chaltu'));
      expect(body('delivery_arrived'), en.notifDeliveryArrivedBody('Dawit'));
      for (final type in [
        'pickup_reminder',
        'delivery_arrived',
        'delivery_confirm_reminder',
      ]) {
        expect(actionRequiredNotificationTypes, contains(type));
      }
    });

    const judged = {
      'transport_failed': 'trucker_no_show',
      'transport_failed_keep_crops': 'trucker_no_show',
      'pickup_missed': 'trucker_no_show',
      'order_cancelled': 'farmer_no_show',
      'pickup_overdue': 'both_present_no_scan',
      'delivered': 'auto_confirmed',
      'delivered_payout_pending': 'auto_confirmed',
      'dispute_opened': 'silent_buyer',
    };

    for (final locale in locales) {
      final l10n = lookupAppLocalizations(locale);
      final code = locale.languageCode;

      test('$code: a judged outcome reads its own words', () {
        judged.forEach((type, reason) {
          final plain = notificationText(l10n, _entry(type, names));
          final told = notificationText(
            l10n,
            _entry(type, {...names, 'reason': reason}),
          );
          expect(told.body, isNot(plain.body), reason: '$type#$reason');
          expect(told.body.trim(), isNotEmpty, reason: '$type#$reason');
        });
      });
    }

    test('every judged reason has its words', () {
      final en = lookupAppLocalizations(const Locale('en'));
      String body(String type, String reason) =>
          notificationText(en, _entry(type, {'reason': reason})).body;

      expect(
        body('transport_failed', 'trucker_no_show'),
        en.notifTruckerNoShowBuyerBody,
      );
      expect(
        body('transport_failed_keep_crops', 'trucker_no_show'),
        en.notifTruckerNoShowFarmerBody,
      );
      expect(
        body('pickup_missed', 'trucker_no_show'),
        en.notifPickupCheckInMissedBody,
      );
      expect(
        body('order_cancelled', 'farmer_no_show'),
        en.notifFarmerNoShowBody,
      );
      expect(
        body('order_cancelled', 'buyer_no_show'),
        en.notifBuyerNoShowBody,
      );
      for (final reason in ['both_present_no_scan', 'unchecked_check_in']) {
        expect(body('pickup_overdue', reason), en.notifPickupReviewBody);
        expect(body('pickup_missed', reason), en.notifPickupReviewBody);
      }
      expect(
        body('delivered', 'auto_confirmed'),
        en.notifAutoConfirmedBuyerBody,
      );
      expect(
        body('delivered_payout_pending', 'auto_confirmed'),
        en.notifAutoConfirmedPayeeBody,
      );
      expect(
        body('dispute_opened', 'delivery_overdue'),
        en.notifDeliveryOverdueBody,
      );
      expect(
        body('dispute_opened', 'silent_buyer'),
        en.notifSilentBuyerBody,
      );
      expect(
        body('dispute_opened', 'dropoff_out_of_range'),
        en.notifDropoffOutOfRangeBody,
      );
      // Any other reason keeps the type's words.
      expect(
        body('transport_failed', 'trucker_declined'),
        en.notifTransportFailedBody,
      );
      expect(
        body('order_cancelled', 'buyer_withdrew'),
        en.notifOrderCancelledBody,
      );
    });
  });
}
