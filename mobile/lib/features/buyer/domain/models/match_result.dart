import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';

/// Result of matching a buyer intent against crop listings and trips.
///
/// Each result pairs a matching crop listing with suggested transport
/// options and an estimated total cost (crop + cheapest transport + commission).
class CropMatchResult {
  /// The matching crop listing
  final CropListing listing;

  /// Available trips from listing origin to buyer's delivery woreda
  final List<Trip> suggestedTrips;

  /// Cheapest estimated total (crop + cheapest trip + 5% commission)
  /// Null if no trips available (only crop cost + commission)
  final double estimatedTotalMin;

  /// Relevance score in [0, 1] from `MatchScorer` (higher = better match).
  /// Results are ranked by this, not by raw price.
  final double matchScore;

  /// True when this crop matched via a substitute type rather than the exact
  /// crop the buyer asked for. UI may label it as a "similar crop".
  final bool isSubstitute;

  const CropMatchResult({
    required this.listing,
    required this.suggestedTrips,
    required this.estimatedTotalMin,
    this.matchScore = 0,
    this.isSubstitute = false,
  });
}
