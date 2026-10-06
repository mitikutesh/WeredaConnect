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

  // Golden vectors shared with the server (WoredaNamesTests.cs). Keep both
  // lists identical so mobile and server woreda matching never drift.
  group('normalizeWoreda', () {
    test('case-folds and trims', () {
      expect(normalizeWoreda('Adama'), 'adama');
      expect(normalizeWoreda('  ADAMA  '), 'adama');
      expect(normalizeWoreda('Addis   Ababa'), 'addis ababa');
    });

    test('folds Latin diacritics (picker spelling)', () {
      expect(normalizeWoreda('Ādama'), 'adama');
      expect(normalizeWoreda('Dirē Dawa'), 'dire dawa');
    });

    test('cuts a ", Region" / "(Region)" / "[...]" suffix', () {
      expect(normalizeWoreda('Adama, Oromia'), 'adama');
      expect(normalizeWoreda('Adama,Oromia'), 'adama');
      expect(normalizeWoreda('Addis Ababa (Finfinne)'), 'addis ababa');
      expect(normalizeWoreda('Dirē Dawa [Dire Dawa]'), 'dire dawa');
      expect(normalizeWoreda('አዳማ፣ ኦሮሚያ'), 'አዳማ');
    });

    test('drops apostrophes and treats hyphens as spaces', () {
      expect(normalizeWoreda('Debre Mark’os'), 'debre markos');
      expect(normalizeWoreda("Debre Mark'os"), 'debre markos');
      expect(normalizeWoreda('Bahir-Dar'), 'bahir dar');
    });

    test('leaves Ge\'ez untouched', () {
      expect(normalizeWoreda('Bishoftu ቦታ'), 'bishoftu ቦታ');
    });

    test('blank or suffix-only input is empty', () {
      expect(normalizeWoreda(''), '');
      expect(normalizeWoreda('   '), '');
      expect(normalizeWoreda(', Oromia'), '');
    });
  });

  group('compareWoredas', () {
    void expectSymmetric(String a, String b, WoredaMatch expected) {
      expect(compareWoredas(a, b), expected, reason: '$a vs $b');
      expect(compareWoredas(b, a), expected, reason: '$b vs $a');
    }

    test('spelling variants of one place are exact', () {
      expectSymmetric('Adama, Oromia', 'Ādama', WoredaMatch.exact);
      expectSymmetric('adama', 'ADAMA', WoredaMatch.exact);
      expectSymmetric(
        'Addis Ababa',
        'addis-ababa, Addis Ababa',
        WoredaMatch.exact,
      );
    });

    test('whole-word overlap is partial', () {
      expectSymmetric('Adama', 'Adama Zone', WoredaMatch.partial);
      expectSymmetric('Wolaita Sodo', 'Sodo', WoredaMatch.partial);
    });

    test('raw substrings and different places are none', () {
      expectSymmetric('Ambo', 'Gambo', WoredaMatch.none);
      expectSymmetric('Adama', 'Mekelle', WoredaMatch.none);
      expectSymmetric('Addis Ababa', 'Addis Abeba', WoredaMatch.none);
    });

    test('a blank side is never a match', () {
      expectSymmetric('', 'Adama', WoredaMatch.none);
      expectSymmetric('   ', 'Adama', WoredaMatch.none);
    });

    test('compareWoredaKeys matches compareWoredas on normalised keys', () {
      expect(
        compareWoredaKeys(normalizeWoreda('Adama, Oromia'), 'adama'),
        WoredaMatch.exact,
      );
      expect(compareWoredaKeys('', 'adama'), WoredaMatch.none);
    });
  });
}
