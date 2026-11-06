import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Money and quantities as the Fieldway boards write them.
void main() {
  group('formatEtbAmount', () {
    test('groups thousands and drops zero cents', () {
      expect(formatEtbAmount(68400), '68,400');
      expect(formatEtbAmount(15000.0), '15,000');
      expect(formatEtbAmount(92), '92');
    });

    test('keeps two decimals when the amount has cents', () {
      expect(formatEtbAmount(1234.5), '1,234.50');
      expect(formatEtbAmount(14625.25), '14,625.25');
    });
  });

  test('wholeNumber groups a quantity', () {
    expect(wholeNumber(1500), '1,500');
    expect(wholeNumber(800), '800');
  });

  group('values that cannot be real (SIM-33)', () {
    final en = lookupAppLocalizations(const Locale('en'));

    test('a negative, NaN or infinite amount is a dash', () {
      for (final amount in [-470.6, -1, double.nan, double.infinity]) {
        expect(isRealEtbAmount(amount), isFalse, reason: '$amount');
        expect(etbAmountText(amount, en), noValueMark, reason: '$amount');
      }
    });

    test('zero and real amounts are written with the unit', () {
      expect(etbAmountText(0, en), '0 ETB');
      expect(etbAmountText(68400, en), '68,400 ETB');
      expect(etbAmountText(1234.5, en), '1,234.50 ETB');
    });

    test('0 kg, a negative or NaN quantity and one past the cap are a dash',
        () {
      for (final kg in [0, -1, double.nan, double.infinity, maxOrderKg + 1]) {
        expect(isRealKgQuantity(kg), isFalse, reason: '$kg');
        expect(kgQuantityText(kg, en), noValueMark, reason: '$kg');
      }
    });

    test('a real quantity keeps a part of a kilo', () {
      expect(kgQuantityText(1500, en), '1,500 kg');
      expect(kgQuantityText(0.5, en), '0.5 kg');
      expect(kgQuantityText(maxOrderKg, en), '1,000,000 kg');
    });
  });
}
