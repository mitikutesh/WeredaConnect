import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';
import 'package:wereda_connect/features/reputation/domain/models/own_reputation.dart';
import 'package:wereda_connect/features/reputation/domain/models/reputation_event.dart';

class MockReputationRepository extends Mock implements ReputationRepository {}

/// An own reputation as `GET /reputation/me` answers it
/// (reputation-api.md section 2). [rated] false leaves the rating out, as
/// the server does for a party without ratings.
OwnReputation ownReputation({
  String tier = 'good',
  int points = 0,
  bool penaltiesEnabled = true,
  bool rated = true,
  double average = 4.8,
  int count = 12,
  double deduction = 0,
  Map<String, dynamic>? restriction,
  Map<String, dynamic>? override,
  int completed = 10,
  int faultCancels = 1,
  int noShows = 1,
  String? nextPointExpiryAt,
}) =>
    OwnReputation.fromJson(<String, dynamic>{
      'userId': 'f-1',
      'role': 'farmer',
      'tier': tier,
      'activePoints': points,
      'thresholds': {'warning': 3, 'low': 6, 'restricted': 10},
      if (rated) 'ratingAverage': average,
      'ratingCount': rated ? count : 0,
      if (rated) 'ratingDeduction': deduction,
      if (rated) 'effectiveRating': average - deduction,
      'completedOrders90d': completed,
      'faultCancels90d': faultCancels,
      'noShows90d': noShows,
      if (restriction != null) 'restriction': restriction,
      if (override != null) 'override': override,
      if (nextPointExpiryAt != null) 'nextPointExpiryAt': nextPointExpiryAt,
      'penaltiesEnabled': penaltiesEnabled,
      'asOf': '2026-11-07T09:00:00.000Z',
    })!;

OwnReputationSnapshot snapshot(
  OwnReputation reputation, {
  bool stale = false,
  DateTime? fetchedAt,
}) =>
    OwnReputationSnapshot(
      reputation: reputation,
      fetchedAt: fetchedAt ?? DateTime.utc(2026, 11, 7, 9),
      stale: stale,
    );

/// An event as `GET /reputation/me/events` answers it (section 4).
ReputationEvent reputationEvent(
  String id, {
  String code = 'LATE_CANCEL',
  int points = 4,
  int earnedBack = 0,
  String status = 'active',
  String appealStatus = 'none',
  bool canAppeal = false,
  String? note,
  String? zeroReason,
}) =>
    ReputationEvent.fromJson(<String, dynamic>{
      'eventId': id,
      'code': code,
      'points': points,
      if (zeroReason != null) 'zeroReason': zeroReason,
      'earnedBack': earnedBack,
      'remainingPoints': status == 'active' ? points - earnedBack : 0,
      'status': status,
      'occurredAt': '2026-11-01T06:00:00.000Z',
      'pointsExpireAt': '2027-01-30T06:00:00.000Z',
      'appeal': {
        'status': appealStatus,
        'canAppeal': canAppeal,
        if (canAppeal) 'deadlineAt': '2026-11-08T06:00:04.000Z',
        if (note != null) 'note': note,
      },
    })!;

ReputationEventsResult eventsResult(
  List<ReputationEvent> events, {
  String? nextToken,
  bool stale = false,
}) =>
    ReputationEventsResult(
      page: ReputationEventsPage(events: events, nextToken: nextToken),
      stale: stale,
    );
