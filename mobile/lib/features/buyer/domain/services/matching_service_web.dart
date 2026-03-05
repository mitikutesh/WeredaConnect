import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';

/// Web stub for MatchingService — same API, no Isar dependency.
///
/// On web, matching returns empty results (no local database to query).
class MatchingService extends BaseMatchingService {
  MatchingService({dynamic isar});

  @override
  Future<List<CropMatchResult>> matchListingsForIntent(
    BuyerIntent intent,
  ) async {
    return [];
  }

  @override
  Future<void> refreshMatchCounts(
    String buyerId,
    BaseBuyerIntentRepository repository,
  ) async {}

  Future<List<Trip>> findTripsForListing(
    CropListing listing,
    String deliveryWoreda,
    double quantityNeeded,
  ) async {
    return [];
  }
}
