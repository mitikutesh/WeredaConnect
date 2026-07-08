import 'package:isar/isar.dart';

part 'rating.g.dart';

/// A rating a buyer leaves for a counterparty (farmer or trucker) after a
/// completed order.
///
/// Offline-first: created locally in Isar, then synced via the sync queue.
/// Immutable once created — exactly one rating per (order, ratee), enforced
/// server-side by the deterministic [ratingId].
@collection
class Rating {
  /// Isar auto-generated ID (internal use only)
  Id id = Isar.autoIncrement;

  /// Deterministic rating id, `"{orderId}:{rateeId}"`. The server's
  /// `attribute_not_exists` create condition uses this to guarantee one rating
  /// per (order, ratee).
  @Index(unique: true)
  late String ratingId;

  /// Order this rating is about.
  @Index()
  late String orderId;

  /// User leaving the rating (always the buyer).
  @Index()
  late String raterId;

  /// User being rated (the order's farmer or trucker).
  @Index()
  late String rateeId;

  /// Role of the ratee: `farmer` or `trucker`.
  late String rateeRole;

  /// Star rating in [1, 5].
  late int stars;

  /// Optional free-text comment (localized by the buyer, stored as-is).
  String? comment;

  /// Timestamp when the rating was created (UTC).
  late DateTime createdAt;

  /// Timestamp of last update (UTC). Equals [createdAt] — ratings are immutable.
  late DateTime updatedAt;

  /// Version number for optimistic concurrency control.
  late int version;
}
