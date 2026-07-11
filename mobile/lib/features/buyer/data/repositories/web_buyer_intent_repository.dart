import 'package:uuid/uuid.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/buyer/domain/repositories/base_buyer_intent_repository.dart';
import 'package:wereda_connect/features/buyer/domain/woreda_centroids.dart';

/// Web-only buyer intent repository.
///
/// Unlike the earlier in-memory-only version, this syncs mutations to the server
/// (POST /sync, entityType 'buyer_intent') so the matching backend and the mobile
/// app see the buyer's intents — the mobile repo does the same. An in-memory cache
/// backs reads within a session.
///
/// KNOWN LIMITATION: there is no GET endpoint to list a buyer's intents, so the
/// cached list is not repopulated after a full page reload (intents created in a
/// prior session are on the server and drive matching, but are not re-listed).
/// Repopulating the list on reload requires a server GET /intents?buyerId= (or a
/// /sync pull into a web cache) — tracked as a server follow-up.
class WebBuyerIntentRepository extends BaseBuyerIntentRepository {
  static const _uuid = Uuid();
  final ApiClient? _api;
  final Map<String, BuyerIntent> _intents = {};

  WebBuyerIntentRepository({ApiClient? api}) : _api = api;

  Future<void> _syncIntent(
    String action,
    String intentId,
    Map<String, dynamic> data,
  ) async {
    final api = _api;
    if (api == null) return;
    // The in-memory write is authoritative for this session; a sync failure must
    // not surface as an operation error (matches the mobile repo's enqueue guard).
    try {
      await api.post<dynamic>(
        '/sync',
        data: {
          'syncBatchId': _uuid.v4(),
          'clientId': 'web',
          'operations': [
            {
              'operationId': _uuid.v4(),
              'entityType': 'buyer_intent',
              'action': action,
              'entityId': intentId,
              'timestamp': DateTime.now().toUtc().toIso8601String(),
              'idempotencyKey': 'web_${action}_$intentId',
              'data': data,
            },
          ],
        },
      );
    } on Object {
      // Swallow: the session cache still reflects the change.
    }
  }

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
      ..matchCount = 0
      ..version = 1;
    _intents[intentId] = intent;

    final coord = WoredaCentroids.lookup(deliveryWoreda);
    await _syncIntent('create', intentId, {
      'intentId': intentId,
      'buyerId': buyerId,
      'cropType': cropType,
      'quantityNeeded': quantityNeeded,
      'maxPricePerKg': maxPricePerKg,
      'deliveryWoreda': deliveryWoreda,
      if (coord != null) 'deliveryLatitude': coord.latitude,
      if (coord != null) 'deliveryLongitude': coord.longitude,
      if (neededByDate != null)
        'neededByDate': neededByDate.toUtc().toIso8601String(),
      'status': 'active',
      'version': 1,
    });
    return intent;
  }

  @override
  Future<List<BuyerIntent>> getActiveIntents(String buyerId) async {
    final list = _intents.values
        .where(
          (i) => i.buyerId == buyerId && i.status == BuyerIntentStatus.active,
        )
        .toList()
      // Newest first, matching the mobile repo's ordering.
      ..sort((a, b) => b.createdAt.compareTo(a.createdAt));
    return list;
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
    // Mirror the mobile repo: a fulfilled intent should stop triggering alerts, so
    // remove the server copy (delete is idempotent even if create hasn't synced).
    await _syncIntent('delete', intentId, {'buyerId': intent?.buyerId ?? ''});
  }

  @override
  Future<void> deleteIntent(String intentId) async {
    final intent = _intents.remove(intentId);
    await _syncIntent('delete', intentId, {'buyerId': intent?.buyerId ?? ''});
  }
}
