import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/features/buyer/domain/woreda_centroids.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_distance_service.dart';

/// Pure, platform-agnostic relevance scoring for buyer intent matching.
///
/// No Isar, no network, no `dart:io` — safe to run on mobile (over
/// Isar-cached listings) AND on web (over API-fetched listings), so
/// `MatchingService` and `WebMatchingService` share ONE scoring definition
/// and can never silently diverge.
///
/// Scoring is a transparent weighted sum of independent signals, deliberately
/// NOT a learned/embedding model: the catalogue is small, target devices are
/// low-end (2GB RAM, Android 8+), and the weights must stay tunable by hand.
/// The signals and their relationships (substitutes = similarity; ranking must
/// be multi-signal, not a single naive price sort) follow the buyer-intent
/// research synthesis on two-sided marketplace matching.
///
/// Signals scored (all normalised to 0..1):
/// - **type**: exact crop = 1.0, accepted substitute = [substituteTypeScore]
/// - **price**: within budget = 1.0, near-miss band over budget decays to 0
/// - **geo**: woreda-string proximity proxy (full haversine is deferred until
///   intents carry coordinates / a woreda-centroid table exists)
/// - **quantity**: partial-fill ratio (listing covers what fraction of need)
/// - **freshness**: listing recency (newer = fresher), decaying to a floor
/// - **reliability**: farmer's listing-conversion track record (server-injected
///   onto the listing); unknown farmers score a neutral default so they're
///   neither rewarded nor punished until they have a history
class MatchScorer {
  const MatchScorer();

  // --- Tunable weights (sum = 1.0). Tune empirically against real usage. ---
  static const double wType = 0.30;
  static const double wPrice = 0.20;
  static const double wGeo = 0.18;
  static const double wQuantity = 0.12;
  static const double wFreshness = 0.10;
  static const double wReliability = 0.10;

  /// Reliability score assumed for a farmer with no track record (or a listing
  /// that predates reliability injection). Neutral — mildly below a proven
  /// farmer, mildly above a flaky one — so new farmers aren't shut out.
  static const double reliabilityNeutral = 0.7;

  /// A crop slightly over budget is still shown (with a decayed price score)
  /// up to this fraction over the buyer's max — the price near-miss band.
  static const double priceTolerance = 0.15; // +15%

  /// A listing must cover at least this fraction of the needed quantity to be
  /// considered a candidate (partial-fill near-miss).
  static const double minQuantityFraction = 0.5;

  /// Relevance of a crop reached via a substitute type rather than the exact
  /// crop the buyer asked for. Substitutes are interchangeable goods (a
  /// similarity relationship), so they score below an exact match but stay
  /// visible when exact matches are thin.
  static const double substituteTypeScore = 0.6;

  /// Listings newer than [freshFullDays] are maximally fresh; freshness then
  /// decays linearly to [freshFloor] by [freshFloorDays].
  static const int freshFullDays = 7;
  static const int freshFloorDays = 30;
  static const double freshFloor = 0.3;

  /// Geo score floor returned when either location name is unknown/empty, or
  /// when the two woreda strings share no substring. Closer textual matches
  /// score higher (exact = 1.0, partial overlap = 0.6); transport handles the
  /// distance, so a fully-different woreda is a floor, not a hard reject.
  static const double geoNeutral = 0.3;

  /// Distance (km) at which the haversine geo score decays to 0.5
  /// (`score = 1 / (1 + km / geoHalfScoreKm)`). Tuned for Ethiopian
  /// rural→market distances; tune empirically.
  static const double geoHalfScoreKm = 50.0;

  /// Accepted-substitute map (lowercase keys MUST match the stored `CropType`
  /// enum names, e.g. `lentils` not `lentil`). A buyer asking for the key may
  /// also accept the listed crops when exact matches are scarce. This is a
  /// domain artifact — tune it with agronomic / market knowledge. `teff`
  /// intentionally has no close substitute in the Ethiopian context.
  static const Map<String, List<String>> substitutes = {
    'wheat': ['barley'],
    'barley': ['wheat'],
    'maize': ['sorghum'],
    'sorghum': ['maize'],
    'chickpea': ['lentils'],
    'lentils': ['chickpea'],
  };

  /// Crop types to search for a given intent: the exact type plus its accepted
  /// substitutes. Drives candidate generation in both matching services.
  List<String> candidateCropTypes(String cropType) {
    final type = cropType.toLowerCase();
    return <String>[type, ...?substitutes[type]];
  }

  /// Whether [listing] matched a substitute type rather than the exact crop
  /// the buyer asked for (UI may label it as a "similar crop").
  bool isSubstitute(BuyerIntent intent, CropListing listing) =>
      listing.cropType.toLowerCase() != intent.cropType.toLowerCase();

