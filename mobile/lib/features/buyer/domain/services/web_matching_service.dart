import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';

/// Web matching service — queries API instead of local Isar database.
///
/// On mobile, matching uses Isar queries against cached local data.
/// On web, we fetch crop listings from the API and filter client-side
/// to match the buyer's intent criteria (crop type, price, quantity).
///
/// Ethiopian context:
/// - Works over network (no offline support on web)
/// - Commission rate is 5% (platform fee), same as mobile
/// - Trip matching not yet available via API (returns empty trips)
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

      final matches = <CropMatchResult>[];
      for (final item in items) {
        final map = item as Map<String, dynamic>;
        final listing = _parseListing(map);

        // Filter: price within buyer's budget
        if (listing.pricePerKg > intent.maxPricePerKg) continue;

        // Filter: sufficient quantity
        if (listing.quantity < intent.quantityNeeded) continue;

        // Compute estimated total (crop cost + 5% commission, no trip yet)
        final quantityKg = intent.quantityNeeded * 100;
        final cropCost = quantityKg * listing.pricePerKg;
        final commission = cropCost * commissionRate;
        final estimatedTotal = cropCost + commission;

        matches.add(
          CropMatchResult(
            listing: listing,
            suggestedTrips: const [],
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
