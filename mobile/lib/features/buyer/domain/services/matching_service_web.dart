import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';
import 'package:wereda_connect/features/buyer/domain/services/web_matching_service.dart';

/// Web stub for MatchingService — same API, no Isar dependency.
///
/// This file is imported via conditional import (`if (dart.library.html)`)
/// from screens that create a MatchingService locally. Delegates trip
/// matching to [WebMatchingService] using the ApiClient singleton.
class MatchingService extends BaseMatchingService {
  MatchingService({dynamic isar});

  @override
  Future<List<CropMatchResult>> matchListingsForIntent(
    BuyerIntent intent,
  ) async {
    // Delegate to WebMatchingService via ApiClient singleton.
    final webService = WebMatchingService(api: ApiClient());
    return webService.matchListingsForIntent(intent);
  }

  @override
  Future<void> refreshMatchCounts(
    String buyerId,
    BaseBuyerIntentRepository repository,
  ) async {
    // No-op: match counts are refreshed by WebMatchingService via BLoC.
  }

  /// Find trips from listing origin to buyer's delivery woreda via API.
  Future<List<Trip>> findTripsForListing(
    CropListing listing,
    String deliveryWoreda,
    double quantityNeeded,
  ) async {
    final webService = WebMatchingService(api: ApiClient());
    return webService.findTripsForListing(
      listing,
      deliveryWoreda,
      quantityNeeded,
    );
  }
}
