import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';
import 'package:wereda_connect/features/buyer/domain/woreda_centroids.dart';

/// Web / online matching service — delegates scoring to the server.
///
/// On mobile (offline-first), matching runs on-device over Isar-cached data.
/// Here, ranking is delegated to the server `GET /match` endpoint, which scores
/// crops with the same weighted formula as the mobile scorer. The device fetches
/// a small, already-ranked result set instead of downloading every listing and
/// scoring it locally. Trips are paired client-side (the match endpoint returns
/// crops only).
///
/// Ethiopian context:
/// - Works over network (no offline support on web)
/// - Commission rate is 5% (platform fee), same as mobile
/// - Trip matching uses case-insensitive woreda name contains-matching
class WebMatchingService extends BaseMatchingService {
  final ApiClient _api;

  static const double commissionRate = 0.05;

  WebMatchingService({required ApiClient api}) : _api = api;

  @override
  Future<List<CropMatchResult>> matchListingsForIntent(
    BuyerIntent intent,
  ) async {
    try {
      // Server-side scoring: GET /match returns crops already ranked by the
      // shared scoring formula. Resolve the delivery woreda to coordinates
      // client-side so the server can use true haversine distance; the woreda
      // string is also sent as a proxy fallback.
      final params = <String, dynamic>{
        'cropType': intent.cropType,
        'maxPricePerKg': intent.maxPricePerKg.toString(),
        'quantityNeeded': intent.quantityNeeded.toString(),
        'deliveryWoreda': intent.deliveryWoreda,
        'limit': '50',
      };
      final destCoord = WoredaCentroids.lookup(intent.deliveryWoreda);
      if (destCoord != null) {
        params['latitude'] = destCoord.latitude.toString();
        params['longitude'] = destCoord.longitude.toString();
      }

      final data = await _api.get<Map<String, dynamic>>(
        '/match',
        queryParameters: params,
      );
      final rawMatches = data['matches'] as List<dynamic>? ?? [];
      if (rawMatches.isEmpty) return [];

      // Pair each matched crop with suggested trips (the match endpoint returns
      // crops only). Fetch open trips once for all matches.
      final allTrips = await _fetchOpenTrips();

      final matches = <CropMatchResult>[];
      for (final raw in rawMatches) {
        final map = raw as Map<String, dynamic>;
        final listing = _parseMatch(map);
        final trips = _filterTripsForRoute(
          allTrips,
          listing.locationName,
          intent.deliveryWoreda,
          intent.quantityNeeded,
        );

        // Compute estimated total with the cheapest trip if available.
        final quantityKg = intent.quantityNeeded * 100;
        final cropCost = quantityKg * listing.pricePerKg;
        final transportCost = trips.isNotEmpty
            ? intent.quantityNeeded * trips.first.pricePerQuintal
            : 0.0;
        final subtotal = cropCost + transportCost;
        final estimatedTotal = subtotal + subtotal * commissionRate;

        matches.add(
          CropMatchResult(
            listing: listing,
            suggestedTrips: trips,
            estimatedTotalMin: estimatedTotal,
            matchScore: (map['score'] as num?)?.toDouble() ?? 0.0,
            isSubstitute: map['isSubstitute'] as bool? ?? false,
          ),
        );
      }

      // The server already ranked by score; preserve that order.
      return matches;
    } on ApiException {
      // Surface transient network/auth/server failures to the caller so the
      // BLoC can emit an error state instead of silently masking the failure
      // as 'no matches' (which would also overwrite the cached match count).
      rethrow;
    }
    // Any other failure (e.g. a malformed response that breaks parsing) is
    // intentionally NOT caught here: it propagates to the BLoC as an error
    // rather than being hidden as an empty result set.
  }

  @override
  Future<void> refreshMatchCounts(
    String buyerId,
    BaseBuyerIntentRepository repository,
  ) async {
    final intents = await repository.getActiveIntents(buyerId);
    for (final intent in intents) {
      final matches = await matchListingsForIntent(intent);
      await repository.updateMatchCount(
        intent.intentId,
        matches.length,
        DateTime.now().toUtc(),
      );
    }
  }

