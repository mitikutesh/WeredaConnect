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
}
