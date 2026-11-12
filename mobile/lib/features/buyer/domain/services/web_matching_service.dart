import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order_pricing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/trip_departure.dart';
import 'package:wereda_connect/core/models/owner_reputation_wire.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';
import 'package:wereda_connect/features/buyer/domain/services/match_scorer.dart';

/// Web / online matching service — client-side matching over API data.
///
/// On mobile (offline-first), matching runs on-device over Isar-cached data
/// via [MatchScorer]. Web has no Isar, so this service fetches the marketplace
/// from the API (`GET /crops?status=available`, `GET /trips?status=open`,
/// following pagination) and runs the SAME [MatchScorer] against it. Mobile
/// and web therefore share ONE scoring definition and produce identical
/// rankings — the device does the ranking either way.
///
/// This deliberately does NOT depend on a server-side `GET /match` endpoint:
/// the plain `/crops` browse endpoint (which the marketplace already uses) is
/// enough to match locally, so matching keeps working even where `/match` is
/// not deployed.
///
/// Ethiopian context:
/// - Buyers see ALL farmers' crops (role visibility), so candidate generation
///   fetches every active listing, not just the buyer's own.
/// - Commission rate is 5% of the crop cost (platform fee, [OrderPricing]),
///   same as mobile.
/// - Trip pairing is [MatchScorer.suggestTrips] (MATCH-01), identical to the
///   mobile [MatchingService]: the exact route or a neighbouring woreda by
///   centroid distance, the woreda names as the fallback.
class WebMatchingService extends BaseMatchingService {
  final ApiClient _api;

  /// Shared, platform-agnostic relevance scorer — identical math to the mobile
  /// [MatchingService], so online and offline rankings agree.
  static const MatchScorer _scorer = MatchScorer();

  final DateTime Function() _clock;

  WebMatchingService({required ApiClient api, DateTime Function()? clock})
      : _api = api,
        _clock = clock ?? DateTime.now;

  @override
  Future<List<CropMatchResult>> matchListingsForIntent(
    BuyerIntent intent,
  ) async {
    final (matches, _) = await _match(intent);
    return matches;
  }

  /// Match ranked crops plus the count of eligible candidates.
  ///
  /// Candidate generation, near-miss tolerance, scoring and tie-breaking all
  /// mirror the mobile [MatchingService]: fetch every active listing, keep the
  /// ones [MatchScorer.isCandidate] accepts, score each, and rank by score DESC
  /// with cheaper crop price breaking ties.
  Future<(List<CropMatchResult>, int)> _match(BuyerIntent intent) async {
    try {
      final now = _clock().toUtc();

      // Buyers match against ALL farmers' active listings (role visibility).
      final crops = await _fetchAvailableCrops();
      final candidates =
          crops.where((c) => _scorer.isCandidate(intent, c)).toList();
      if (candidates.isEmpty) return (const <CropMatchResult>[], 0);

      // Pair each candidate crop with suggested trips (fetched once), with
      // the order lead the same response reported (FUP-122).
      final (allTrips, orderLeadHours) = await _fetchOpenTrips();

      final results = <CropMatchResult>[];
      for (final listing in candidates) {
        final trips = _filterTripsForRoute(
          allTrips,
          listing.locationName,
          intent.deliveryWoreda,
          intent.quantityNeeded,
          now: now,
          orderLeadHours: orderLeadHours,
          neededByDate: intent.neededByDate,
        );

        // Compute estimated total with the cheapest trip if available, with
        // the server's formula: commission on the crop cost only (SIM-53).
        final quantityKg = intent.quantityNeeded * 100;
        final cropCost = quantityKg * listing.pricePerKg;
        final transportCost = trips.isNotEmpty
            ? intent.quantityNeeded * trips.first.pricePerQuintal
            : 0.0;
        final estimatedTotal =
            OrderPricing(cropCost: cropCost, transportCost: transportCost)
                .orderTotal;

        results.add(
          CropMatchResult(
            listing: listing,
            suggestedTrips: trips,
            estimatedTotalMin: estimatedTotal,
            matchScore: _scorer.scoreListing(intent, listing, now),
            isSubstitute: _scorer.isSubstitute(intent, listing),
          ),
        );
      }

      // Rank by relevance (highest first); tie-break by cheaper crop price —
      // identical to the mobile ranking so results don't reorder online/offline.
      results.sort((a, b) {
        final byScore = b.matchScore.compareTo(a.matchScore);
        return byScore != 0
            ? byScore
            : a.listing.pricePerKg.compareTo(b.listing.pricePerKg);
      });

      return (results, results.length);
    } on ApiException {
      // Surface transient network/auth/server failures to the caller so the
      // BLoC can decide how to present them (empty/stale on reopen, silent on
      // create) instead of this layer silently masking them as 'no matches'.
      rethrow;
    }
  }

