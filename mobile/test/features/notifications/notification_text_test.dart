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

  test('the type list matches the server (33 types, no repeats)', () {
    expect(notificationTypes, hasLength(33));
    expect(notificationTypes.toSet(), hasLength(33));
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
  });
}
