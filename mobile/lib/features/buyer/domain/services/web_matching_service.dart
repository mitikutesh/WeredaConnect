import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';

/// Web matching service — queries API instead of local Isar database.
///
/// On mobile, matching uses Isar queries against cached local data.
/// On web, we fetch crop listings and trips from the API and filter
/// client-side to match the buyer's intent criteria.
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
      final data = await _api.get<Map<String, dynamic>>(
        '/crops',
        queryParameters: {
          'cropType': intent.cropType,
          'status': 'available',
        },
      );

      final items = data['crops'] as List<dynamic>? ??
          data['items'] as List<dynamic>? ??
          [];

      // Parse crop listings that match criteria
      final matchingListings = <CropListing>[];
      for (final item in items) {
        final map = item as Map<String, dynamic>;
        final listing = _parseListing(map);
        if (listing.pricePerKg > intent.maxPricePerKg) continue;
        if (listing.quantity < intent.quantityNeeded) continue;
        matchingListings.add(listing);
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
          ),
        );
      }

      // Sort by price ascending (cheapest first), matching mobile behavior
      matches.sort(
        (a, b) => a.listing.pricePerKg.compareTo(b.listing.pricePerKg),
      );
      return matches;
    } on Exception {
      return [];
    }
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

  /// Fetch all open trips from the API.
  Future<List<Trip>> _fetchOpenTrips() async {
    try {
      final data = await _api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: {'status': 'open'},
      );
      final items = data['trips'] as List<dynamic>? ??
          data['items'] as List<dynamic>? ??
          [];
      return items
          .map((item) => _parseTrip(item as Map<String, dynamic>))
          .toList();
    } on Exception {
      return [];
    }
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
