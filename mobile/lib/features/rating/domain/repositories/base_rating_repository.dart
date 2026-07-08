import 'package:wereda_connect/core/models/rating.dart';

/// Aggregate rating figures for a rated user (ratee).
class RatingSummary {
  final double average;
  final int count;

  const RatingSummary({required this.average, required this.count});

  static const empty = RatingSummary(average: 0, count: 0);
}

/// Abstract base for rating repositories.
///
/// Extracted so both mobile (Isar + sync) and web (HTTP-only) implementations
/// share the same BLoC interface. Ratings are create-only and immutable.
abstract class BaseRatingRepository {
  /// Create a rating (a buyer rates a counterparty for a delivered order).
  ///
  /// Idempotent per (order, ratee): the id is deterministic
  /// (`"{orderId}:{rateeId}"`) so re-submitting is a no-op server-side.
  Future<Rating> createRating({
    required String orderId,
    required String raterId,
    required String rateeId,
    required String rateeRole,
    required int stars,
    String? comment,
  });

  /// Whether the buyer already rated [rateeId] for [orderId].
  Future<bool> hasRated({
    required String orderId,
    required String rateeId,
  });

  /// Ratings received by [rateeId], newest first (best-effort; may be empty
  /// offline).
  Future<List<Rating>> getRatingsForRatee(String rateeId);

  /// Average + count for [rateeId] (best-effort; [RatingSummary.empty] offline
  /// or when there are no ratings yet).
  Future<RatingSummary> getRatingSummary(String rateeId);
}
