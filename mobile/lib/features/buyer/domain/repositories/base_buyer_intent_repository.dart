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
  });

  Future<List<BuyerIntent>> getActiveIntents(String buyerId);

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
