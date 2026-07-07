import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/buyer/domain/models/match_result.dart';
import 'package:wereda_connect/features/buyer/domain/services/base_match_event_logger.dart';

/// Append-only match-interaction telemetry (see specs/contracts/match-events.md).
///
/// Logs which matches were SHOWN to a buyer, which they OPENED, and which
/// converted to an ORDER — with the score/rank the scorer assigned at the
/// time. These logs are the training corpus that makes a learned ranker
/// possible later; without them every future ranking improvement starts from
/// zero data.
///
/// Events flow through the offline [SyncQueue] as `match_event` operations,
/// so logging works offline and rides the existing sync batching. Telemetry
/// must NEVER break the marketplace flow: every method swallows failures
/// (queue full, duplicate) after a debug log, and when no queue is available
/// (tests) every call is a silent no-op.
///
/// Mobile-only: imports the Isar-backed [SyncQueue]. The shared BLoC depends on
/// [BaseMatchEventLogger] instead, so this file never enters the web build.
class MatchEventLogger implements BaseMatchEventLogger {
  final SyncQueue? _syncQueue;
  static const _uuid = Uuid();

  /// Impressions past this rank are not logged — the tail of a long results
  /// list is noise for ranking purposes and payload on a 2G uplink.
  static const int maxLoggedImpressions = 20;

  /// Identifies the ranking policy that produced the logged scores, so future
  /// training can separate data by policy version.
  static const String policyTag = 'weighted-v1';

  MatchEventLogger({SyncQueue? syncQueue}) : _syncQueue = syncQueue;

  /// Log that [results] (top [maxLoggedImpressions]) were shown for [intent].
  /// One event per view, with the impression list as a compact JSON string.
  @override
  Future<void> logShown(
    BuyerIntent intent,
    List<CropMatchResult> results,
  ) async {
    final impressions = <Map<String, Object>>[
      for (var rank = 0;
          rank < results.length && rank < maxLoggedImpressions;
          rank++)
        {
          'listingId': results[rank].listing.listingId,
          'score': results[rank].matchScore,
          'rank': rank,
          'isSubstitute': results[rank].isSubstitute,
        },
    ];
    await _enqueue(intent, {
      'eventType': 'shown',
      'matchCount': results.length,
      'impressions': jsonEncode(impressions),
    });
  }

  /// Log that the buyer opened the match at [rank] (0-based display position).
  @override
  Future<void> logOpened(
    BuyerIntent intent,
    CropMatchResult result,
    int rank,
  ) async {
    await _enqueue(intent, {
      'eventType': 'opened',
      'listingId': result.listing.listingId,
      'score': result.matchScore,
      'rank': rank,
      'isSubstitute': result.isSubstitute,
    });
  }

  /// Log that an order was created for [listingId] from this intent's matches
  /// — the conversion signal a learned ranker optimises for.
  @override
  Future<void> logOrderCreated(BuyerIntent intent, String listingId) async {
    await _enqueue(intent, {
      'eventType': 'order_created',
      'listingId': listingId,
    });
  }

  Future<void> _enqueue(BuyerIntent intent, Map<String, Object> fields) async {
    final queue = _syncQueue;
    if (queue == null) return;
    final eventId = _uuid.v4();
    try {
      await queue.enqueue(
        userId: intent.buyerId,
        entityType: 'match_event',
        entityId: eventId,
        action: 'create',
        data: jsonEncode({
          'eventId': eventId,
          'intentId': intent.intentId,
          'buyerId': intent.buyerId,
          'occurredAt': DateTime.now().toUtc().toIso8601String(),
          'policyTag': policyTag,
          ...fields,
        }),
        operationId: _uuid.v4(),
      );
    } on Object catch (e) {
      // Telemetry is strictly best-effort — a full queue or duplicate must
      // never surface to the buyer or block matching/ordering.
      debugPrint('[MatchEventLogger] dropped ${fields['eventType']} event: $e');
    }
  }
}
