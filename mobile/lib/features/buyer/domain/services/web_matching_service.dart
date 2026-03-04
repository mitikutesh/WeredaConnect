import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_matching_service.dart';

/// Web-only matching service.
///
/// On mobile, matching uses Isar queries against cached local data.
/// On web, there is no local cache. Returns empty results for now.
/// Server-side matching endpoint can be added later.
class WebMatchingService extends BaseMatchingService {
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
  ) async {
    // No-op on web
  }
}
