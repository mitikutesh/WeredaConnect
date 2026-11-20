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

  group('quintals after the kilograms (design canvas page M)', () {
    final en = lookupAppLocalizations(const Locale('en'));

    test('whole and part quintals, grouped', () {
      expect(quintalsInParensText(2800, en), '(28 quintals)');
      expect(quintalsInParensText(50, en), '(0.5 quintals)');
      // Exactly one: the singular.
      expect(quintalsInParensText(100, en), '(1 quintal)');
      expect(quintalsInParensText(250000, en), '(2,500 quintals)');
    });

    test('a quintal figure beside its kilograms keeps two decimals', () {
      // 125 kg is 1.25 quintals, never "1.3"; 4 kg is 0.04, never "0.0".
      expect(quintalsNumberText(1.25), '1.25');
      expect(quintalsNumberText(0.04), '0.04');
      expect(quintalsNumberText(50), '50');
      expect(quintalsNumberText(2500), '2,500');
      // Float dust from a sum of stocks does not show.
      expect(quintalsNumberText(0.1 + 0.2), '0.3');
    });

    test('stored quintals in parentheses: singular for exactly one', () {
      expect(quintalsCountInParens(1, en), '(1 quintal)');
      expect(quintalsCountInParens(1.25, en), '(1.25 quintals)');
      expect(quintalsCountInParens(0.04, en), '(0.04 quintals)');
      expect(quintalsCountInParens(0, en), '(0 quintals)');
      expect(quintalsCountInParens(60, en), '(60 quintals)');
    });

    test('part of total quintals', () {
      expect(quintalsOfInParensText(1.25, 50, en), '(1.25 of 50 quintals)');
      expect(quintalsOfInParensText(32, 60, en), '(32 of 60 quintals)');
    });

    test('a full trip has "0 kg" of room, not the no-value mark', () {
      expect(kgRoomText(0, en), '0 kg');
      expect(kgRoomText(-5, en), '0 kg');
      expect(kgRoomText(1800, en), '1,800 kg');
      expect(kgRoomText(double.nan, en), noValueMark);
    });

    test('nothing trails a quantity that cannot be real', () {
      for (final kg in [0, -1, double.nan, double.infinity, maxOrderKg + 1]) {
        expect(quintalsInParensText(kg, en), isEmpty, reason: '$kg');
      }
    });
  });
}
