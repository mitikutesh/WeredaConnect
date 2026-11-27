import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/place.dart';

void main() {
  // Shapes taken from the bundled asset (ethiopia_woredas.json).
  final meki = Place.fromJson(const {
    'id': 'gn-331163',
    'en': 'Mek’ī',
    'am': 'መቂ',
    'alts': ["Mek'i"],
  });
  final batu = Place.fromJson(const {
    'id': 'gn-325579',
    'en': 'Ziway',
    'display': 'Batu',
    'am': 'ባቱ',
    'om': 'Baatuu',
  });
  final alemTena =
      Place.fromJson(const {'id': 'gn-344267', 'en': '‘Alem T’ēna'});
  final addisRomanized = Place.fromJson(const {
    'id': 'gn-344979',
    'en': 'Addis Ababa',
    'am': 'Ādīs Ābeba', // romanized GeoNames "am" value, not Ge'ez
  });

  group('fromJson', () {
    test('reads the curated display name and defaults it to empty', () {
      expect(batu.display, 'Batu');
      expect(meki.display, isEmpty);
    });

    test('keeps id and the canonical en untouched', () {
      expect(meki.id, 'gn-331163');
      expect(meki.en, 'Mek’ī');
      expect(batu.en, 'Ziway');
    });
  });

  group('displayEn', () {
    test('drops transliteration marks and folds macrons', () {
      expect(meki.displayEn, 'Meki');
      expect(alemTena.displayEn, 'Alem Tena');
      expect(
        Place.fromJson(const {'id': 'x', 'en': 'K’ok’a'}).displayEn,
        'Koka',
      );
    });

    test('prefers the curated display name', () {
      expect(batu.displayEn, 'Batu');
    });
  });

  group('displayName', () {
    test('en shows the English display name', () {
      expect(meki.displayName('en'), 'Meki');
      expect(batu.displayName('en'), 'Batu');
    });

    test('am shows the Ge\'ez name, else the English display name', () {
      expect(meki.displayName('am'), 'መቂ');
      expect(batu.displayName('am'), 'ባቱ');
      expect(alemTena.displayName('am'), 'Alem Tena');
      // A romanized "am" value is not shown as Amharic.
      expect(addisRomanized.displayName('am'), 'Addis Ababa');
      expect(addisRomanized.amGeez, isEmpty);
    });

    test('om shows the Oromo name, else the English display name', () {
      expect(batu.displayName('om'), 'Baatuu');
      expect(meki.displayName('om'), 'Meki');
      expect(alemTena.displayName('om'), 'Alem Tena');
    });

    test('an unknown language falls back to English', () {
      expect(batu.displayName('fr'), 'Batu');
      expect(meki.displayName(''), 'Meki');
    });
  });
}
