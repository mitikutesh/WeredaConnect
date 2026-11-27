import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

const _lockedRefusal = 'ORDER_DELIVERY_LOCATION_LOCKED: The drop-off place can '
    'only be changed before pickup.';

/// LT-14: the refusal of a drop-off place change after pickup
/// (`ORDER_DELIVERY_LOCATION_LOCKED`) has its own text on web and on the
/// mobile queue, in en, am and om; the note's refusal keeps its own.
void main() {
  test('the prefix is recognised, and only it', () {
    expect(isDeliveryLocationLockedMessage(_lockedRefusal), isTrue);
    expect(
      isDeliveryLocationLockedMessage(
        'ORDER_DROPOFF_NOTE_LOCKED: The drop-off note can only be changed '
        'before pickup.',
      ),
      isFalse,
    );
    expect(isDeliveryLocationLockedMessage(null), isFalse);
  });

  test('web: mapErrorToCode', () {
    expect(
      mapErrorToCode(const SyncRejectedException(_lockedRefusal)),
      AppErrorCode.deliveryLocationLocked,
    );
  });

  test('mobile queue: syncRejectionErrorCode', () {
    expect(
      syncRejectionErrorCode(
        entityType: 'order',
        action: 'update',
        message: _lockedRefusal,
      ),
      AppErrorCode.deliveryLocationLocked,
    );
  });

  group('localizeErrorCode', () {
    const expected = {
      'en': 'The goods were picked up, so the drop-off place can no longer be '
          'changed.',
      'am': 'ዕቃው ስለተነሳ የማውረጃ ቦታውን ከእንግዲህ መቀየር አይቻልም።',
      'om': "Meeshaan waan fe'ameef bakka buusii jijjiiruun hin danda'amu.",
    };
    for (final MapEntry(key: locale, value: text) in expected.entries) {
      test(locale, () async {
        final l10n = await AppLocalizations.delegate.load(Locale(locale));
        expect(
          localizeErrorCode(AppErrorCode.deliveryLocationLocked, l10n),
          text,
        );
      });
    }
  });
}
