import 'package:isar/isar.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';

/// On-device matching service for buyer intents.
///
/// All operations are Isar queries — zero network dependency.
/// Matches crop listings and trips against buyer's declared needs.
///
/// Ethiopian context:
/// - Works 100% offline on cached Isar data
/// - Trip matching uses exact woreda name comparison (case-insensitive)
/// - Commission rate is 5% (platform fee)
class MatchingService extends BaseMatchingService {
  final Isar _isar;

  static const double commissionRate = 0.05;

  MatchingService({required Isar isar}) : _isar = isar;

  /// Match crop listings against a buyer intent.
  ///
  /// Finds active listings where:
  /// - cropType matches intent
  /// - pricePerKg <= maxPricePerKg
  /// - quantity >= quantityNeeded
  ///
  /// For each match, finds available trips and computes estimated total.
  /// Results sorted by price ASC (cheapest first).
  @override
  Future<List<CropMatchResult>> matchListingsForIntent(
    BuyerIntent intent,
  ) async {
    // Query matching crop listings
    final listings = await _isar.cropListings
        .filter()
        .statusEqualTo(ListingStatus.active)
        .cropTypeEqualTo(intent.cropType)
        .pricePerKgLessThan(intent.maxPricePerKg + 0.01) // inclusive <=
        .quantityGreaterThan(intent.quantityNeeded - 0.01) // inclusive >=
        .sortByPricePerKg()
        .findAll();

    final results = <CropMatchResult>[];

    for (final listing in listings) {
      // Find trips for this listing → buyer's delivery destination
      final trips = await findTripsForListing(
        listing,
        intent.deliveryWoreda,
        intent.quantityNeeded,
      );

      // Compute estimated total with cheapest trip
      final cheapestTrip = trips.isNotEmpty ? trips.first : null;
      final estimatedTotal = computeEstimatedTotal(
        listing,
        cheapestTrip,
        intent.quantityNeeded,
      );

      results.add(
        CropMatchResult(
          listing: listing,
          suggestedTrips: trips,
          estimatedTotalMin: estimatedTotal,
        ),
      );
    }

    return results;
  }

  /// Find available trips from a listing's location to the buyer's destination.
  ///
  /// Matches trips where:
  /// - status is open
  /// - fromWoreda contains listing's locationName (case-insensitive)
  /// - toWoreda contains buyer's deliveryWoreda (case-insensitive)
  /// - departureDate is in the future
  /// - capacity >= quantityNeeded
  ///
  /// Results sorted by pricePerQuintal ASC (cheapest first).
  Future<List<Trip>> findTripsForListing(
    CropListing listing,
    String deliveryWoreda,
    double quantityNeeded,
  ) async {
    final now = DateTime.now().toUtc();
    final fromLower = listing.locationName.toLowerCase();
    final toLower = deliveryWoreda.toLowerCase();

    // Isar string filter is case-sensitive, so we query all open trips
    // with future departure and sufficient capacity, then filter in memory
    // for case-insensitive woreda matching.
    final candidates = await _isar.trips
        .filter()
        .statusEqualTo(TripStatus.open)
        .departureDateGreaterThan(now)
        .capacityGreaterThan(quantityNeeded - 0.01) // inclusive >=
        .sortByPricePerQuintal()
        .findAll();

    return candidates.where((trip) {
      return trip.fromWoreda.toLowerCase().contains(fromLower) &&
          trip.toWoreda.toLowerCase().contains(toLower);
    }).toList();
  }

  /// Compute estimated total cost for a crop purchase with optional transport.
  ///
  /// Formula: (quantity_kg * pricePerKg) + (quantity_quintals * pricePerQuintal) + 5% commission
  /// If no trip is selected, only crop cost + commission.
  double computeEstimatedTotal(
    CropListing listing,
    Trip? trip,
    double quantityQuintals,
  ) {
    final quantityKg = quantityQuintals * 100;
    final cropCost = quantityKg * listing.pricePerKg;
    final transportCost =
        trip != null ? quantityQuintals * trip.pricePerQuintal : 0.0;
    final subtotal = cropCost + transportCost;
    final commission = subtotal * commissionRate;
    return subtotal + commission;
  }

  /// Refresh match counts for all active intents of a buyer.
  ///
  /// Called after sync completes to update cached matchCount values.
  /// If matchCount increased since lastViewedAt, the badge will show.
  @override
  Future<void> refreshMatchCounts(
    String buyerId,
    BaseBuyerIntentRepository repository,
  ) async {
    final intents = await repository.getActiveIntents(buyerId);
    final now = DateTime.now().toUtc();

    for (final intent in intents) {
      final matches = await _isar.cropListings
          .filter()
          .statusEqualTo(ListingStatus.active)
          .cropTypeEqualTo(intent.cropType)
          .pricePerKgLessThan(intent.maxPricePerKg + 0.01)
          .quantityGreaterThan(intent.quantityNeeded - 0.01)
          .count();

      await repository.updateMatchCount(intent.intentId, matches, now);
    }
  }
}
