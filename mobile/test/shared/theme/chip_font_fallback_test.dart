import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';

void main() {
  group('chip labels carry the Ethiopic font fallback (SIM-22)', () {
    for (final entry
        in {'light': AppTheme.light(), 'dark': AppTheme.dark()}.entries) {
      test('${entry.key} theme', () {
        final chip = entry.value.chipTheme;
        for (final style in [chip.labelStyle, chip.secondaryLabelStyle]) {
          expect(style, isNotNull);
          expect(style!.fontFamily, AppTheme.fontFamily);
          expect(style.fontFamilyFallback, contains('NotoSansEthiopic'));
        }
        // Same fallback as the body text, so chips and text render alike.
        expect(
          entry.value.textTheme.bodyMedium?.fontFamilyFallback,
          contains('NotoSansEthiopic'),
        );
      });
    }
  });
}
