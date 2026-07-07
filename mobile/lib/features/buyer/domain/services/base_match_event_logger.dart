import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';

/// Web-safe interface for match-interaction telemetry.
///
/// The shared [BuyerIntentBloc] depends on THIS, not the concrete
/// [MatchEventLogger] — which imports the Isar-backed `SyncQueue` and so must
/// stay out of the web build (web's `SyncQueue` stub has a different enqueue
/// signature; see CLAUDE.md platform pairs). On web no logger is injected, so
/// telemetry is simply absent there.
abstract class BaseMatchEventLogger {
  /// Log that [results] were shown to the buyer for [intent].
  Future<void> logShown(BuyerIntent intent, List<CropMatchResult> results);

  /// Log that the buyer opened the match at [rank] (0-based display position).
  Future<void> logOpened(BuyerIntent intent, CropMatchResult result, int rank);

  /// Log that an order was created for [listingId] from this intent's matches.
  Future<void> logOrderCreated(BuyerIntent intent, String listingId);
}
