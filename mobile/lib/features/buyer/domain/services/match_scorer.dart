import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/trip_departure.dart';
import 'package:wereda_connect/features/buyer/domain/woreda_centroids.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_distance_service.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';

/// MATCH-01: the centroid of a woreda name, or null when it is unknown.
/// [WoredaCentroids.lookup] in the app; a test can pass a fixed table.
typedef WoredaLocator = LatLng? Function(String woreda);

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
/// - **reliability**: the farmer's `ownerReliabilityPct` (completed orders
///   over completed, fault-cancelled and no-show orders in 90 days, copied
///   onto the listing by the server, ESC-30); a listing without it scores a
///   neutral default so a farmer is neither rewarded nor punished until they
///   have a history
class MatchScorer {
  const MatchScorer();

  // --- Tunable weights (sum = 1.0). Tune empirically against real usage. ---
  static const double wType = 0.30;
  static const double wPrice = 0.20;
  static const double wGeo = 0.18;
  static const double wQuantity = 0.12;
  static const double wFreshness = 0.10;
  static const double wReliability = 0.10;

  /// Reliability assumed when the owner's `reliabilityPct` is absent: the
  /// public reputation flag is off, the owner has fewer than 3 orders, or the
  /// row has no copy yet. It is the value every listing scored with before
  /// ESC-30 in practice, so a flags-off ranking is the old neutral ranking,
  /// and a known reliability only moves a listing up (above 70) or down
  /// (below 70) from there. Same value as the server's `ReliabilityNeutral`.
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
  /// when the two woreda names share no whole word after [normalizeWoreda].
  /// Closer textual matches score higher (same place = 1.0, whole-word
  /// overlap = 0.6); transport handles the distance, so a fully-different
  /// woreda is a floor, not a hard reject.
  static const double geoNeutral = 0.3;

  /// Geo score for a whole-word partial match of the two woreda names.
  static const double geoPartial = 0.6;

  /// Distance (km) at which the haversine geo score decays to 0.5
  /// (`score = 1 / (1 + km / geoHalfScoreKm)`). Tuned for Ethiopian
  /// rural→market distances; tune empirically.
  static const double geoHalfScoreKm = 50.0;

  /// MATCH-01: how far (km, straight line between centroids) a trip's start
  /// may be from the crop's woreda, and its end from the buyer's delivery
  /// woreda, for the trip to be suggested. It keeps a neighbouring town
  /// (Hawassa and Shashemene are 22 km apart, Adama and Bishoftu 39.5) and
  /// leaves out the next market over (Addis Ababa and Bishoftu are 40.8).
  /// Conservative on purpose: the trip's price does not show the detour.
  /// Same value as the server's `TripNearRadiusKm`.
  static const double tripNearRadiusKm = 40.0;

  /// MATCH-01: slack on the room-left check, the server's order tolerance
  /// (`MoneyTolerance` in `BindOrderToTrip`): a trip is suggested when its
  /// room is at least the need minus 0.01 quintal, so the order create
  /// accepts it.
  static const double tripRoomSlackQuintals = 0.01;

  /// MATCH-01: the room a trip must have left when the request names no
  /// positive quantity, so a full or nearly full truck is never suggested.
  static const double tripMinUsefulLoadQuintals = 1.0;

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

