import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

void main() {
  late PlaceRepository repo;

  setUp(() {
    final json = File('test/fixtures/woredas_sample.json').readAsStringSync();
    repo = PlaceRepository()..loadFromStringForTest(json);
  });

  group('search', () {
    test('returns empty list for empty query', () {
      expect(repo.search(''), isEmpty);
      expect(repo.search('   '), isEmpty);
    });

    test('ranks prefix matches above substring matches', () {
      // "Robe" (en) is a prefix match; "Goba" matches only via its alt
      // "Goba Robe" (substring). Goba has a far larger population, so this also
      // proves prefix beats substring regardless of the population tiebreaker.
      final results = repo.search('robe');
      expect(results.first.id, 'gn-robe');
      expect(results.map((p) => p.id), contains('gn-goba'));
      expect(
        results.indexWhere((p) => p.id == 'gn-robe'),
        lessThan(results.indexWhere((p) => p.id == 'gn-goba')),
      );
    });

    test('ranks more-populous places first within prefix matches', () {
      final results = repo.search('a'); // Addis Ababa, Adama, Assosa
      expect(results.first.id, 'gn-addis');
      expect(results[1].id, 'gn-adama');
    });

    test('matches Amharic (Geʼez) names directly', () {
      final results = repo.search('ጋምቤላ');
      expect(results.map((p) => p.id), contains('gn-gambela'));
    });

    test('matches Afaan Oromo names', () {
      final results = repo.search('Bishooftuu');
      expect(results.map((p) => p.id), contains('gn-bishoftu'));
    });

    test('matches alternate spellings', () {
      expect(repo.search('Nazret').map((p) => p.id), contains('gn-adama'));
      expect(
        repo.search('Debre Zeit').map((p) => p.id),
        contains('gn-bishoftu'),
      );
    });

    test('matches accented Latin alternates after folding', () {
      // Query "adis abeba" should match the accented alt "Ādīs Ābeba".
      expect(repo.search('adis abeba').map((p) => p.id), contains('gn-addis'));
    });

    test('caps results at the given limit', () {
      expect(repo.search('a', limit: 2), hasLength(2));
    });

    test('matches the cleaned name and the old marked spellings', () {
      for (final query in ['Meki', 'meki', 'Mek’ī', "Mek'i"]) {
        expect(repo.search(query).first.id, 'gn-meki', reason: query);
      }
    });

    test('matches the curated display name and the old name', () {
      expect(repo.search('Batu').first.id, 'gn-ziway');
      expect(repo.search('Ziway').first.id, 'gn-ziway');
      expect(repo.search('Baatuu').first.id, 'gn-ziway');
    });
  });

  group('findByDisplayName', () {
    test('resolves an exact name (case/space-insensitive)', () {
      expect(repo.findByDisplayName('Addis Ababa')?.id, 'gn-addis');
      expect(repo.findByDisplayName('  addis   ababa ')?.id, 'gn-addis');
    });

    test('resolves the most-populous place when a name is shared', () {
      // "Bure" exists in Amhara (20000) and Oromia (5000).
      expect(repo.findByDisplayName('Bure')?.id, 'gn-bure-amhara');
    });

    test('resolves the cleaned and the curated display names', () {
      expect(repo.findByDisplayName('Meki')?.id, 'gn-meki');
      expect(repo.findByDisplayName('Batu')?.id, 'gn-ziway');
    });

    test('returns null for an off-list value', () {
      expect(repo.findByDisplayName('Nowhereville'), isNull);
      expect(repo.findByDisplayName(''), isNull);
    });
  });

  group('byId', () {
    test('returns the matching place', () {
      expect(repo.byId('gn-adama')?.en, 'Adama');
    });

    test('returns null for an unknown id', () {
      expect(repo.byId('does-not-exist'), isNull);
    });
  });

  // The real bundled asset, regenerated with the overrides merged
  // (scripts/build_ethiopia_places.py --apply-overrides).
  group('bundled asset', () {
    late PlaceRepository bundled;

    setUpAll(() {
      final json = File('assets/data/ethiopia_woredas.json').readAsStringSync();
      bundled = PlaceRepository()..loadFromStringForTest(json);
    });

    test('loads every place', () {
      expect(bundled.isLoaded, isTrue);
      expect(bundled.byId('gn-330186')?.en, 'Nazrēt'); // en is never changed
      expect(bundled.search('a', limit: 1000).length, greaterThan(100));
    });

    test('finds the right town for the current and old names', () {
      const expected = {
        'Adama': 'gn-330186', // Oromia city, not the Amhara village "Ādama"
        'Nazret': 'gn-330186',
        'Batu': 'gn-325579',
        'Ziway': 'gn-325579',
        'Debre Zeyit': 'gn-339666', // Bishoftu, not the Benishangul village
        'Debre Zeit': 'gn-339666',
        'Bishoftu': 'gn-339666',
        'Meki': 'gn-331163',
        'Mek’ī': 'gn-331163',
        'Hawassa': 'gn-343137',
        'Awasa': 'gn-343137',
        'Ādīs Ābeba': 'gn-344979',
        'Finfinnee': 'gn-344979',
        'አዳማ': 'gn-330186',
      };
      expected.forEach((query, id) {
        expect(bundled.search(query).first.id, id, reason: query);
      });
    });

    test('shows display names in en, am and om', () {
      final adama = bundled.byId('gn-330186')!;
      expect(adama.displayName('en'), 'Adama');
      expect(adama.displayName('am'), 'አዳማ');
      expect(adama.displayName('om'), 'Adaamaa');

      final meki = bundled.byId('gn-331163')!;
      expect(meki.displayName('en'), 'Meki');
      expect(meki.displayName('am'), 'መቂ');
      expect(meki.displayName('om'), 'Meki'); // no Oromo name curated yet

      final alemTena = bundled.byId('gn-344267')!;
      expect(alemTena.displayName('en'), 'Alem Tena');
      expect(bundled.byId('gn-332917')!.displayName('en'), 'Koka');
    });
  });
}
