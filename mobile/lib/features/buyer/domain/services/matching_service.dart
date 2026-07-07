import 'package:isar/isar.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';
import 'package:wereda_connect/features/buyer/domain/services/match_scorer.dart';

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

  /// Shared, platform-agnostic relevance scorer (identical math on web).
  static const MatchScorer _scorer = MatchScorer();

  MatchingService({required Isar isar}) : _isar = isar;

  /// Match crop listings against a buyer intent.
  ///
  /// Candidate generation queries active listings for the exact crop type
  /// *and* its accepted substitutes; price/quantity near-miss tolerance and
  /// relevance scoring are then applied in memory via [MatchScorer] (Isar
  /// can't express the tolerance bands cleanly). For each candidate, finds
  /// available trips and computes the estimated total.
  ///
  /// Results are ranked by relevance score DESC (best match first), with
  /// cheaper crop price breaking ties — the same tie-break as the server's
  /// GET /match (see specs/contracts/match-api.md), so online and offline
  /// rankings agree.
  @override
  Future<List<CropMatchResult>> matchListingsForIntent(
    BuyerIntent intent,
  ) async {
    final now = DateTime.now().toUtc();
    final types = _scorer.candidateCropTypes(intent.cropType);

    // Query active listings for the exact + substitute crop types. The
    // grouped `anyOf` expands to: status==active AND (type==t1 OR type==t2 …).
    final listings = await _isar.cropListings
        .filter()
        .statusEqualTo(ListingStatus.active)
        .anyOf<String, QAfterFilterCondition>(
          types,
          (q, t) => q.cropTypeEqualTo(t),
        )
        .findAll();

    final results = <CropMatchResult>[];

    for (final listing in listings) {
      // Drop listings outside the near-miss tolerance bands.
      if (!_scorer.isCandidate(intent, listing)) continue;

      // Find trips for this listing → buyer's delivery destination, honouring
      // the buyer's delivery deadline (trips that depart too late are dropped).
      final trips = await findTripsForListing(
        listing,
        intent.deliveryWoreda,
        intent.quantityNeeded,
        neededByDate: intent.neededByDate,
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
          matchScore: _scorer.scoreListing(intent, listing, now),
          isSubstitute: _scorer.isSubstitute(intent, listing),
        ),
      );
    }

    // Rank by relevance (highest score first); tie-break by cheaper crop
    // price — identical to the server ranking so results don't reorder when
    // the buyer goes online/offline.
    results.sort((a, b) {
      final byScore = b.matchScore.compareTo(a.matchScore);
      return byScore != 0
          ? byScore
          : a.listing.pricePerKg.compareTo(b.listing.pricePerKg);
    });

    return results;
  }

  /// Find available trips from a listing's location to the buyer's destination.
  ///
  /// Matches trips where:
  /// - status is open
  /// - fromWoreda contains listing's locationName (case-insensitive)
  /// - toWoreda contains buyer's deliveryWoreda (case-insensitive)
  /// - departureDate is in the future
  /// - departureDate is on/before [neededByDate] when the buyer set a deadline
  /// - capacity >= quantityNeeded
  ///
  /// Results sorted by pricePerQuintal ASC (cheapest first).
  Future<List<Trip>> findTripsForListing(
    CropListing listing,
    String deliveryWoreda,
    double quantityNeeded, {
    DateTime? neededByDate,
  }) async {
    final now = DateTime.now().toUtc();
    final fromLower = listing.locationName.toLowerCase();
    final toLower = deliveryWoreda.toLowerCase();
    final deadline = neededByDate?.toUtc();

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
      // Guard against empty needles: String.contains('') is always true,
      // which would drop the woreda filter and match every open trip.
      if (fromLower.isEmpty || toLower.isEmpty) return false;
      // Date-fit: drop trips departing after the buyer's deadline. This
      // compares DEPARTURE (not arrival) to the deadline — a proxy, since
      // trips carry no transit-time estimate; refine if one is added.
      if (deadline != null && trip.departureDate.isAfter(deadline)) {
        return false;
      }
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
  ///
  /// Counts candidates using the SAME predicate as [matchListingsForIntent]
  /// (exact + substitute types within the near-miss bands) so the badge count
  /// can never drift from what the buyer actually sees. Trip lookups are
  /// skipped here — only the crop candidate count matters for the badge.
  @override
  Future<void> refreshMatchCounts(
    String buyerId,
    BaseBuyerIntentRepository repository,
  ) async {
    final intents = await repository.getActiveIntents(buyerId);

    for (final intent in intents) {
      final types = _scorer.candidateCropTypes(intent.cropType);
      final listings = await _isar.cropListings
          .filter()
          .statusEqualTo(ListingStatus.active)
          .anyOf<String, QAfterFilterCondition>(
            types,
            (q, t) => q.cropTypeEqualTo(t),
          )
          .findAll();

      final count =
          listings.where((l) => _scorer.isCandidate(intent, l)).length;

      await repository.updateMatchCount(
        intent.intentId,
        count,
        DateTime.now().toUtc(),
      );
    }
  }
}
