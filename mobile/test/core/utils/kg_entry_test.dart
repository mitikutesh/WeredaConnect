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

  test('kgNumberText groups thousands and keeps up to two decimals', () {
    expect(kgNumberText(3200), '3,200');
    expect(kgNumberText(800), '800');
    expect(kgNumberText(1234.5), '1,234.5');
  });
}
