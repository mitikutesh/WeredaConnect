import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';

/// ESC-31: the public reputation of a listing's farmer or a trip's trucker,
/// as the server copied it onto the row (reputation-api.md section 7). Cards
/// read it from the cached row: no call per card, and it works offline.
///
/// The server sends the copy only while its public reputation flag is on,
/// so [isPresent] is the one switch the cards key on: without a copy every
/// card looks and behaves as it did before.
class OwnerReputation {
  /// The public tier: `good` or `low`.
  final String? tier;
  final double? effectiveRating;
  final int? ratingCount;
  final int? reliabilityPct;

  /// When the server wrote the copy.
  final DateTime? asOf;

  const OwnerReputation({
    this.tier,
    this.effectiveRating,
    this.ratingCount,
    this.reliabilityPct,
    this.asOf,
  });

  /// No copy: the flag is off, or the row has none yet.
  static const none = OwnerReputation();

  factory OwnerReputation.ofListing(CropListing listing) => OwnerReputation(
        tier: listing.ownerTier,
        effectiveRating: listing.ownerEffectiveRating,
        ratingCount: listing.ownerRatingCount,
        reliabilityPct: listing.ownerReliabilityPct,
        asOf: listing.ownerReputationAt,
      );

  factory OwnerReputation.ofTrip(Trip trip) => OwnerReputation(
        tier: trip.ownerTier,
        effectiveRating: trip.ownerEffectiveRating,
        ratingCount: trip.ownerRatingCount,
        reliabilityPct: trip.ownerReliabilityPct,
        asOf: trip.ownerReputationAt,
      );

  /// Whether the row carries the server's copy. The tier and the stamp come
  /// with every copy; the reliability alone (stored since ESC-30) does not
  /// count, so a row with only that keeps the old rating read.
  bool get isPresent => tier != null || asOf != null;

  /// Whether buyers are shown the "Low reputation" label. The public tier is
  /// `good` or `low`; a `restricted` that ever reached a client reads as low
  /// too, as the contract's public tier does. No other tier name is shown.
  bool get isLow => tier == 'low' || tier == 'restricted';

  /// The rating to show: the effective rating, only with ratings behind it.
  double? get rating => effectiveRating != null && (ratingCount ?? 0) > 0
      ? effectiveRating
      : null;

  /// The number of ratings behind [rating]; null when there is no rating.
  int? get count => rating == null ? null : ratingCount;
}