  /// Woreda-string proximity proxy. Both names go through the shared
  /// [normalizeWoreda] rule, so "Ādama", "Adama, Oromia" and "adama" are the
  /// same place; the server's MatchScoring applies the identical rule.
  double _woredaProxyScore(BuyerIntent intent, CropListing listing) {
    switch (compareWoredas(intent.deliveryWoreda, listing.locationName)) {
      case WoredaMatch.exact:
        return 1.0;
      case WoredaMatch.partial:
        return geoPartial;
      case WoredaMatch.none:
        return geoNeutral;
    }
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

  /// ESC-30: an owner's `reliabilityPct` (0 to 100) as a 0..1 signal, or
  /// [reliabilityNeutral] when absent. Out of range values are clamped. The
  /// server's `MatchScoring.ReliabilityFromPct` is the same rule.
  double reliabilityFromPct(int? reliabilityPct) => reliabilityPct == null
      ? reliabilityNeutral
      : (reliabilityPct / 100.0).clamp(0.0, 1.0);

  /// Farmer reliability signal: the listing's server-written
  /// [CropListing.ownerReliabilityPct] through [reliabilityFromPct].
  double reliabilityScore(CropListing listing) =>
      reliabilityFromPct(listing.ownerReliabilityPct);

  /// MATCH-01: the trips to suggest for a crop at [listingWoreda] going to
  /// [deliveryWoreda], best first. Shared by the mobile and the web matching
  /// service; the server's `MatchScoring.SuggestTrips` is the same rule.
  ///
  /// A trip is kept when [isTripEligible] (open, not left, outside the order
  /// lead, room for the need, departing by the deadline) and
  /// [tripRouteScore] serves the route; the kept trips are ordered by
  /// [rankTripSuggestions]. Centroids come from [locate], once per name.
  List<Trip> suggestTrips(
    Iterable<Trip> trips, {
    required String listingWoreda,
    required String deliveryWoreda,
    required double quantityNeeded,
    required DateTime now,
    int? orderLeadHours,
    DateTime? neededByDate,
    WoredaLocator locate = WoredaCentroids.lookup,
  }) {
    final points = <String, LatLng?>{};
    LatLng? cached(String woreda) =>
        points.putIfAbsent(woreda, () => locate(woreda));
    final routes = Map<Trip, double>.identity();
    for (final trip in trips) {
      if (!isTripEligible(
        trip,
        quantityNeeded: quantityNeeded,
        now: now,
        orderLeadHours: orderLeadHours,
        neededByDate: neededByDate,
      )) {
        continue;
      }
      final route = tripRouteScore(
        trip,
        listingWoreda: listingWoreda,
        deliveryWoreda: deliveryWoreda,
        locate: cached,
      );
      if (route != null) routes[trip] = route;
    }
    return rankTripSuggestions(routes.keys, routeScore: (t) => routes[t]!);
  }

  /// MATCH-01: whether [trip] can take the buyer's load at all, before the
  /// route is looked at. It must be `open`, its departure day in Ethiopia not
  /// over ([tripHasLeft], FUP-85), outside the order lead when the server runs
  /// the commitments ([tripInsideOrderLead], FUP-122), with room left for
  /// [quantityNeeded] less [tripRoomSlackQuintals] (or for
  /// [tripMinUsefulLoadQuintals] when the need is not positive), and, when
  /// the buyer set [neededByDate], departing on or before it. The deadline is
  /// compared with the departure, not an arrival: trips carry no transit
  /// time. A trip outside that window is dropped, not scored down.
  bool isTripEligible(
    Trip trip, {
    required double quantityNeeded,
    required DateTime now,
    int? orderLeadHours,
    DateTime? neededByDate,
  }) {
    if (trip.status != TripStatus.open) return false;
    if (tripHasLeft(trip, now)) return false;
    if (tripInsideOrderLead(trip, now, orderLeadHours)) return false;
    final need =
        quantityNeeded > 0 ? quantityNeeded : tripMinUsefulLoadQuintals;
    if (tripRoomLeft(trip) < need - tripRoomSlackQuintals) return false;
    if (neededByDate != null && trip.departureDate.isAfter(neededByDate)) {
      return false;
    }
    return true;
  }

  /// MATCH-01: how well [trip]'s route serves a crop at [listingWoreda]
  /// going to [deliveryWoreda], in 0..1: the mean of [tripEndScore] for the
  /// trip's start against the crop's woreda and for its end against the
  /// delivery woreda. Null when either end does not serve (the trip is then
  /// not suggested). 1.0 is the exact route.
  double? tripRouteScore(
    Trip trip, {
    required String listingWoreda,
    required String deliveryWoreda,
    WoredaLocator locate = WoredaCentroids.lookup,
  }) {
    final from = tripEndScore(
      trip.fromWoreda,
      listingWoreda,
      tripPoint: locate(trip.fromWoreda),
      wantedPoint: locate(listingWoreda),
    );
    if (from == null) return null;
    final to = tripEndScore(
      trip.toWoreda,
      deliveryWoreda,
      tripPoint: locate(trip.toWoreda),
      wantedPoint: locate(deliveryWoreda),
    );
    if (to == null) return null;
    return (from + to) / 2;
  }

  /// MATCH-01: how well one end of a trip, [tripWoreda], serves the place the
  /// buyer wants it at, [wantedWoreda], in 0..1, or null when it does not.
  ///
  /// The best of what applies: 1.0 when the two names are the same place
  /// after [normalizeWoreda]; `1 / (1 + km / geoHalfScoreKm)` (the [geoScore]
  /// decay) when both centroids are known and at most [tripNearRadiusKm]
  /// apart ([tripEndKm]); [geoPartial] for a whole-word name match. The name
  /// match is the fallback for a woreda the centroid table does not know. A
  /// blank name never serves.
  double? tripEndScore(
    String tripWoreda,
    String wantedWoreda, {
    LatLng? tripPoint,
    LatLng? wantedPoint,
  }) {
    final tripKey = normalizeWoreda(tripWoreda);
    final wantedKey = normalizeWoreda(wantedWoreda);
    if (tripKey.isEmpty || wantedKey.isEmpty) return null;
    final match = compareWoredaKeys(tripKey, wantedKey);
    if (match == WoredaMatch.exact) return 1.0;
    double? score = match == WoredaMatch.partial ? geoPartial : null;
    final km = tripEndKm(tripPoint, wantedPoint);
    if (km != null && km <= tripNearRadiusKm) {
      final byDistance = 1.0 / (1.0 + km / geoHalfScoreKm);
      if (score == null || byDistance > score) score = byDistance;
    }
    return score;
  }

  /// MATCH-01: the great-circle distance between two centroids in km,
  /// rounded half away from zero to 0.1 km, or null when either point is
  /// unknown or not a real position. The rounding keeps the phone, the web
  /// and the server on the same number whatever the platform's last digit
  /// of sin and cos.
  double? tripEndKm(LatLng? a, LatLng? b) {
    if (a == null || b == null || !_hasRealFix(a) || !_hasRealFix(b)) {
      return null;
    }
    final km = GpsDistanceService.haversineDistance(
          a.latitude,
          a.longitude,
          b.latitude,
          b.longitude,
        ) /
        1000.0;
    return (km * 10).round() / 10;
  }

  /// The order of the trips suggested for a crop (ESC-30, MATCH-01), shared
  /// by the mobile and the web matching service through [suggestTrips].
  ///
  /// 1. Cheapest `pricePerQuintal` first, so the first trip is always a
  ///    cheapest one (the estimated total uses it).
  /// 2. At the same price, the better [routeScore] first: the exact route
  ///    above a neighbouring woreda. Without [routeScore] every trip counts
  ///    as the exact route.
  /// 3. Then the trucker with the higher [reliabilityFromPct] (an unknown
  ///    trucker counts as [reliabilityNeutral]).
  /// 4. Then the earlier departure, then `tripId`, so the order never
  ///    depends on the order the trips came in (the phone's cache and the
  ///    web's API pages differ).
  List<Trip> rankTripSuggestions(
    Iterable<Trip> trips, {
    double Function(Trip trip)? routeScore,
  }) {
    final keyed = [
      for (final (index, trip) in trips.indexed)
        (index, trip, routeScore == null ? 1.0 : routeScore(trip)),
    ]..sort((a, b) {
        final byPrice = a.$2.pricePerQuintal.compareTo(b.$2.pricePerQuintal);
        if (byPrice != 0) return byPrice;
        final byRoute = b.$3.compareTo(a.$3);
        if (byRoute != 0) return byRoute;
        final byReliability = reliabilityFromPct(b.$2.ownerReliabilityPct)
            .compareTo(reliabilityFromPct(a.$2.ownerReliabilityPct));
        if (byReliability != 0) return byReliability;
        final byDeparture = a.$2.departureDate.compareTo(b.$2.departureDate);
        if (byDeparture != 0) return byDeparture;
        final byId = a.$2.tripId.compareTo(b.$2.tripId);
        return byId != 0 ? byId : a.$1.compareTo(b.$1);
      });
    return [for (final entry in keyed) entry.$2];
  }

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
