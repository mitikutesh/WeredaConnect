import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/utils/etb_format.dart';

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
}
