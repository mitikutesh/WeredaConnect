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

  const OwnRating({
    this.average,
    this.count = 0,
    this.penaltiesEnabled = false,
  });

  /// No ratings: a new farmer or trucker, a buyer (buyers are not rated),
  /// or nothing readable yet.
  static const none = OwnRating();

  bool get hasRating => average != null && count > 0;
}

/// Reads the signed-in user's own rating.
///
/// - A buyer makes no call: buyers rate, they are not rated, and they have
///   no reputation (the server answers them `403`).
/// - A farmer or trucker reads their own `GET /reputation/me` through
///   [reputation] (asked at most every 30 minutes, saved for offline). The
///   raw buyers' average is shown, as on My reputation.
/// - When that cannot be read and nothing is saved, the user's own
///   `GET /ratings?rateeId={self}` aggregate through [ratings], which the
///   server allows the ratee.
///
/// Only ever [userId]'s own data. Never throws: null means the rating could
/// not be read (offline with nothing saved), which is not the same as
/// having none.
Future<OwnRating?> loadOwnRating({
  required String userId,
  required UserRole role,
  ReputationRepository? reputation,
  BaseRatingRepository? ratings,
}) async {
  if (role == UserRole.buyer) return OwnRating.none;
  if (reputation != null) {
    try {
      final own = (await reputation.loadOwn(userId)).reputation;
      return OwnRating(
        average: own.hasRating ? own.ratingAverage : null,
        count: own.hasRating ? own.ratingCount : 0,
        penaltiesEnabled: own.penaltiesEnabled,
      );
    } on Object catch (e) {
      debugPrint('[OwnRating] reputation read failed: $e');
    }
  }
  if (ratings != null) {
    try {
      final summary = await ratings.getRatingSummary(userId);
      if (summary.count > 0) {
        return OwnRating(average: summary.average, count: summary.count);
      }
    } on Object catch (e) {
      debugPrint('[OwnRating] rating summary read failed: $e');
    }
  }
  return null;
}
