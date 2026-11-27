import 'package:wereda_connect/core/models/buyer_intent.dart';

/// Abstract base for buyer intent repositories.
///
/// Extracted from BuyerIntentRepository so both mobile (Isar) and
/// web (HTTP-only) implementations can share the same BLoC interface.
abstract class BaseBuyerIntentRepository {
  Future<BuyerIntent> createIntent({
    required String buyerId,
    required String cropType,
    required double quantityNeeded,
    required double maxPricePerKg,
    required String deliveryWoreda,
    String? placeId,
    DateTime? neededByDate,
  });

  Future<List<BuyerIntent>> getActiveIntents(String buyerId);

  /// SIM-56: brings back the buyer's own requests the server holds
  /// (`GET /intents`) that this device does not, so My Requests is not empty
  /// after a web reload or on a new phone. Never removes or overwrites what
  /// the device has (its own changes win). True when it added any. Throws
  /// when the read fails (offline, an old server); callers treat it as best
  /// effort and go on with what the device has. False, with no request,
  /// without an API client (tests).
  Future<bool> refreshFromServer(String buyerId);

  Future<BuyerIntent?> getIntentById(String intentId);

  Future<void> updateMatchCount(
    String intentId,
    int count,
    DateTime lastMatchedAt,
  );

  Future<void> updateLastViewed(String intentId);

  Future<void> fulfillIntent(String intentId);

  Future<void> deleteIntent(String intentId);
}
