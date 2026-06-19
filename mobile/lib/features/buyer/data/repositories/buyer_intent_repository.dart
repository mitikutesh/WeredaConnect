import 'dart:convert';

import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/woreda_centroids.dart';

/// Offline-first repository for buyer intent CRUD operations.
///
/// Intents are stored locally in Isar (the on-device matcher reads them) AND,
/// when a [SyncQueue] is provided, synced to the server as `buyer_intent`
/// operations so the backend can match new crop listings against them for push
/// alerts. Sync is additive — local writes never depend on it.
class BuyerIntentRepository extends BaseBuyerIntentRepository {
  final Isar _isar;
  final SyncQueue? _syncQueue;
  static const _uuid = Uuid();

  BuyerIntentRepository({required Isar isar, SyncQueue? syncQueue})
      : _isar = isar,
        _syncQueue = syncQueue;

  /// Enqueue a `buyer_intent` sync operation (no-op when there's no queue, e.g.
  /// in tests or environments without server intent sync).
  Future<void> _enqueueIntent(
    String action,
    String intentId,
    Map<String, dynamic> data,
  ) async {
    final queue = _syncQueue;
    if (queue == null) return;
    await queue.enqueue(
      userId: data['buyerId'] as String,
      entityType: 'buyer_intent',
      entityId: intentId,
      action: action,
      data: jsonEncode(data),
      operationId: _uuid.v4(),
    );
  }

  /// Create a new buyer intent.
  @override
  Future<BuyerIntent> createIntent({
    required String buyerId,
    required String cropType,
    required double quantityNeeded,
    required double maxPricePerKg,
    required String deliveryWoreda,
    String? placeId,
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
      ..placeId = placeId
      ..status = BuyerIntentStatus.active
      ..createdAt = now
      ..updatedAt = now
      ..matchCount = 0
      ..version = 1;

    await _isar.writeTxn(() async {
      await _isar.buyerIntents.put(intent);
    });

    // Sync to server so the backend can match new crops against this intent.
    // Resolve the delivery woreda to coordinates client-side (single source of
    // centroid data) so the server can score geo-distance.
    final coord = WoredaCentroids.lookup(deliveryWoreda);
    await _enqueueIntent('create', intentId, {
      'intentId': intentId,
      'buyerId': buyerId,
      'cropType': cropType,
      'quantityNeeded': quantityNeeded,
      'maxPricePerKg': maxPricePerKg,
      'deliveryWoreda': deliveryWoreda,
      if (coord != null) 'deliveryLatitude': coord.latitude,
      if (coord != null) 'deliveryLongitude': coord.longitude,
      'status': 'active',
      'version': 1,
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

    // A fulfilled intent should no longer trigger alerts — remove the server
    // copy (the local record is kept for the buyer's history). We deliberately
    // enqueue a `delete` rather than an `update status=fulfilled`: delete is
    // idempotent (a no-op if the create op hasn't synced yet), whereas an update
    // would 404 against a not-yet-created server row. The app has no intent-edit
    // path, so the server row is never needed again once fulfilled.
    await _enqueueIntent('delete', intent.intentId, {'buyerId': intent.buyerId});
  }

  /// Delete an intent.
  @override
  Future<void> deleteIntent(String intentId) async {
    final intent =
        await _isar.buyerIntents.filter().intentIdEqualTo(intentId).findFirst();

    if (intent == null) return;

    final buyerId = intent.buyerId;
    await _isar.writeTxn(() async {
      await _isar.buyerIntents.delete(intent.id);
    });

    await _enqueueIntent('delete', intentId, {'buyerId': buyerId});
  }
}
