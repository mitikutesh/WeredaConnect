import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/rating.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';

/// Offline-first repository for ratings.
///
/// Writes go to local Isar first (offline-first), then enqueue a `rating`
/// sync operation. Aggregate reads prefer the server (which holds ratings from
/// ALL raters) and fall back to the local store when offline.
class RatingRepository extends BaseRatingRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;

  /// Optional API client for server-side aggregates. Null in tests / pure
  /// offline setups (aggregate reads then fall back to the local store).
  final ApiClient? _apiClient;

  static const _uuid = Uuid();

  RatingRepository({
    required Isar isar,
    required SyncQueue syncQueue,
    ApiClient? apiClient,
  })  : _isar = isar,
        _syncQueue = syncQueue,
        _apiClient = apiClient;

  /// Deterministic rating id — one rating per (order, ratee).
  static String ratingIdFor(String orderId, String rateeId) =>
      '$orderId:$rateeId';

  @override
  Future<Rating> createRating({
    required String orderId,
    required String raterId,
    required String rateeId,
    required String rateeRole,
    required int stars,
    String? comment,
  }) async {
    final ratingId = ratingIdFor(orderId, rateeId);
    final now = DateTime.now().toUtc();
    final trimmedComment = comment?.trim();

    final rating = Rating()
      ..ratingId = ratingId
      ..orderId = orderId
      ..raterId = raterId
      ..rateeId = rateeId
      ..rateeRole = rateeRole
      ..stars = stars
      ..comment = (trimmedComment != null && trimmedComment.isNotEmpty)
          ? trimmedComment
          : null
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;

    await _isar.writeTxn(() async {
      // Upsert by the unique ratingId (re-rating overwrites locally; the server
      // is idempotent via attribute_not_exists).
      final existing =
          await _isar.ratings.filter().ratingIdEqualTo(ratingId).findFirst();
      if (existing != null) rating.id = existing.id;
      await _isar.ratings.put(rating);
    });

    // The local write is authoritative (offline-first); a failed enqueue must
    // not surface as a save failure — the sync layer reconciles later.
    try {
      await _syncQueue.enqueue(
        userId: raterId,
        entityType: 'rating',
        entityId: ratingId,
        action: 'create',
        data: jsonEncode({
          'ratingId': ratingId,
          'orderId': orderId,
          'raterId': raterId,
          'rateeId': rateeId,
          'rateeRole': rateeRole,
          'stars': stars,
          if (rating.comment != null) 'comment': rating.comment,
          'version': 1,
        }),
        operationId: _uuid.v4(),
      );
    } on Object catch (e) {
      debugPrint(
        '[RatingRepository] rating saved locally but sync enqueue failed: $e',
      );
    }

    return rating;
  }

  @override
  Future<bool> hasRated({
    required String orderId,
    required String rateeId,
  }) async {
    final existing = await _isar.ratings
        .filter()
        .ratingIdEqualTo(ratingIdFor(orderId, rateeId))
        .findFirst();
    return existing != null;
  }

  @override
  Future<List<Rating>> getRatingsForRatee(String rateeId) async {
    final api = _apiClient;
    if (api != null) {
      try {
        final data = await api.get<Map<String, dynamic>>(
          '/ratings',
          queryParameters: {'rateeId': rateeId},
        );
        final list = (data['ratings'] as List?) ?? const [];
        return list
            .map((e) => _parseRating(e as Map<String, dynamic>))
            .toList();
      } on Object catch (e) {
        debugPrint('[RatingRepository] getRatingsForRatee server fetch: $e');
      }
    }
    return _isar.ratings
        .filter()
        .rateeIdEqualTo(rateeId)
        .sortByCreatedAtDesc()
        .findAll();
  }

  @override
  Future<RatingSummary> getRatingSummary(String rateeId) async {
    final api = _apiClient;
    if (api != null) {
      try {
        final data = await api.get<Map<String, dynamic>>(
          '/ratings',
          queryParameters: {'rateeId': rateeId},
        );
        final summary = data['summary'] as Map<String, dynamic>?;
        if (summary != null) {
          return RatingSummary(
            average: (summary['average'] as num?)?.toDouble() ?? 0,
            count: (summary['count'] as num?)?.toInt() ?? 0,
          );
        }
      } on Object catch (e) {
        debugPrint('[RatingRepository] getRatingSummary server fetch: $e');
      }
    }
    // Local fallback (incomplete — only ratings authored on this device).
    final local =
        await _isar.ratings.filter().rateeIdEqualTo(rateeId).findAll();
    if (local.isEmpty) return RatingSummary.empty;
    final total = local.fold<int>(0, (sum, r) => sum + r.stars);
    return RatingSummary(average: total / local.length, count: local.length);
  }

  Rating _parseRating(Map<String, dynamic> m) {
    return Rating()
      ..ratingId = m['ratingId'] as String? ?? ''
      ..orderId = m['orderId'] as String? ?? ''
      ..raterId = m['raterId'] as String? ?? ''
      ..rateeId = m['rateeId'] as String? ?? ''
      ..rateeRole = m['rateeRole'] as String? ?? ''
      ..stars = (m['stars'] as num?)?.toInt() ?? 0
      ..comment = m['comment'] as String?
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt =
          DateTime.tryParse(m['updatedAt'] as String? ?? '') ?? DateTime.now()
      ..version = (m['version'] as num?)?.toInt() ?? 1;
  }
}
