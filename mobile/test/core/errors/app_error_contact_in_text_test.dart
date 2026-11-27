import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// FUP-49: a crop listing or trip refused because its text holds a phone
/// number is explained in all three languages, on web (direct /sync) and in
/// the mobile queue's rejection snackbar. LT-12 adds the order's drop-off
/// note (`ORDER_CONTACT_IN_TEXT`) and its edit window
/// (`ORDER_DROPOFF_NOTE_LOCKED`).
void main() {
  const cropRefusal = 'CROP_CONTACT_IN_TEXT: Remove the phone number from '
      'description. Contact opens after payment is held in escrow.';
  const tripRefusal = 'TRIP_CONTACT_IN_TEXT: Remove the phone number from '
      'vehicleDescription. Contact opens after payment is held in escrow.';
  const orderRefusal = 'ORDER_CONTACT_IN_TEXT: Remove the phone number from '
      'dropoffNote. Contact opens after payment is held in escrow.';
  const lockedRefusal = 'ORDER_DROPOFF_NOTE_LOCKED: The drop-off note can '
      'only be changed before pickup.';

  test('isContactInTextMessage matches only the three codes', () {
    expect(isContactInTextMessage(cropRefusal), isTrue);
    expect(isContactInTextMessage(tripRefusal), isTrue);
    expect(isContactInTextMessage(orderRefusal), isTrue);
    expect(isContactInTextMessage(lockedRefusal), isFalse);
    expect(
      isContactInTextMessage('CROP_VALIDATION: Invalid cropType.'),
      isFalse,
    );
    expect(isContactInTextMessage('MESSAGE_CONTACT_LOCKED: closed'), isFalse);
    expect(isContactInTextMessage(null), isFalse);
  });

  test('the order codes stay permanent order refusals (ORDER_*)', () {
    expect(isRevertableRejectionMessage(orderRefusal), isTrue);
    expect(isRevertableRejectionMessage(lockedRefusal), isTrue);
    expect(isDropoffNoteLockedMessage(lockedRefusal), isTrue);
    expect(isDropoffNoteLockedMessage(orderRefusal), isFalse);
  });

  group('mapErrorToCode (web create through /sync)', () {
    for (final message in [cropRefusal, tripRefusal, orderRefusal]) {
      test(message.split(':').first, () {
        expect(
          mapErrorToCode(SyncRejectedException(message)),
          AppErrorCode.contactInText,
        );
      });
    }

    test('a drop-off note change after pickup has its own text', () {
      expect(
        mapErrorToCode(const SyncRejectedException(lockedRefusal)),
        AppErrorCode.dropoffNoteLocked,
      );
    });

    test('other order refusals keep the order text', () {
      expect(
        mapErrorToCode(
          const SyncRejectedException('ORDER_VALIDATION: Unknown fields: x'),
        ),
        AppErrorCode.orderRefused,
      );
    });

    test('other crop and trip refusals keep the generic text', () {
      expect(
        mapErrorToCode(
          const SyncRejectedException('TRIP_VALIDATION: capacity is required.'),
        ),
        AppErrorCode.server,
      );
    });
  });

  group('syncRejectionErrorCode (mobile queue)', () {
    test('a refused crop listing create', () {
      expect(
        syncRejectionErrorCode(
          entityType: 'crop_listing',
          action: 'create',
          message: cropRefusal,
        ),
        AppErrorCode.contactInText,
      );
    });

    test('a refused trip update', () {
      expect(
        syncRejectionErrorCode(
          entityType: 'trip',
          action: 'update',
          message: tripRefusal,
        ),
        AppErrorCode.contactInText,
      );
    });

    test('a refused drop-off note change', () {
      expect(
        syncRejectionErrorCode(
          entityType: 'order',
          action: 'update',
          message: orderRefusal,
        ),
        AppErrorCode.contactInText,
      );
      expect(
        syncRejectionErrorCode(
          entityType: 'order',
          action: 'update',
          message: lockedRefusal,
        ),
        AppErrorCode.dropoffNoteLocked,
      );
    });

    test('a refused order create still says the order was not placed', () {
      expect(
        syncRejectionErrorCode(
          entityType: 'order',
          action: 'create',
          message: orderRefusal,
        ),
        AppErrorCode.orderNotPlaced,
      );
    });
  });

  group('localizeErrorCode', () {
    const expected = {
      'en': 'Phone numbers are not allowed here. Remove the number: contact '
          'opens after payment is held in escrow.',
      'am': 'እዚህ ስልክ ቁጥር አይፈቀድም። ቁጥሩን ያስወግዱ፤ ክፍያው በኢስክሮ ከተያዘ በኋላ '
          'መገናኘት ይቻላል።',
      'om': 'Lakkoofsi bilbilaa asitti hin hayyamamu. Lakkoofsa kana haqi; '
          'quunnamtiin kan banamu erga kaffaltiin Eeskiroo keessatti qabamee '
          'booda.',
    };
    for (final MapEntry(key: locale, value: text) in expected.entries) {
      test(locale, () async {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        expect(localizeErrorCode(AppErrorCode.contactInText, l10n), text);
      });
    }
  });

  group('localizeErrorCode, the drop-off note after pickup', () {
    const expected = {
      'en': 'The goods were picked up, so the drop-off landmark can no longer '
          'be changed.',
      'am': 'ዕቃው ስለተነሳ የማውረጃ ምልክቱን ከእንግዲህ መቀየር አይቻልም።',
      'om': "Meeshaan waan fe'ameef mallattoo bakka buusii jijjiiruun hin "
          "danda'amu.",
    };
    for (final MapEntry(key: locale, value: text) in expected.entries) {
      test(locale, () async {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        expect(localizeErrorCode(AppErrorCode.dropoffNoteLocked, l10n), text);
      });
    }
  });
}
