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
    final targetId = ratingIdFor(orderId, rateeId);
    // Ratings are immutable (one per order+ratee), so a rating made on another
    // device must gate the button here too. Check the server by the EXACT id via
    // GET /ratings/{id} (cheap, authoritative) — mirrors the web repo. A paged
    // rateeId list scan could miss the rating past page 1. Fall back to the local
    // store when offline (create is idempotent server-side either way).
    final api = _apiClient;
    if (api != null) {
      try {
        final data = await api.get<Map<String, dynamic>>('/ratings/$targetId');
        if (data['rating'] != null) return true;
      } on Object catch (e) {
        // 404 → not rated; network error → fall through to the local check.
        debugPrint('[RatingRepository] hasRated server check: $e');
      }
    }
    final existing =
        await _isar.ratings.filter().ratingIdEqualTo(targetId).findFirst();
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
    // No server data (offline / fetch failed): the public aggregate is NOT
    // derivable locally — Isar only holds ratings authored on THIS device, so
    // averaging them would present the current user's own rating as the ratee's
    // public score. Return empty rather than a misleading aggregate (the UI shows
    // "no ratings yet / unavailable"). The server is the source of truth.
    return RatingSummary.empty;
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
