import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';

/// Offline-first repository for buyer intent CRUD operations.
///
/// BuyerIntent is local-only for MVP — no server sync needed.
/// All reads and writes go directly to Isar.
class BuyerIntentRepository extends BaseBuyerIntentRepository {
  final Isar _isar;
  static const _uuid = Uuid();

  BuyerIntentRepository({required Isar isar}) : _isar = isar;

  /// Create a new buyer intent.
  @override
  Future<BuyerIntent> createIntent({
    required String buyerId,
    required String cropType,
    required double quantityNeeded,
    required double maxPricePerKg,
    required String deliveryWoreda,
  }) async {
    final intentId = _uuid.v4();
    final now = DateTime.now().toUtc();

    final intent = BuyerIntent()
      ..intentId = intentId
      ..buyerId = buyerId
      ..cropType = cropType
      ..quantityNeeded = quantityNeeded
      ..maxPricePerKg = maxPricePerKg
      ..deliveryWoreda = deliveryWoreda
      ..status = BuyerIntentStatus.active
      ..createdAt = now
      ..updatedAt = now
      ..matchCount = 0
      ..version = 1;

    await _isar.writeTxn(() async {
      await _isar.buyerIntents.put(intent);
    });

    return intent;
  }

  /// Get all active intents for a buyer, sorted by most recent first.
  @override
  Future<List<BuyerIntent>> getActiveIntents(String buyerId) async {
    return _isar.buyerIntents
        .filter()
        .buyerIdEqualTo(buyerId)
        .statusEqualTo(BuyerIntentStatus.active)
        .sortByCreatedAtDesc()
        .findAll();
  }

  /// Get a single intent by its intentId.
  @override
  Future<BuyerIntent?> getIntentById(String intentId) async {
    return _isar.buyerIntents.filter().intentIdEqualTo(intentId).findFirst();
  }

  /// Update the cached match count and last matched timestamp.
  @override
  Future<void> updateMatchCount(
    String intentId,
    int count,
    DateTime lastMatchedAt,
  ) async {
    final intent =
        await _isar.buyerIntents.filter().intentIdEqualTo(intentId).findFirst();

    if (intent == null) return;

    await _isar.writeTxn(() async {
      intent.matchCount = count;
      intent.lastMatchedAt = lastMatchedAt;
      intent.updatedAt = DateTime.now().toUtc();
      await _isar.buyerIntents.put(intent);
    });
  }

  /// Mark an intent's match results as viewed (clears "new matches" badge).
  ///
  /// Snapshots the current [BuyerIntent.matchCount] into
  /// [BuyerIntent.lastSeenMatchCount] so the badge reflects only matches that
  /// appear *after* this view.
  @override
  Future<void> updateLastViewed(String intentId) async {
    final intent =
        await _isar.buyerIntents.filter().intentIdEqualTo(intentId).findFirst();

    if (intent == null) return;

    await _isar.writeTxn(() async {
      intent.lastViewedAt = DateTime.now().toUtc();
      intent.lastSeenMatchCount = intent.matchCount;
      intent.updatedAt = intent.lastViewedAt!;
      await _isar.buyerIntents.put(intent);
    });
  }

  /// Mark an intent as fulfilled (buyer placed an order).
  @override
  Future<void> fulfillIntent(String intentId) async {
    final intent =
        await _isar.buyerIntents.filter().intentIdEqualTo(intentId).findFirst();

    if (intent == null) return;

    await _isar.writeTxn(() async {
      intent.status = BuyerIntentStatus.fulfilled;
      intent.updatedAt = DateTime.now().toUtc();
      intent.version += 1;
      await _isar.buyerIntents.put(intent);
    });
  }

  /// Delete an intent.
  @override
  Future<void> deleteIntent(String intentId) async {
    final intent =
        await _isar.buyerIntents.filter().intentIdEqualTo(intentId).findFirst();

    if (intent == null) return;

    await _isar.writeTxn(() async {
      await _isar.buyerIntents.delete(intent.id);
    });
  }
}
