import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/reputation/domain/own_rating.dart';

import 'reputation_fixtures.dart';

class _MockRatingRepository extends Mock implements BaseRatingRepository {}

/// ESC-31: the signed-in user's own rating for Profile. Role visibility:
/// only the caller's own id is ever asked about, and a buyer asks nothing.
void main() {
  late MockReputationRepository reputation;
  late _MockRatingRepository ratings;
  final now = DateTime.utc(2026, 11, 7, 9);

  setUp(() {
    reputation = MockReputationRepository();
    ratings = _MockRatingRepository();
    when(() => reputation.readOwnRatingSummary(any()))
        .thenAnswer((_) async => null);
    when(
      () => reputation.saveOwnRatingSummary(
        any(),
        average: any(named: 'average'),
        count: any(named: 'count'),
      ),
    ).thenAnswer((_) async {});
  });

  Future<OwnRating?> load(UserRole role) => loadOwnRating(
        userId: 'u-1',
        role: role,
        reputation: reputation,
        ratings: ratings,
        now: () => now,
      );

  void unrated({bool stale = false, DateTime? fetchedAt}) =>
      when(() => reputation.loadOwn('u-1')).thenAnswer(
        (_) async => snapshot(
          ownReputation(rated: false),
          stale: stale,
          fetchedAt: fetchedAt ?? now,
        ),
      );

  test('a buyer makes no call and has nothing to show', () async {
    // Buyers are never rated: no "None yet" that promises a rating.
    expect(await load(UserRole.buyer), isNull);
    verifyZeroInteractions(reputation);
    verifyZeroInteractions(ratings);
  });

  for (final role in [UserRole.farmer, UserRole.trucker]) {
    group(role.name, () {
      test('the raw average from the own reputation, no rating call', () async {
        when(() => reputation.loadOwn('u-1')).thenAnswer(
          (_) async => snapshot(ownReputation(deduction: 0.4, points: 4)),
        );

        final rating = await load(role);

        expect(rating!.average, 4.8);
        expect(rating.count, 12);
        expect(rating.penaltiesEnabled, isTrue);
        verifyZeroInteractions(ratings);
      });

      // The server sends count 0 for a rated user whose row the rating
      // backfill has not reached: the ratings themselves know better.
      test('reputation says 0, ratings say 12: the 12 are shown', () async {
        unrated();
        when(() => ratings.getRatingSummary('u-1')).thenAnswer(
          (_) async => const RatingSummary(average: 4.6, count: 12),
        );

        final rating = await load(role);

        expect(rating!.hasRating, isTrue);
        expect(rating.average, 4.6);
        expect(rating.count, 12);
        // Kept from the reputation answer.
        expect(rating.penaltiesEnabled, isTrue);
        verify(
          () => reputation.saveOwnRatingSummary('u-1', average: 4.6, count: 12),
        ).called(1);
      });

      test('none yet only when the ratings also say 0', () async {
        unrated();
        when(() => ratings.getRatingSummary('u-1'))
            .thenAnswer((_) async => RatingSummary.empty);

        final rating = await load(role);

        expect(rating, isNotNull);
        expect(rating!.known, isTrue);
        expect(rating.hasRating, isFalse);
        verify(() => ratings.getRatingSummary('u-1')).called(1);
      });

      test('the rating read is asked at most every 30 minutes', () async {
        unrated(fetchedAt: now.subtract(const Duration(minutes: 20)));
        when(() => reputation.readOwnRatingSummary('u-1')).thenAnswer(
          (_) async => (
            average: 4.6,
            count: 12,
            fetchedAt: now.subtract(const Duration(minutes: 20)),
          ),
        );

        final rating = await load(role);

        expect(rating!.average, 4.6);
        expect(rating.count, 12);
        verifyZeroInteractions(ratings);
      });

      test('an older saved answer is asked again', () async {
        unrated();
        when(() => reputation.readOwnRatingSummary('u-1')).thenAnswer(
          (_) async => (
            average: 4.6,
            count: 12,
            fetchedAt: now.subtract(const Duration(minutes: 31)),
          ),
        );
        when(() => ratings.getRatingSummary('u-1')).thenAnswer(
          (_) async => const RatingSummary(average: 4.5, count: 13),
        );

        final rating = await load(role);

        expect(rating!.average, 4.5);
        expect(rating.count, 13);
      });

      test('offline with a saved reputation: the last known rating, no call',
          () async {
        unrated(stale: true, fetchedAt: now.subtract(const Duration(hours: 5)));
        when(() => reputation.readOwnRatingSummary('u-1')).thenAnswer(
          (_) async => (
            average: 4.6,
            count: 12,
            fetchedAt: now.subtract(const Duration(hours: 5)),
          ),
        );

        final rating = await load(role);

        expect(rating!.average, 4.6);
        verifyZeroInteractions(ratings);
      });

      test(
          'an empty rating answer that may be a failed request is unknown, '
          'never "none"', () async {
        // The reputation answer is the saved one (10 minutes old), so an
        // empty rating answer now proves nothing: the rating repositories
        // answer empty for a failed request too.
        unrated(fetchedAt: now.subtract(const Duration(minutes: 10)));
        when(() => ratings.getRatingSummary('u-1'))
            .thenAnswer((_) async => RatingSummary.empty);

        final rating = await load(role);

        expect(rating!.known, isFalse);
        expect(rating.hasRating, isFalse);
        expect(rating.penaltiesEnabled, isTrue);
        verifyNever(
          () => reputation.saveOwnRatingSummary(
            any(),
            average: any(named: 'average'),
            count: any(named: 'count'),
          ),
        );
      });

      test('reputation unreadable: the own rating aggregate alone', () async {
        when(() => reputation.loadOwn('u-1'))
            .thenThrow(const NetworkException());
        when(() => ratings.getRatingSummary('u-1')).thenAnswer(
          (_) async => const RatingSummary(average: 4.6, count: 5),
        );

        final rating = await load(role);

        expect(rating!.average, 4.6);
        expect(rating.count, 5);
        expect(rating.penaltiesEnabled, isFalse);
      });

      test('nothing readable is unknown (null)', () async {
        when(() => reputation.loadOwn('u-1'))
            .thenThrow(const NetworkException());
        when(() => ratings.getRatingSummary('u-1'))
            .thenAnswer((_) async => RatingSummary.empty);

        expect(await load(role), isNull);
        expect(await loadOwnRating(userId: 'u-1', role: role), isNull);
      });
    });
  }
}
