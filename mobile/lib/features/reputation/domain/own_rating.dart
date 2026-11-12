import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';

/// ESC-31: the signed-in user's own rating, for the Profile's Rating tile.
class OwnRating {
  /// The buyers' star average; null with no ratings (or none readable).
  final double? average;
  final int count;

  /// Whether the server's penalties flag is on for this user's reputation:
  /// Profile then offers My reputation. False for buyers and while unknown.
  final bool penaltiesEnabled;

  /// Whether the rating is known. False when the reputation was read but
  /// the ratings could not be checked: the tile then keeps the dash, and
  /// [penaltiesEnabled] still holds.
  final bool known;

  const OwnRating({
    this.average,
    this.count = 0,
    this.penaltiesEnabled = false,
    this.known = true,
  });

  /// No ratings yet: a farmer or trucker nobody has rated.
  static const none = OwnRating();

  bool get hasRating => average != null && count > 0;
}

/// Reads the signed-in user's own rating.
///
/// - A buyer makes no call and has no rating to show (null): buyers rate,
///   they are never rated, and they have no reputation (the server answers
///   them `403`).
/// - A farmer or trucker reads their own `GET /reputation/me` through
///   [reputation] (asked at most every 30 minutes, saved for offline). The
///   raw buyers' average is shown, as on My reputation.
/// - That answer carries no rating for a new user, and also for a rated
///   user whose row the server's rating backfill has not reached
///   (`rating-api.md` "Stored aggregate" step 4). So "no rating" there is
///   checked against the user's own `GET /ratings?rateeId={self}` aggregate
///   through [ratings], which still counts every rating and which the
///   server allows the ratee. That answer is kept for 30 minutes too. "None
///   yet" is said only when both agree.
/// - When `/reputation/me` cannot be read and nothing is saved, the same
///   aggregate alone.
///
/// Only ever [userId]'s own data. Never throws: null means there is nothing
/// to say (a buyer, or offline with nothing saved), which is not the same
/// as having no ratings.
Future<OwnRating?> loadOwnRating({
  required String userId,
  required UserRole role,
  ReputationRepository? reputation,
  BaseRatingRepository? ratings,
  DateTime Function()? now,
}) async {
  if (role == UserRole.buyer) return null;
  final clock = now ?? DateTime.now;
  if (reputation != null) {
    final OwnReputationSnapshot own;
    try {
      own = await reputation.loadOwn(userId);
    } on Object catch (e) {
      debugPrint('[OwnRating] reputation read failed: $e');
      return _fromRatings(userId, ratings);
    }
    final penaltiesEnabled = own.reputation.penaltiesEnabled;
    if (own.reputation.hasRating) {
      return OwnRating(
        average: own.reputation.ratingAverage,
        count: own.reputation.ratingCount,
        penaltiesEnabled: penaltiesEnabled,
      );
    }

    // The reputation answer shows no rating: ask the ratings themselves,
    // at most every 30 minutes.
    final saved = await reputation.readOwnRatingSummary(userId);
    final current = clock().toUtc();
    if (saved != null) {
      final age = current.difference(saved.fetchedAt);
      if (!age.isNegative && age < ReputationRepository.refreshInterval) {
        return _withSummary(saved.average, saved.count, penaltiesEnabled);
      }
    }
    // The rating repositories answer "empty" both for no ratings and for a
    // failed request, so an empty answer counts as "none" only when the
    // reputation answer itself just came from the server.
    final fresh = !own.stale &&
        current.difference(own.fetchedAt).abs() < const Duration(minutes: 1);
    if (ratings != null && !own.stale) {
      try {
        final summary = await ratings.getRatingSummary(userId);
        if (summary.count > 0 || fresh) {
          await reputation.saveOwnRatingSummary(
            userId,
            average: summary.average,
            count: summary.count,
          );
          return _withSummary(summary.average, summary.count, penaltiesEnabled);
        }
      } on Object catch (e) {
        debugPrint('[OwnRating] rating summary read failed: $e');
      }
    }
    // Offline, or nothing certain: the last known answer, else unknown (the
    // tile keeps the dash) with what the reputation answer still tells.
    if (saved != null) {
      return _withSummary(saved.average, saved.count, penaltiesEnabled);
    }
    return OwnRating(known: false, penaltiesEnabled: penaltiesEnabled);
  }
  return _fromRatings(userId, ratings);
}

OwnRating _withSummary(double average, int count, bool penaltiesEnabled) =>
    count > 0
        ? OwnRating(
            average: average,
            count: count,
            penaltiesEnabled: penaltiesEnabled,
          )
        : OwnRating(penaltiesEnabled: penaltiesEnabled);

/// The user's own rating aggregate alone; null when it shows no rating
/// (which may be a failed request).
Future<OwnRating?> _fromRatings(
  String userId,
  BaseRatingRepository? ratings,
) async {
  if (ratings == null) return null;
  try {
    final summary = await ratings.getRatingSummary(userId);
    if (summary.count > 0) {
      return OwnRating(average: summary.average, count: summary.count);
    }
  } on Object catch (e) {
    debugPrint('[OwnRating] rating summary read failed: $e');
  }
  return null;
}
