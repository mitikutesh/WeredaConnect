import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';

/// Abstract base for matching services.
///
/// Mobile uses Isar queries against cached local data.
/// Web returns empty results (server-side matching not yet implemented).
abstract class BaseMatchingService {
  Future<List<CropMatchResult>> matchListingsForIntent(BuyerIntent intent);

  Future<void> refreshMatchCounts(
    String buyerId,
    BaseBuyerIntentRepository repository,
  );
}
