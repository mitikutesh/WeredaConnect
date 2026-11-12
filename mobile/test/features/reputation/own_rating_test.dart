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
///
/// "None yet" only when the server really answered count 0; whenever the
/// app cannot be sure, unknown (the tile keeps the dash).
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

  void ratingsAnswer(double average, int count) =>
      when(() => reputation.fetchOwnRatingSummary('u-1')).thenAnswer(
        (_) async => RatingSummary(average: average, count: count),
      );

  void ratingsFail(Object error) =>
      when(() => reputation.fetchOwnRatingSummary('u-1'))
          .thenAnswer((_) => Future.error(error));

  void savedSummary(double average, int count, Duration age) =>
      when(() => reputation.readOwnRatingSummary('u-1')).thenAnswer(
        (_) async => (
          average: average,
          count: count,
          fetchedAt: now.subtract(age),
        ),
      );

  void verifyNothingSaved() => verifyNever(
        () => reputation.saveOwnRatingSummary(
          any(),
          average: any(named: 'average'),
          count: any(named: 'count'),
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
        verifyNever(() => reputation.fetchOwnRatingSummary(any()));
        verifyZeroInteractions(ratings);
      });

      // The server sends count 0 for a rated user whose row the rating
      // backfill has not reached: the ratings themselves know better.
      test('reputation count 0, ratings answer 12: the 12 are shown', () async {
        unrated();
        ratingsAnswer(4.6, 12);

        final rating = await load(role);

        expect(rating!.known, isTrue);
        expect(rating.hasRating, isTrue);
        expect(rating.average, 4.6);
        expect(rating.count, 12);
        // Kept from the reputation answer.
        expect(rating.penaltiesEnabled, isTrue);
        verify(
          () => reputation.saveOwnRatingSummary('u-1', average: 4.6, count: 12),
        ).called(1);
        // Only ever the user's own id.
        verify(() => reputation.fetchOwnRatingSummary('u-1')).called(1);
      });

      test('reputation count 0, ratings answer count 0: "None yet", saved',
          () async {
        unrated();
        ratingsAnswer(0, 0);

        final rating = await load(role);

        expect(rating, isNotNull);
        expect(rating!.known, isTrue);
        expect(rating.hasRating, isFalse);
        verify(
          () => reputation.saveOwnRatingSummary('u-1', average: 0, count: 0),
        ).called(1);
      });

      test(
          'a truly unrated user whose reputation came from the saved copy: '
          '"None yet" once the ratings read answers 0', () async {
        // The reputation answer is the saved one, 10 minutes old (not
        // stale): the ratings read is a real one, so its 0 is believed.
        unrated(fetchedAt: now.subtract(const Duration(minutes: 10)));
        ratingsAnswer(0, 0);

        final rating = await load(role);

        expect(rating!.known, isTrue);
        expect(rating.hasRating, isFalse);
        verify(
          () => reputation.saveOwnRatingSummary('u-1', average: 0, count: 0),
        ).called(1);
      });

      // The case the rating repositories got wrong: they answer "empty" for
      // a failed request, which a rated user on 3G would have seen as
      // "None yet" for 30 minutes.
      for (final error in const <Object>[
        NetworkException(),
        TimeoutException(),
        ServerException('Internal error', 503),
        RateLimitException(),
      ]) {
        test(
            'reputation count 0, ratings read fails (${error.runtimeType}): '
            'unknown, nothing saved', () async {
          unrated();
          ratingsFail(error);

          final rating = await load(role);

          expect(rating!.known, isFalse);
          expect(rating.hasRating, isFalse);
          expect(rating.penaltiesEnabled, isTrue);
          verifyNothingSaved();
          // The failure-blind summary is never asked on this path.
          verifyZeroInteractions(ratings);
        });
      }

      test('a summary that cannot be saved is still shown', () async {
        unrated();
        ratingsAnswer(4.6, 12);
        when(
          () => reputation.saveOwnRatingSummary(
            any(),
            average: any(named: 'average'),
            count: any(named: 'count'),
          ),
        ).thenThrow(StateError('storage'));

        final rating = await load(role);

        expect(rating!.average, 4.6);
        expect(rating.count, 12);
      });

      test('the rating read is asked at most every 30 minutes', () async {
        unrated(fetchedAt: now.subtract(const Duration(minutes: 20)));
        savedSummary(4.6, 12, const Duration(minutes: 20));

        final rating = await load(role);

        expect(rating!.average, 4.6);
        expect(rating.count, 12);
        verifyNever(() => reputation.fetchOwnRatingSummary(any()));
      });

      test('a saved real 0 inside 30 minutes: "None yet", no call', () async {
        unrated();
        savedSummary(0, 0, const Duration(minutes: 20));

        final rating = await load(role);

        expect(rating!.known, isTrue);
        expect(rating.hasRating, isFalse);
        verifyNever(() => reputation.fetchOwnRatingSummary(any()));
      });

      test('an older saved answer is asked again', () async {
        unrated();
        savedSummary(4.6, 12, const Duration(minutes: 31));
        ratingsAnswer(4.5, 13);

        final rating = await load(role);

        expect(rating!.average, 4.5);
        expect(rating.count, 13);
      });

      test('an older saved rating and a failed read: the last known rating',
          () async {
        unrated();
        savedSummary(4.6, 12, const Duration(hours: 2));
        ratingsFail(const NetworkException());

        final rating = await load(role);

        expect(rating!.average, 4.6);
        expect(rating.count, 12);
        verifyNothingSaved();
      });

      test('an older saved 0 and a failed read: unknown, never "None yet"',
          () async {
        unrated();
        savedSummary(0, 0, const Duration(hours: 2));
        ratingsFail(const ServerException('Internal error', 500));

        final rating = await load(role);

        expect(rating!.known, isFalse);
        verifyNothingSaved();
      });

      test('offline with a saved reputation: the last known rating, no call',
          () async {
        unrated(stale: true, fetchedAt: now.subtract(const Duration(hours: 5)));
        savedSummary(4.6, 12, const Duration(hours: 5));

        final rating = await load(role);

        expect(rating!.average, 4.6);
        verifyNever(() => reputation.fetchOwnRatingSummary(any()));
        verifyZeroInteractions(ratings);
      });

      test('offline with an older saved 0: unknown, no call', () async {
        unrated(stale: true, fetchedAt: now.subtract(const Duration(hours: 5)));
        savedSummary(0, 0, const Duration(hours: 5));

        final rating = await load(role);

        expect(rating!.known, isFalse);
        expect(rating.penaltiesEnabled, isTrue);
        verifyNever(() => reputation.fetchOwnRatingSummary(any()));
      });

      test('offline with nothing saved: unknown, no call', () async {
        unrated(stale: true, fetchedAt: now.subtract(const Duration(hours: 5)));

        final rating = await load(role);

        expect(rating!.known, isFalse);
        verifyNever(() => reputation.fetchOwnRatingSummary(any()));
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
