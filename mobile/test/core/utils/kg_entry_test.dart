import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/utils/kg_entry.dart';

/// Design canvas page M: a listing's quantity is typed and read in
/// kilograms, stored and sent in quintals (crop-listing-api.md). These are
/// the conversions at that edge.
void main() {
  group('quintalsFromKg', () {
    test('100 kg is one quintal', () {
      expect(quintalsFromKg(4000), 40);
      expect(quintalsFromKg(100), 1);
      expect(quintalsFromKg(1), 0.01);
    });

    test('keeps the four decimals the stock can carry, no float dust', () {
      expect(quintalsFromKg(1234.56), 12.3456);
      expect(quintalsFromKg(1250.5), 12.505);
      expect(quintalsFromKg(0.3 + 0.6), 0.009);
    });

    test('the limits are the server\'s 0.01 and 10,000 quintals', () {
      expect(quintalsFromKg(minListingKg), 0.01);
      expect(quintalsFromKg(maxListingKg), 10000);
    });
  });

  group('kgFromQuintals and kgEntryText', () {
    test('quintals read as kilograms', () {
      expect(kgFromQuintals(40), 4000);
      expect(kgFromQuintals(12.3456), 1234.56);
      expect(kgFromQuintals(0.01), 1);
    });

    test('the field shows plain kilograms without trailing zeros', () {
      expect(kgEntryText(40), '4000');
      expect(kgEntryText(12.345), '1234.5');
      expect(kgEntryText(12.3456), '1234.56');
      expect(kgEntryText(0.5), '50');
      expect(kgEntryText(10), '1000');
    });

    test('what the field shows converts back to the same quintals', () {
      for (final quintals in [0.01, 0.5, 8.0, 12.3456, 28.0, 9999.99]) {
        expect(
          quintalsFromKg(parseKgEntry(kgEntryText(quintals))!),
          quintals,
          reason: '$quintals quintals',
        );
      }
    });
  });

  group('parseKgEntry', () {
    test('reads a typed number, grouping ignored', () {
      expect(parseKgEntry('4000'), 4000);
      expect(parseKgEntry('4,000'), 4000);
      expect(parseKgEntry(' 12.5 '), 12.5);
    });

    test('nothing typed or not a number is null', () {
      expect(parseKgEntry(null), isNull);
      expect(parseKgEntry(''), isNull);
      expect(parseKgEntry('abc'), isNull);
      expect(parseKgEntry('.'), isNull);
    });
  });

  group('kgEntryProblem', () {
    test('a real quantity has none', () {
      expect(kgEntryProblem('1'), isNull);
      expect(kgEntryProblem('4000'), isNull);
      expect(kgEntryProblem('1000000'), isNull);
    });

    test('nothing, zero, too small and too large are told apart', () {
      expect(kgEntryProblem(''), KgEntryProblem.missing);
      expect(kgEntryProblem('0'), KgEntryProblem.missing);
      expect(kgEntryProblem('0.5'), KgEntryProblem.tooSmall);
      expect(kgEntryProblem('1000000.01'), KgEntryProblem.tooLarge);
    });
  });

  group('KgEntryFormatter', () {
    const formatter = KgEntryFormatter();
    TextEditingValue typed(String text, {String before = ''}) =>
        formatter.formatEditUpdate(
          TextEditingValue(text: before),
          TextEditingValue(
            text: text,
            selection: TextSelection.collapsed(offset: text.length),
          ),
        );

    test('plain kilograms pass as typed', () {
      expect(typed('4000').text, '4000');
      expect(typed('1234.56').text, '1234.56');
      expect(typed('1000000').text, '1000000');
      expect(typed('0.5').text, '0.5');
      expect(typed('').text, '');
    });

    test('a comma or a space is grouping and is taken out, digits stay', () {
      expect(typed('4,000').text, '4000');
      expect(typed('1,000,000').text, '1000000');
      expect(typed('4 000').text, '4000');
      expect(typed('1,234.56').text, '1234.56');
      // Also before one or two last digits: never a decimal mark.
      expect(typed('12,5').text, '125');
      expect(typed('12,50').text, '1250');
      // What the field holds is what the form reads.
      expect(parseKgEntry(typed('4,000').text), 4000);
    });

    test('the caret stays after the characters that were kept', () {
      final value = typed('4,000');
      expect(value.selection, const TextSelection.collapsed(offset: 4));
      final mid = formatter.formatEditUpdate(
        TextEditingValue.empty,
        const TextEditingValue(
          text: '1,234',
          selection: TextSelection.collapsed(offset: 3),
        ),
      );
      expect(mid.text, '1234');
      expect(mid.selection, const TextSelection.collapsed(offset: 2));
    });

    test('what does not fit is refused whole: no digit is dropped', () {
      // An eighth digit, a third decimal, a letter, a second point.
      expect(typed('12345678', before: '1234567').text, '1234567');
      expect(typed('1.234', before: '1.23').text, '1.23');
      expect(typed('12a', before: '12').text, '12');
      expect(typed('1.2.3', before: '1.2').text, '1.2');
      // A paste that does not fit leaves the field as it was.
      expect(typed('4,000.555', before: '7').text, '7');
    });
  });

  test('kgNumberText groups thousands and keeps up to two decimals', () {
    expect(kgNumberText(3200), '3,200');
    expect(kgNumberText(800), '800');
    expect(kgNumberText(1234.5), '1,234.5');
  });
}
