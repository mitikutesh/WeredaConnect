import 'dart:convert';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/app_notification.dart';
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

  test('the type list matches the server (32 types, no repeats)', () {
    expect(notificationTypes, hasLength(32));
    expect(notificationTypes.toSet(), hasLength(32));
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

    test('crop, quantity, amount and, for an act type, when it is due', () {
      final line = notificationDetailLine(
        en,
        _entry('order_request', {
          'cropType': 'teff',
          'quantityKg': '500',
          'amountEtb': '42500.00',
          'deadlineAt': '2026-10-27T08:15:02Z',
        }),
      )!;
      expect(line, contains('500 kg'));
      expect(line, contains('42,500.00 ETB'));
      // 08:15 UTC is 11:15 in Ethiopia.
      final due = formatEthiopiaDeadline(DateTime.utc(2026, 10, 27, 8, 15, 2));
      expect(due, startsWith('Oct 27 11:15'));
      expect(line, contains(en.actionDueBy(due)));
    });

    test('no deadline on a type that asks nothing', () {
      final line = notificationDetailLine(
        en,
        _entry('order_confirmed', {'deadlineAt': '2026-10-27T08:15:02Z'}),
      );
      expect(line, isNull);
    });
  });
}