  /// Whether a listing qualifies as a candidate match for the intent.
  ///
  /// Centralised so the result-listing path and the count-only path
  /// ([MatchScorer]-driven `refreshMatchCounts`) agree on what counts as a
  /// match — they must never drift.
  bool isCandidate(BuyerIntent intent, CropListing listing) {
    final types = candidateCropTypes(intent.cropType);
    if (!types.contains(listing.cropType.toLowerCase())) return false;
    // Price: within budget, or within the near-miss tolerance band. A
    // non-positive budget means "no budget set" — price is unconstrained,
    // mirroring the server's IsCandidate guard.
    if (intent.maxPricePerKg > 0 &&
        listing.pricePerKg > intent.maxPricePerKg * (1 + priceTolerance)) {
      return false;
    }
    // Quantity: must cover at least the minimum fraction of what's needed.
    if (listing.quantity < intent.quantityNeeded * minQuantityFraction) {
      return false;
    }
    return true;
  }

  double typeScore(BuyerIntent intent, CropListing listing) =>
      isSubstitute(intent, listing) ? substituteTypeScore : 1.0;

  double priceScore(BuyerIntent intent, CropListing listing) {
    final max = intent.maxPricePerKg;
    // A non-positive budget means "no budget set" (the max-price field is
    // optional): price is unconstrained and scores 1.0, identical to the
    // server scorer (MatchScoring.PriceScore) so rankings stay in parity.
    if (max <= 0) return 1.0;
    if (listing.pricePerKg <= max) return 1.0;
    // Linear decay across the tolerance band: at +0% over → 1.0, at +tol → 0.
    final over = (listing.pricePerKg - max) / max;
    return (1.0 - over / priceTolerance).clamp(0.0, 1.0);
  }

  double quantityScore(BuyerIntent intent, CropListing listing) {
    if (intent.quantityNeeded <= 0) return 1.0;
    return (listing.quantity / intent.quantityNeeded).clamp(0.0, 1.0);
  }

  /// Geographic relevance of [listing] to the buyer's delivery destination.
  ///
  /// Prefers true great-circle distance: when the crop has a real GPS fix and
  /// the delivery woreda resolves to a known centroid, score by haversine
  /// distance with a smooth decay (`1 / (1 + km / geoHalfScoreKm)`). Otherwise
  /// falls back to a woreda-string proximity proxy — so coverage gaps (crops
  /// without GPS, woredas absent from the centroid table, Ethiopic-script
  /// names) degrade gracefully rather than failing.
  double geoScore(BuyerIntent intent, CropListing listing) {
    final destCoord = WoredaCentroids.lookup(intent.deliveryWoreda);
    final cropCoord = listing.location;
    if (destCoord != null && _hasRealFix(cropCoord)) {
      final km = GpsDistanceService.haversineDistance(
            cropCoord.latitude,
            cropCoord.longitude,
            destCoord.latitude,
            destCoord.longitude,
          ) /
          1000.0;
      return 1.0 / (1.0 + km / geoHalfScoreKm);
    }
    return _woredaProxyScore(intent, listing);
  }

  /// Whether [coord] is a usable GPS fix. `(0, 0)` is the "no GPS captured"
  /// sentinel written by crop creation when location permission is denied.
  bool _hasRealFix(LatLng coord) {
    if (coord.latitude == 0 && coord.longitude == 0) return false;
    return coord.latitude.abs() <= 90 && coord.longitude.abs() <= 180;
  }

  double _woredaProxyScore(BuyerIntent intent, CropListing listing) {
    final crop = listing.locationName.trim().toLowerCase();
    final dest = intent.deliveryWoreda.trim().toLowerCase();
    if (crop.isEmpty || dest.isEmpty) return geoNeutral;
    if (crop == dest) return 1.0;
    if (crop.contains(dest) || dest.contains(crop)) return 0.6;
    return geoNeutral;
  }

  double freshnessScore(CropListing listing, DateTime now) {
    final ageDays = now.difference(listing.createdAt).inDays;
    if (ageDays <= freshFullDays) return 1.0;
    if (ageDays >= freshFloorDays) return freshFloor;
    const span = freshFloorDays - freshFullDays;
    final decayed =
        1.0 - ((ageDays - freshFullDays) / span) * (1.0 - freshFloor);
    return decayed.clamp(freshFloor, 1.0);
  }

  /// Farmer reliability signal: the listing's server-injected
  /// [CropListing.farmerReliability], or [reliabilityNeutral] when absent.
  double reliabilityScore(CropListing listing) =>
      (listing.farmerReliability ?? reliabilityNeutral).clamp(0.0, 1.0);

  /// Overall 0..1 relevance score for [listing] against [intent] at [now].
  double scoreListing(BuyerIntent intent, CropListing listing, DateTime now) {
    return wType * typeScore(intent, listing) +
        wPrice * priceScore(intent, listing) +
        wGeo * geoScore(intent, listing) +
        wQuantity * quantityScore(intent, listing) +
        wFreshness * freshnessScore(listing, now) +
        wReliability * reliabilityScore(listing);
  }
}
