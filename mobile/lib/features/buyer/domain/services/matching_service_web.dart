import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';

/// Web stub for MatchingService — same API, no Isar dependency.
///
/// This file is imported via conditional import (`if (dart.library.html)`)
/// from screens that create a MatchingService locally. Because it cannot
/// receive the ApiClient through constructor injection, real API matching
/// lives in [WebMatchingService] (injected via main_web.dart BLoC).
///
/// The crop_listing_detail_screen guards trip-matching calls with kIsWeb
/// and returns early, so [findTripsForListing] will not be reached on web.
class MatchingService extends BaseMatchingService {
  MatchingService({dynamic isar});

  @override
  Future<List<CropMatchResult>> matchListingsForIntent(
    BuyerIntent intent,
  ) async {
    // TODO(web): delegate to WebMatchingService once DI is available here.
    return [];
  }

  @override
  Future<void> refreshMatchCounts(
    String buyerId,
    BaseBuyerIntentRepository repository,
  ) async {
    // No-op: match counts are refreshed by WebMatchingService via BLoC.
  }

  /// Stub — trip matching is not available on web via conditional import.
  ///
  /// The calling screen (crop_listing_detail_screen) guards this call with
  /// `kIsWeb` and returns early with an empty list, so this code path is
  /// unreachable at runtime. Kept for API compatibility with mobile version.
  // TODO(web): implement API-based trip matching when server endpoint exists.
  Future<List<Trip>> findTripsForListing(
    CropListing listing,
    String deliveryWoreda,
    double quantityNeeded,
  ) async {
    return [];
  }
}
