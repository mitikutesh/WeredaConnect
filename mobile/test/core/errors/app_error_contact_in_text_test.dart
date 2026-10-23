import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// FUP-49: a crop listing or trip refused because its text holds a phone
/// number is explained in all three languages, on web (direct /sync) and in
/// the mobile queue's rejection snackbar.
void main() {
  const cropRefusal = 'CROP_CONTACT_IN_TEXT: Remove the phone number from '
      'description. Contact opens after payment is held in escrow.';
  const tripRefusal = 'TRIP_CONTACT_IN_TEXT: Remove the phone number from '
      'vehicleDescription. Contact opens after payment is held in escrow.';

  test('isContactInTextMessage matches only the two codes', () {
    expect(isContactInTextMessage(cropRefusal), isTrue);
    expect(isContactInTextMessage(tripRefusal), isTrue);
    expect(
      isContactInTextMessage('CROP_VALIDATION: Invalid cropType.'),
      isFalse,
    );
    expect(isContactInTextMessage('MESSAGE_CONTACT_LOCKED: closed'), isFalse);
    expect(isContactInTextMessage(null), isFalse);
  });

  group('mapErrorToCode (web create through /sync)', () {
    for (final message in [cropRefusal, tripRefusal]) {
      test(message.split(':').first, () {
        expect(
          mapErrorToCode(SyncRejectedException(message)),
          AppErrorCode.contactInText,
        );
      });
    }

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
}
