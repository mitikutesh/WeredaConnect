import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';

/// ESC-31: reads the owner's public reputation copy off a server crop or trip
/// row (reputation-api.md section 7): `ownerTier`, `ownerEffectiveRating`,
/// `ownerRatingCount`, `ownerReliabilityPct` and `ownerReputationAt`.
///
/// The server leaves all five out while its public reputation flag is off,
/// and each one out when it has no value, so every field is nullable and a
/// row without them parses as it did before. One reader for every place a
/// row is parsed (the marketplace pull, the web repositories, the web
/// matcher and the conflict echo), so they cannot drift apart.
///
/// The fields are server-owned: nothing here is ever written back.
typedef OwnerReputationFields = ({
  String? tier,
  double? effectiveRating,
  int? ratingCount,
  int? reliabilityPct,
  DateTime? at,
});

/// The five owner fields of a server row [m], each null when missing or of
/// an unexpected type (a bad value never fails the whole row).
OwnerReputationFields ownerReputationFromWire(Map<String, dynamic> m) {
  final tier = m['ownerTier'];
  final rating = m['ownerEffectiveRating'];
  final count = m['ownerRatingCount'];
  final pct = m['ownerReliabilityPct'];
  final at = m['ownerReputationAt'];
  return (
    tier: tier is String && tier.isNotEmpty ? tier : null,
    effectiveRating: rating is num ? rating.toDouble() : null,
    ratingCount: count is num ? count.toInt() : null,
    reliabilityPct: pct is num ? pct.toInt() : null,
    at: at is String ? DateTime.tryParse(at)?.toUtc() : null,
  );
}

extension CropOwnerReputationWire on CropListing {
  /// Replaces this listing's owner reputation copy with the one on [m].
  void readOwnerReputation(Map<String, dynamic> m) {
    final fields = ownerReputationFromWire(m);
    ownerTier = fields.tier;
    ownerEffectiveRating = fields.effectiveRating;
    ownerRatingCount = fields.ratingCount;
    ownerReliabilityPct = fields.reliabilityPct;
    ownerReputationAt = fields.at;
  }
}

extension TripOwnerReputationWire on Trip {
  /// Replaces this trip's owner reputation copy with the one on [m].
  void readOwnerReputation(Map<String, dynamic> m) {
    final fields = ownerReputationFromWire(m);
    ownerTier = fields.tier;
    ownerEffectiveRating = fields.effectiveRating;
    ownerRatingCount = fields.ratingCount;
    ownerReliabilityPct = fields.reliabilityPct;
    ownerReputationAt = fields.at;
  }
}
