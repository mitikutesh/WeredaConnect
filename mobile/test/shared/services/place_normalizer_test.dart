import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';

void main() {
  group('normalizeForSearch', () {
    test('lowercases Latin input', () {
      expect(normalizeForSearch('ASSOSA'), 'assosa');
      expect(normalizeForSearch('Bishoftu'), 'bishoftu');
    });

    test('folds common Latin accents/diacritics to ASCII', () {
      expect(normalizeForSearch('Ādīs Ābeba'), 'adis abeba');
      expect(normalizeForSearch('Dirē Dawa'), 'dire dawa');
      expect(normalizeForSearch('Café'), 'cafe');
      expect(normalizeForSearch('Gambëla'), 'gambela');
    });

    test('leaves Geʼez (Amharic) characters untouched', () {
      expect(normalizeForSearch('ጋምቤላ'), 'ጋምቤላ');
      expect(normalizeForSearch('አዲስ አበባ'), 'አዲስ አበባ');
    });

    test('normalizes only the Latin half of a mixed-script string', () {
      expect(normalizeForSearch('Bishoftu ቦታ'), 'bishoftu ቦታ');
    });

    test('collapses and trims whitespace', () {
      expect(normalizeForSearch('  Addis   Ababa '), 'addis ababa');
    });

    test('returns empty string for empty/whitespace input', () {
      expect(normalizeForSearch(''), '');
      expect(normalizeForSearch('   '), '');
    });
  });
}