  /// Fetch all open trips from the API, following pagination.
  ///
  /// Errors are not swallowed: they propagate so the caller can report a real
  /// failure instead of treating it as "no trips available".
  Future<List<Trip>> _fetchOpenTrips() async {
    final allTrips = <Trip>[];
    String? nextToken;
    do {
      final params = <String, dynamic>{'status': 'open', 'limit': '50'};
      if (nextToken != null) params['nextToken'] = nextToken;
      final data = await _api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: params,
      );
      final items = data['trips'] as List<dynamic>? ??
          data['items'] as List<dynamic>? ??
          [];
      allTrips.addAll(
        items.map((item) => _parseTrip(item as Map<String, dynamic>)),
      );
      nextToken = data['nextToken'] as String?;
    } while (nextToken != null);
    return allTrips;
  }

  /// Find trips for a specific listing → buyer delivery route.
  ///
  /// Fetches open trips and filters client-side for the given route.
  Future<List<Trip>> findTripsForListing(
    CropListing listing,
    String deliveryWoreda,
    double quantityNeeded,
  ) async {
    final allTrips = await _fetchOpenTrips();
    return _filterTripsForRoute(
      allTrips,
      listing.locationName,
      deliveryWoreda,
      quantityNeeded,
    );
  }

  /// Filter trips matching origin → destination route.
  ///
  /// Approximates mobile MatchingService trip filtering:
  /// - fromWoreda contains listing location (case-insensitive)
  /// - toWoreda contains delivery woreda (case-insensitive)
  /// - departureDate in the future
  /// - capacity >= quantityNeeded
  /// - Sorted by pricePerQuintal ascending
  ///
  /// NOTE: mobile applies a 0.01 float tolerance on the capacity check
  /// (`capacityGreaterThan(needed - 0.01)`); this strict `<` comparison can
  /// differ from mobile at the exact capacity == needed boundary under float
  /// representation. Acceptable for the online path (a near-exact capacity match
  /// is rare and only affects trip suggestions, not crop ranking).
  List<Trip> _filterTripsForRoute(
    List<Trip> trips,
    String fromLocation,
    String toLocation,
    double quantityNeeded,
  ) {
    final now = DateTime.now().toUtc();
    final fromLower = fromLocation.toLowerCase();
    final toLower = toLocation.toLowerCase();

    final matched = trips.where((trip) {
      if (trip.departureDate.isBefore(now)) return false;
      if (trip.capacity < quantityNeeded) return false;
      return trip.fromWoreda.toLowerCase().contains(fromLower) &&
          trip.toWoreda.toLowerCase().contains(toLower);
    }).toList();

    matched.sort(
      (a, b) => a.pricePerQuintal.compareTo(b.pricePerQuintal),
    );
    return matched;
  }

  Trip _parseTrip(Map<String, dynamic> m) {
    return Trip()
      ..tripId = m['tripId'] as String? ?? ''
      ..truckerId = m['truckerId'] as String? ?? ''
      ..truckerName = m['truckerName'] as String? ?? ''
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

  /// Parse a MatchDto (from GET /match) into a CropListing. The match endpoint
  /// returns the crop's woreda and GPS coordinates but not its full free-text
  /// description (loaded on the detail screen). Matches are always available.
  CropListing _parseMatch(Map<String, dynamic> m) {
    return CropListing()
      ..listingId = m['cropId'] as String? ?? ''
      ..farmerId = m['farmerId'] as String? ?? ''
      ..farmerName = m['farmerName'] as String? ?? ''
      ..cropType = m['cropType'] as String? ?? ''
      ..quantity = (m['quantityKg'] as num?)?.toDouble() ?? 0
      ..pricePerKg = (m['pricePerKg'] as num?)?.toDouble() ?? 0
      ..location = (LatLng()
        ..latitude = (m['latitude'] as num?)?.toDouble() ?? 0
        ..longitude = (m['longitude'] as num?)?.toDouble() ?? 0)
      ..locationName = m['woreda'] as String? ?? ''
      ..description = ''
      ..status = ListingStatus.active
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..version = 1;
  }
}
