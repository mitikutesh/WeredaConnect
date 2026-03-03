import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';

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

  const CropMatchResult({
    required this.listing,
    required this.suggestedTrips,
    required this.estimatedTotalMin,
  });
}
