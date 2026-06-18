import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';
import 'package:wereda_connect/features/buyer/domain/services/match_scorer.dart';

/// Web matching service — queries API instead of local Isar database.
///
/// On mobile, matching uses Isar queries against cached local data.
/// On web, we fetch crop listings and trips from the API and filter
/// client-side to match the buyer's intent criteria.
///
/// Relevance scoring uses the SAME [MatchScorer] as mobile so ranking,
/// substitute handling, and near-miss tolerance are identical on both
/// platforms.
///
/// Ethiopian context:
/// - Works over network (no offline support on web)
/// - Commission rate is 5% (platform fee), same as mobile
/// - Trip matching uses case-insensitive woreda name contains-matching
class WebMatchingService extends BaseMatchingService {
  final ApiClient _api;

  static const double commissionRate = 0.05;

  /// Shared, platform-agnostic relevance scorer (identical math on mobile).
  static const MatchScorer _scorer = MatchScorer();

  WebMatchingService({required ApiClient api}) : _api = api;

  @override
  Future<List<CropMatchResult>> matchListingsForIntent(
    BuyerIntent intent,
  ) async {
    try {
      final now = DateTime.now().toUtc();

      // Fetch candidate crops for the exact type + accepted substitutes,
      // following pagination per type. Near-miss tolerance and relevance are
      // applied via the shared MatchScorer (identical to mobile).
      final matchingListings = <CropListing>[];
      for (final cropType in _scorer.candidateCropTypes(intent.cropType)) {
        String? nextToken;
        do {
          final params = <String, dynamic>{
            'cropType': cropType,
            'status': 'available',
            'limit': '50',
          };
          if (nextToken != null) params['nextToken'] = nextToken;
          final data = await _api.get<Map<String, dynamic>>(
            '/crops',
            queryParameters: params,
          );
          final items = data['crops'] as List<dynamic>? ??
              data['items'] as List<dynamic>? ??
              [];
          for (final item in items) {
            final listing = _parseListing(item as Map<String, dynamic>);
            if (_scorer.isCandidate(intent, listing)) {
              matchingListings.add(listing);
            }
          }
          nextToken = data['nextToken'] as String?;
        } while (nextToken != null);
      }

      // Fetch open trips once for all matches
      final allTrips = await _fetchOpenTrips();

      final matches = <CropMatchResult>[];
      for (final listing in matchingListings) {
        // Find trips: listing origin → buyer's delivery woreda
        final trips = _filterTripsForRoute(
          allTrips,
          listing.locationName,
          intent.deliveryWoreda,
          intent.quantityNeeded,
        );

        // Compute estimated total with cheapest trip if available
        final quantityKg = intent.quantityNeeded * 100;
        final cropCost = quantityKg * listing.pricePerKg;
        final transportCost = trips.isNotEmpty
            ? intent.quantityNeeded * trips.first.pricePerQuintal
            : 0.0;
        final subtotal = cropCost + transportCost;
        final commission = subtotal * commissionRate;
        final estimatedTotal = subtotal + commission;

        matches.add(
          CropMatchResult(
            listing: listing,
            suggestedTrips: trips,
            estimatedTotalMin: estimatedTotal,
            matchScore: _scorer.scoreListing(intent, listing, now),
            isSubstitute: _scorer.isSubstitute(intent, listing),
          ),
        );
      }

      // Rank by relevance (highest score first); tie-break by cheaper total,
      // matching mobile behavior.
      matches.sort((a, b) {
        final byScore = b.matchScore.compareTo(a.matchScore);
        return byScore != 0
            ? byScore
            : a.estimatedTotalMin.compareTo(b.estimatedTotalMin);
      });
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
  /// Mirrors mobile MatchingService logic:
  /// - fromWoreda contains listing location (case-insensitive)
  /// - toWoreda contains delivery woreda (case-insensitive)
  /// - departureDate in the future
  /// - capacity >= quantityNeeded
  /// - Sorted by pricePerQuintal ascending
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

  /// Parse a JSON map into a CropListing, matching the pattern used by
  /// WebCropListingRepository._parseListings.
  CropListing _parseListing(Map<String, dynamic> m) {
    return CropListing()
      // API uses "cropId"; sync-created listings use "listingId"
      ..listingId = m['listingId'] as String? ?? m['cropId'] as String? ?? ''
      ..farmerId = m['farmerId'] as String? ?? ''
      ..cropType = m['cropType'] as String? ?? ''
      // API uses "quantityKg"; sync payload uses "quantity"
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
      ..audioNoteRef = m['audioNoteRef'] as String?
      ..status = _parseListingStatus(m['status'] as String?)
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt =
          DateTime.tryParse(m['updatedAt'] as String? ?? '') ?? DateTime.now()
      ..version = (m['version'] as num?)?.toInt() ?? 1;
  }

  ListingStatus _parseListingStatus(String? s) {
    // API returns "available" which maps to ListingStatus.active
    if (s == 'available') return ListingStatus.active;
    return ListingStatus.values.firstWhere(
      (e) => e.name == s,
      orElse: () => ListingStatus.active,
    );
  }
}
