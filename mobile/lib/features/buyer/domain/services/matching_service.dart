import 'package:isar/isar.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order_pricing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_departure.dart';
import 'package:wereda_connect/core/sync/order_lead_store.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';
import 'package:wereda_connect/features/buyer/domain/services/match_scorer.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';

/// On-device matching service for buyer intents.
///
/// All operations are Isar queries — zero network dependency.
/// Matches crop listings and trips against buyer's declared needs.
///
/// Ethiopian context:
/// - Works 100% offline on cached Isar data and the bundled woreda
///   centroids
/// - Trip pairing is [MatchScorer.suggestTrips] (MATCH-01): the exact route
///   or a neighbouring woreda by centroid distance, the woreda names as the
///   fallback, the same rule as on web
/// - Commission rate is 5% of the crop cost (platform fee, [OrderPricing])
class MatchingService extends BaseMatchingService {
  final Isar _isar;

  /// FUP-122: the server's order lead in hours as the last trip pull cached
  /// it ([OrderLeadStore]); null when unknown.
  final Future<int?> Function() _orderLeadHours;

  final DateTime Function() _clock;

  /// Shared, platform-agnostic relevance scorer (identical math on web).
  static const MatchScorer _scorer = MatchScorer();

  MatchingService({
    required Isar isar,
    Future<int?> Function()? orderLeadHours,
    DateTime Function()? clock,
  })  : _isar = isar,
        _orderLeadHours = orderLeadHours ?? OrderLeadStore().read,
        _clock = clock ?? DateTime.now;

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
    final now = _clock().toUtc();
    final orderLeadHours = await _orderLeadHours();
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
      final trips = await _tripsForListing(
        listing,
        intent.deliveryWoreda,
        intent.quantityNeeded,
        neededByDate: intent.neededByDate,
        now: now,
        orderLeadHours: orderLeadHours,
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
  /// The cached open trips go through [MatchScorer.suggestTrips] (MATCH-01):
  /// - open, departing today or later in Ethiopia (FUP-85) and at least the
  ///   server's order lead ahead when it runs the commitments (FUP-122)
  /// - room left for [quantityNeeded] (ESC-15), so a full trip never shows
  /// - departing on or before [neededByDate] when the buyer set a deadline
  /// - starting at the listing's woreda or within 40 km of it, and ending at
  ///   the delivery woreda or within 40 km of it (centroid distance); the
  ///   woreda names decide when a centroid is unknown
  ///
  /// Cheapest first; at the same price the exact route before a neighbouring
  /// woreda, then the more reliable trucker (ESC-30), then the earlier
  /// departure and the trip id ([MatchScorer.rankTripSuggestions]).
  Future<List<Trip>> findTripsForListing(
    CropListing listing,
    String deliveryWoreda,
    double quantityNeeded, {
    DateTime? neededByDate,
  }) async {
    return _tripsForListing(
      listing,
      deliveryWoreda,
      quantityNeeded,
      neededByDate: neededByDate,
      now: _clock().toUtc(),
      orderLeadHours: await _orderLeadHours(),
    );
  }

  Future<List<Trip>> _tripsForListing(
    CropListing listing,
    String deliveryWoreda,
    double quantityNeeded, {
    required DateTime now,
    required int? orderLeadHours,
    DateTime? neededByDate,
  }) async {
    // A blank origin or destination can never name a route; bail out before
    // touching Isar (suggestTrips would reject every trip anyway).
    if (normalizeWoreda(listing.locationName).isEmpty ||
        normalizeWoreda(deliveryWoreda).isEmpty) {
      return const <Trip>[];
    }

    // The index narrows to open trips whose departure day is not over
    // (FUP-85: departureDate >= the start of today in Ethiopia, the same
    // cutoff suggestTrips applies). Isar string filters know nothing about
    // the woreda normalisation or centroids, so the route, the room and the
    // order are decided in memory by the rule web shares (MATCH-01).
    final candidates = await _isar.trips
        .filter()
        .statusEqualTo(TripStatus.open)
        .departureDateGreaterThan(
          tripListingCutoff(now).subtract(const Duration(microseconds: 1)),
        )
        .findAll();

    return _scorer.suggestTrips(
      candidates,
      listingWoreda: listing.locationName,
      deliveryWoreda: deliveryWoreda,
      quantityNeeded: quantityNeeded,
      now: now,
      orderLeadHours: orderLeadHours,
      neededByDate: neededByDate,
    );
  }

  /// Compute estimated total cost for a crop purchase with optional transport.
  ///
  /// Formula: crop (quantity_kg * pricePerKg) + transport (quantity_quintals *
  /// pricePerQuintal) + 5% commission on the crop cost only, as the server
  /// charges ([OrderPricing], SIM-53). With no trip the total is the crop
  /// cost plus commission.
  double computeEstimatedTotal(
    CropListing listing,
    Trip? trip,
    double quantityQuintals,
  ) {
    final quantityKg = quantityQuintals * 100;
    final cropCost = quantityKg * listing.pricePerKg;
    final transportCost =
        trip != null ? quantityQuintals * trip.pricePerQuintal : 0.0;
    return OrderPricing(cropCost: cropCost, transportCost: transportCost)
        .orderTotal;
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
