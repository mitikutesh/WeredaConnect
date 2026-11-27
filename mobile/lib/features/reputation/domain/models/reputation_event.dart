import 'package:wereda_connect/features/reputation/domain/models/own_reputation.dart';

/// ESC-31: one row of the party's own penalty history
/// (`GET /reputation/me/events`, reputation-api.md section 4).
class ReputationEvent {
  final String eventId;

  /// A code of reputation-api.md 1.1 (`LATE_CANCEL`, `ORDER_COMPLETED`...).
  final String code;

  /// The points after the caps.
  final int points;

  /// Why a penalty code carries 0 points; null otherwise.
  final String? zeroReason;
  final int earnedBack;
  final int remainingPoints;

  /// `active`, `expired` or `excused`.
  final String status;
  final DateTime? occurredAt;
  final DateTime? pointsExpireAt;
  final ReputationAppeal appeal;

  const ReputationEvent({
    required this.eventId,
    required this.code,
    required this.points,
    required this.earnedBack,
    required this.remainingPoints,
    required this.status,
    required this.appeal,
    this.zeroReason,
    this.occurredAt,
    this.pointsExpireAt,
  });

  bool get isActive => status == 'active';

  /// This event with another appeal state (after the appeal was sent).
  ReputationEvent withAppeal(ReputationAppeal appeal) => ReputationEvent(
        eventId: eventId,
        code: code,
        points: points,
        zeroReason: zeroReason,
        earnedBack: earnedBack,
        remainingPoints: remainingPoints,
        status: status,
        occurredAt: occurredAt,
        pointsExpireAt: pointsExpireAt,
        appeal: appeal,
      );

  /// Parses one event, or null when [json] is not one (no id).
  static ReputationEvent? fromJson(Object? json) {
    if (json is! Map) return null;
    final id = json['eventId'];
    if (id is! String || id.isEmpty) return null;
    final points = _int(json['points']) ?? 0;
    final earnedBack = _int(json['earnedBack']) ?? 0;
    return ReputationEvent(
      eventId: id,
      code: json['code'] is String ? json['code'] as String : '',
      points: points,
      zeroReason:
          json['zeroReason'] is String ? json['zeroReason'] as String : null,
      earnedBack: earnedBack,
      remainingPoints: _int(json['remainingPoints']) ??
          (points - earnedBack < 0 ? 0 : points - earnedBack),
      status: json['status'] is String ? json['status'] as String : 'active',
      occurredAt: reputationDate(json['occurredAt']),
      pointsExpireAt: reputationDate(json['pointsExpireAt']),
      appeal: ReputationAppeal.fromJson(json['appeal']),
    );
  }
}

/// The appeal state of an event.
class ReputationAppeal {
  /// `none`, `pending`, `review` (an admin review of a late delivery),
  /// `excused` (accepted) or `upheld`.
  final String status;

  /// Whether the server takes an appeal now. The server decides it: the
  /// window, the status and "one appeal per event" are its rules.
  final bool canAppeal;
  final DateTime? deadlineAt;
  final DateTime? decidedAt;

  /// The admin's note to the party, after a decision.
  final String? note;

  const ReputationAppeal({
    required this.status,
    required this.canAppeal,
    this.deadlineAt,
    this.decidedAt,
    this.note,
  });

  static const none = ReputationAppeal(status: 'none', canAppeal: false);

  /// An appeal the server just took: waiting for the team.
  static const sent = ReputationAppeal(status: 'pending', canAppeal: false);

  static ReputationAppeal fromJson(Object? json) {
    if (json is! Map) return none;
    final note = json['note'];
    return ReputationAppeal(
      status: json['status'] is String ? json['status'] as String : 'none',
      canAppeal: json['canAppeal'] == true,
      deadlineAt: reputationDate(json['deadlineAt']),
      decidedAt: reputationDate(json['decidedAt']),
      note: note is String && note.trim().isNotEmpty ? note.trim() : null,
    );
  }
}

/// One page of events and the token of the next, null on the last page.
class ReputationEventsPage {
  final List<ReputationEvent> events;
  final String? nextToken;

  const ReputationEventsPage({required this.events, this.nextToken});

  static ReputationEventsPage fromJson(Map<String, dynamic> json) {
    final rows = json['events'];
    final token = json['nextToken'];
    return ReputationEventsPage(
      events: [
        if (rows is List)
          for (final row in rows)
            if (ReputationEvent.fromJson(row) case final event?) event,
      ],
      nextToken: token is String && token.isNotEmpty ? token : null,
    );
  }
}

int? _int(Object? value) => value is num ? value.toInt() : null;
