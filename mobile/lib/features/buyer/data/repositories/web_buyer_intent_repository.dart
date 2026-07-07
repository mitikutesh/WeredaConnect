import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';

/// Web-only buyer intent repository.
///
/// Buyer intents are local-only in the mobile app (no server sync).
/// On web, we store them in-memory since there is no Isar.
/// This is sufficient because the web app always has connectivity.
class WebBuyerIntentRepository extends BaseBuyerIntentRepository {
  final Map<String, BuyerIntent> _intents = {};

  @override
  Future<BuyerIntent> createIntent({
    required String buyerId,
    required String cropType,
    required double quantityNeeded,
    required double maxPricePerKg,
    required String deliveryWoreda,
    String? placeId,
    DateTime? neededByDate,
  }) async {
    final intentId =
        'int_${DateTime.now().millisecondsSinceEpoch}_${buyerId.hashCode.abs()}';
    final now = DateTime.now().toUtc();
    final intent = BuyerIntent()
      ..intentId = intentId
      ..buyerId = buyerId
      ..cropType = cropType
      ..quantityNeeded = quantityNeeded
      ..maxPricePerKg = maxPricePerKg
      ..deliveryWoreda = deliveryWoreda
      ..placeId = placeId
      ..neededByDate = neededByDate
      ..status = BuyerIntentStatus.active
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;
    _intents[intentId] = intent;
    return intent;
  }

  @override
  Future<List<BuyerIntent>> getActiveIntents(String buyerId) async {
    return _intents.values
        .where(
          (i) => i.buyerId == buyerId && i.status == BuyerIntentStatus.active,
        )
        .toList();
  }

  @override
  Future<BuyerIntent?> getIntentById(String intentId) async {
    return _intents[intentId];
  }

  @override
  Future<void> updateMatchCount(
    String intentId,
    int count,
    DateTime lastMatchedAt,
  ) async {
    final intent = _intents[intentId];
    if (intent != null) {
      intent
        ..matchCount = count
        ..lastMatchedAt = lastMatchedAt;
    }
  }

  @override
  Future<void> updateLastViewed(String intentId) async {
    final intent = _intents[intentId];
    if (intent != null) {
      intent
        ..lastViewedAt = DateTime.now().toUtc()
        ..lastSeenMatchCount = intent.matchCount;
    }
  }

  @override
  Future<void> fulfillIntent(String intentId) async {
    final intent = _intents[intentId];
    if (intent != null) {
      intent
        ..status = BuyerIntentStatus.fulfilled
        ..updatedAt = DateTime.now().toUtc();
    }
  }

  @override
  Future<void> deleteIntent(String intentId) async {
    _intents.remove(intentId);
  }
}