  @override
  Future<void> refreshMatchCounts(
    String buyerId,
    BaseBuyerIntentRepository repository,
  ) async {
    final intents = await repository.getActiveIntents(buyerId);
    if (intents.isEmpty) return;

    // Fetch the catalogue once and count candidates per intent with the SAME
    // predicate as [matchListingsForIntent], so the badge count can never drift
    // from what the buyer actually sees.
    final crops = await _fetchAvailableCrops();
    for (final intent in intents) {
      final count = crops.where((c) => _scorer.isCandidate(intent, c)).length;
      await repository.updateMatchCount(
        intent.intentId,
        count,
        DateTime.now().toUtc(),
      );
    }
  }

  /// Fetch all active crop listings from the API, following pagination.
  ///
  /// Uses the marketplace browse endpoint (`GET /crops?status=available`) so no
  /// dedicated match endpoint is required. Errors propagate so the caller can
  /// report a real failure instead of treating it as "no matches".
  Future<List<CropListing>> _fetchAvailableCrops() async {
    final crops = <CropListing>[];
    String? nextToken;
    do {
      final params = <String, dynamic>{'status': 'available', 'limit': '50'};
      if (nextToken != null) params['nextToken'] = nextToken;
      final data = await _api.get<Map<String, dynamic>>(
        '/crops',
        queryParameters: params,
      );
      final items = data['crops'] as List<dynamic>? ??
          data['items'] as List<dynamic>? ??
          const <dynamic>[];
      for (final item in items) {
        crops.add(_parseCrop(item as Map<String, dynamic>));
      }
      nextToken = data['nextToken'] as String?;
    } while (nextToken != null);
    return crops;
  }

