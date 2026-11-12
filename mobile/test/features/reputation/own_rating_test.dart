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

  setUp(() {
    reputation = MockReputationRepository();
    ratings = _MockRatingRepository();
  });

  test('a buyer makes no call and has no rating', () async {
    final rating = await loadOwnRating(
      userId: 'b-1',
      role: UserRole.buyer,
      reputation: reputation,
      ratings: ratings,
    );

    expect(rating, isNotNull);
    expect(rating!.hasRating, isFalse);
    expect(rating.penaltiesEnabled, isFalse);
    verifyZeroInteractions(reputation);
    verifyZeroInteractions(ratings);
  });

  for (final role in [UserRole.farmer, UserRole.trucker]) {
    test('${role.name}: the raw average from the own reputation', () async {
      when(() => reputation.loadOwn('u-1')).thenAnswer(
        (_) async => snapshot(ownReputation(deduction: 0.4, points: 4)),
      );

      final rating = await loadOwnRating(
        userId: 'u-1',
        role: role,
        reputation: reputation,
        ratings: ratings,
      );

      expect(rating!.average, 4.8);
      expect(rating.count, 12);
      expect(rating.penaltiesEnabled, isTrue);
      verifyZeroInteractions(ratings);
    });

    test('${role.name}: no ratings yet is known, not unknown', () async {
      when(() => reputation.loadOwn('u-1')).thenAnswer(
        (_) async => snapshot(
          ownReputation(rated: false, penaltiesEnabled: false),
        ),
      );

      final rating = await loadOwnRating(
        userId: 'u-1',
        role: role,
        reputation: reputation,
        ratings: ratings,
      );

      expect(rating, isNotNull);
      expect(rating!.hasRating, isFalse);
      verifyZeroInteractions(ratings);
    });

    test('${role.name}: falls back to the own rating aggregate', () async {
      when(() => reputation.loadOwn('u-1')).thenThrow(const NetworkException());
      when(() => ratings.getRatingSummary('u-1')).thenAnswer(
        (_) async => const RatingSummary(average: 4.6, count: 5),
      );

      final rating = await loadOwnRating(
        userId: 'u-1',
        role: role,
        reputation: reputation,
        ratings: ratings,
      );

      expect(rating!.average, 4.6);
      expect(rating.count, 5);
      expect(rating.penaltiesEnabled, isFalse);
    });

    test('${role.name}: nothing readable is unknown (null)', () async {
      when(() => reputation.loadOwn('u-1')).thenThrow(const NetworkException());
      // The rating repositories answer "empty" both offline and for no
      // ratings, so an empty aggregate here proves nothing.
      when(() => ratings.getRatingSummary('u-1'))
          .thenAnswer((_) async => RatingSummary.empty);

      expect(
        await loadOwnRating(
          userId: 'u-1',
          role: role,
          reputation: reputation,
          ratings: ratings,
        ),
        isNull,
      );
      expect(
        await loadOwnRating(userId: 'u-1', role: role),
        isNull,
      );
    });
  }
}
