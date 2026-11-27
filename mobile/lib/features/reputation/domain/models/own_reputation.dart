/// ESC-31: a farmer's or trucker's own reputation, the answer of
/// `GET /reputation/me` (reputation-api.md section 2). Only ever the
/// signed-in user's own: the server refuses anyone else's, and a buyer has
/// none.
///
/// The server leaves out a field without a value, so everything that can be
/// missing is nullable or has a neutral default; a bad value never fails
/// the whole answer.
class OwnReputation {
  /// The internal tier: `good`, `warning`, `low` or `restricted`.
  final String tier;
  final int activePoints;

  /// The live policy thresholds; null when the server sent none.
  final int? warningThreshold;
  final int? lowThreshold;
  final int? restrictedThreshold;

  /// The buyers' star average; null with no ratings.
  final double? ratingAverage;
  final int ratingCount;

  /// What the penalties take off the average; null with no ratings.
  final double? ratingDeduction;

  /// What buyers see; null with no ratings.
  final double? effectiveRating;

  /// Whole percent; null below 3 orders.
  final int? reliabilityPct;
  final int completedOrders90d;
  final int faultCancels90d;
  final int noShows90d;

  /// Present while restricted.
  final ReputationRestriction? restriction;

  /// Present while an admin tag is in force.
  final ReputationOverride? override;
  final DateTime? nextPointExpiryAt;

  /// The server's `reputationPenalties` flag. While it is off the app hides
  /// the points, the history and the appeal: only the rating is shown.
  final bool penaltiesEnabled;

  /// Server time of the answer.
  final DateTime? asOf;

  const OwnReputation({
    required this.tier,
    required this.activePoints,
    required this.ratingCount,
    required this.completedOrders90d,
    required this.faultCancels90d,
    required this.noShows90d,
    required this.penaltiesEnabled,
    this.warningThreshold,
    this.lowThreshold,
    this.restrictedThreshold,
    this.ratingAverage,
    this.ratingDeduction,
    this.effectiveRating,
    this.reliabilityPct,
    this.restriction,
    this.override,
    this.nextPointExpiryAt,
    this.asOf,
  });

  /// Completed orders plus the commitments that were broken, the base of
  /// "{completed} of {total} orders completed".
  int get totalOrders90d => completedOrders90d + faultCancels90d + noShows90d;

  /// Whether there are ratings to show.
  bool get hasRating => ratingCount > 0 && ratingAverage != null;

  /// Parses the `reputation` object of the answer, or null when [json] is
  /// not one.
  static OwnReputation? fromJson(Object? json) {
    if (json is! Map) return null;
    final thresholds = json['thresholds'];
    final restriction = json['restriction'];
    final override = json['override'];
    final tier = json['tier'];
    return OwnReputation(
      tier: tier is String && tier.isNotEmpty ? tier : 'good',
      activePoints: _int(json['activePoints']) ?? 0,
      warningThreshold: thresholds is Map ? _int(thresholds['warning']) : null,
      lowThreshold: thresholds is Map ? _int(thresholds['low']) : null,
      restrictedThreshold:
          thresholds is Map ? _int(thresholds['restricted']) : null,
      ratingAverage: _double(json['ratingAverage']),
      ratingCount: _int(json['ratingCount']) ?? 0,
      ratingDeduction: _double(json['ratingDeduction']),
      effectiveRating: _double(json['effectiveRating']),
      reliabilityPct: _int(json['reliabilityPct']),
      completedOrders90d: _int(json['completedOrders90d']) ?? 0,
      faultCancels90d: _int(json['faultCancels90d']) ?? 0,
      noShows90d: _int(json['noShows90d']) ?? 0,
      restriction: restriction is Map
          ? ReputationRestriction(
              since: reputationDate(restriction['since']),
              until: reputationDate(restriction['until']),
              cause: restriction['cause'] is String
                  ? restriction['cause'] as String
                  : '',
            )
          : null,
      override: override is Map && override['tag'] is String
          ? ReputationOverride(
              tag: override['tag'] as String,
              until: reputationDate(override['until']),
              reason: override['reason'] is String
                  ? override['reason'] as String
                  : null,
            )
          : null,
      nextPointExpiryAt: reputationDate(json['nextPointExpiryAt']),
      penaltiesEnabled: json['penaltiesEnabled'] == true,
      asOf: reputationDate(json['asOf']),
    );
  }
}

/// Why and until when the party takes no new commitments.
class ReputationRestriction {
  final DateTime? since;

  /// Null while an admin review is open (no end date yet).
  final DateTime? until;

  /// `points`, `delivery_overdue` or `admin`.
  final String cause;

  const ReputationRestriction({required this.cause, this.since, this.until});
}

/// An admin's tag in force: `low` (label shown) or `clear` (label hidden).
class ReputationOverride {
  final String tag;
  final DateTime? until;
  final String? reason;

  const ReputationOverride({required this.tag, this.until, this.reason});
}

/// An ISO 8601 timestamp of a reputation answer as UTC, or null.
DateTime? reputationDate(Object? value) =>
    value is String ? DateTime.tryParse(value)?.toUtc() : null;

int? _int(Object? value) => value is num ? value.toInt() : null;

double? _double(Object? value) => value is num ? value.toDouble() : null;
