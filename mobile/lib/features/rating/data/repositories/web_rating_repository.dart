import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/rating.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';

/// Web-only rating repository. All operations use HTTP via [ApiClient];
/// no Isar dependency.
class WebRatingRepository extends BaseRatingRepository {
  static const _uuid = Uuid();
  final ApiClient _api;

  WebRatingRepository({required ApiClient api}) : _api = api;

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
    final now = DateTime.now().toUtc().toIso8601String();
    final trimmedComment = comment?.trim();

    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'rating',
            'action': 'create',
            'entityId': ratingId,
            'timestamp': now,
            'idempotencyKey': 'web_rating_$ratingId',
            'data': {
              'ratingId': ratingId,
              'orderId': orderId,
              'raterId': raterId,
              'rateeId': rateeId,
              'rateeRole': rateeRole,
              'stars': stars,
              if (trimmedComment != null && trimmedComment.isNotEmpty)
                'comment': trimmedComment,
              'version': 1,
            },
          },
        ],
      },
    );
    ensureSyncSucceeded(response);

    return Rating()
      ..ratingId = ratingId
      ..orderId = orderId
      ..raterId = raterId
      ..rateeId = rateeId
      ..rateeRole = rateeRole
      ..stars = stars
      ..comment = (trimmedComment != null && trimmedComment.isNotEmpty)
          ? trimmedComment
          : null
      ..createdAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc()
      ..version = 1;
  }

  @override
  Future<bool> hasRated({
    required String orderId,
    required String rateeId,
  }) async {
    try {
      final data = await _api.get<Map<String, dynamic>>(
        '/ratings/${ratingIdFor(orderId, rateeId)}',
      );
      return data['rating'] != null;
    } on Object {
      // 404 / offline → treat as not rated (create is idempotent server-side).
      return false;
    }
  }

  @override
  Future<List<Rating>> getRatingsForRatee(String rateeId) async {
    try {
      final data = await _api.get<Map<String, dynamic>>(
        '/ratings',
        queryParameters: {'rateeId': rateeId},
      );
      final list = (data['ratings'] as List?) ?? const [];
      return list.map((e) => _parseRating(e as Map<String, dynamic>)).toList();
    } on Object {
      return const [];
    }
  }

  @override
  Future<RatingSummary> getRatingSummary(String rateeId) async {
    try {
      final data = await _api.get<Map<String, dynamic>>(
        '/ratings',
        queryParameters: {'rateeId': rateeId},
      );
      final summary = data['summary'] as Map<String, dynamic>?;
      if (summary == null) return RatingSummary.empty;
      return RatingSummary(
        average: (summary['average'] as num?)?.toDouble() ?? 0,
        count: (summary['count'] as num?)?.toInt() ?? 0,
      );
    } on Object {
      return RatingSummary.empty;
    }
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