  /// Fetch all open trips from the API, following pagination, with the order
  /// lead the server reported (FUP-122, the first page's; null when it sent
  /// none). Web reads both fresh on every match, so it caches neither.
  ///
  /// Errors are not swallowed: they propagate so the caller can report a real
  /// failure instead of treating it as "no trips available".
  Future<(List<Trip>, int?)> _fetchOpenTrips() async {
    final allTrips = <Trip>[];
    int? orderLeadHours;
    var firstPage = true;
    String? nextToken;
    do {
      final params = <String, dynamic>{'status': 'open', 'limit': '50'};
      if (nextToken != null) params['nextToken'] = nextToken;
      final data = await _api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: params,
      );
      if (firstPage) {
        orderLeadHours = orderLeadHoursFromWire(data);
        firstPage = false;
      }
      final items = data['trips'] as List<dynamic>? ??
          data['items'] as List<dynamic>? ??
          [];
      allTrips.addAll(
        items.map((item) => _parseTrip(item as Map<String, dynamic>)),
      );
      nextToken = data['nextToken'] as String?;
    } while (nextToken != null);
    return (allTrips, orderLeadHours);
  }

  /// Find trips for a specific listing → buyer delivery route.
  ///
  /// Fetches open trips and filters client-side for the given route, with the
  /// same rule and order as the mobile `MatchingService.findTripsForListing`.
  Future<List<Trip>> findTripsForListing(
    CropListing listing,
    String deliveryWoreda,
    double quantityNeeded, {
    DateTime? neededByDate,
  }) async {
    final (allTrips, orderLeadHours) = await _fetchOpenTrips();
    return _filterTripsForRoute(
      allTrips,
      listing.locationName,
      deliveryWoreda,
      quantityNeeded,
      now: _clock().toUtc(),
      orderLeadHours: orderLeadHours,
      neededByDate: neededByDate,
    );
  }

  /// The open trips that serve the origin → destination route, best first:
  /// [MatchScorer.suggestTrips] (MATCH-01), the rule the mobile
  /// [MatchingService] applies to its cache.
  /// - open, departing today or later in Ethiopia (FUP-85) and at least
  ///   [orderLeadHours] ahead when the server runs the commitments (FUP-122)
  /// - room left (`remainingQuintals`, else capacity) for [quantityNeeded],
  ///   less the server's 0.01 quintal tolerance (ESC-15)
  /// - departing on or before the buyer's deadline when one is set
  /// - starting at [fromLocation] or within 40 km of it, ending at
  ///   [toLocation] or within 40 km of it (centroid distance); the woreda
  ///   names decide when a centroid is unknown
  /// - cheapest first, then the exact route before a neighbouring woreda,
  ///   then the more reliable trucker, the earlier departure, the trip id
  List<Trip> _filterTripsForRoute(
    List<Trip> trips,
    String fromLocation,
    String toLocation,
    double quantityNeeded, {
    required DateTime now,
    required int? orderLeadHours,
    DateTime? neededByDate,
  }) {
    return _scorer.suggestTrips(
      trips,
      listingWoreda: fromLocation,
      deliveryWoreda: toLocation,
      quantityNeeded: quantityNeeded,
      now: now,
      orderLeadHours: orderLeadHours,
      neededByDate: neededByDate,
    );
  }

  Trip _parseTrip(Map<String, dynamic> m) {
    return Trip()
      ..tripId = m['tripId'] as String? ?? ''
      ..truckerId = m['truckerId'] as String? ?? ''
      ..truckerName = m['truckerName'] as String? ?? ''
      // ESC-48: GET /trips sends a buyer no truckerPhone, so this stays empty;
      // contact opens on the order.
      ..truckerPhone = m['truckerPhone'] as String? ?? ''
      ..vehiclePlate = m['vehiclePlate'] as String?
      ..vehicleDescription = m['vehicleDescription'] as String?
      ..fromWoreda = m['fromWoreda'] as String? ?? ''
      ..toWoreda = m['toWoreda'] as String? ?? ''
      ..departureDate =
          DateTime.tryParse(m['departureDate'] as String? ?? '') ??
              DateTime.now()
      ..capacity = (m['capacity'] as num?)?.toDouble() ?? 0
      ..pricePerQuintal = (m['pricePerQuintal'] as num?)?.toDouble() ?? 0
      ..bookedQuintals = quintalsFromWire(m['bookedQuintals'])
      ..remainingQuintals = quintalsFromWire(m['remainingQuintals'])
      ..readOwnerReputation(m)
      ..status = TripStatus.values.firstWhere(
        (e) => e.name == (m['status'] as String? ?? 'open'),
        orElse: () => TripStatus.open,
      )
      ..matchedListings = (m['matchedListings'] as List<dynamic>?)
              ?.map((e) => e as String)
              .toList() ??
          []
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt =
          DateTime.tryParse(m['updatedAt'] as String? ?? '') ?? DateTime.now()
      ..version = (m['version'] as num?)?.toInt() ?? 1;
  }

  /// Parse a crop from `GET /crops` into a [CropListing].
  ///
  /// The browse endpoint returns the crop's woreda but may omit GPS
  /// coordinates and the farmer's `ownerReliabilityPct` (sent only while the
  /// public reputation flag is on); the [MatchScorer] degrades gracefully in
  /// that case (woreda-string geo proxy + neutral reliability), so a missing
  /// field lowers precision but never breaks matching.
  CropListing _parseCrop(Map<String, dynamic> m) {
    return CropListing()
      // API uses "cropId"; sync-created listings echo "listingId".
      ..listingId = m['listingId'] as String? ?? m['cropId'] as String? ?? ''
      ..farmerId = m['farmerId'] as String? ?? ''
      ..farmerName = m['farmerName'] as String? ?? ''
      ..cropType = m['cropType'] as String? ?? ''
      // The number is quintals regardless of the key name (historical misnomer).
      ..quantity = (m['quantity'] as num?)?.toDouble() ??
          (m['quantityKg'] as num?)?.toDouble() ??
          0
      ..pricePerKg = (m['pricePerKg'] as num?)?.toDouble() ?? 0
      ..location = (LatLng()
        ..latitude = (m['latitude'] as num?)?.toDouble() ?? 0
        ..longitude = (m['longitude'] as num?)?.toDouble() ?? 0)
      ..locationName =
          m['locationName'] as String? ?? m['woreda'] as String? ?? ''
      ..description = m['description'] as String? ?? ''
      ..farmerReliability = (m['farmerReliability'] as num?)?.toDouble()
      ..readOwnerReputation(m)
      ..status = ListingStatus.active
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt = DateTime.tryParse(
            m['updatedAt'] as String? ?? m['createdAt'] as String? ?? '',
          ) ??
          DateTime.now()
      ..version = (m['version'] as num?)?.toInt() ?? 1;
  }
}
