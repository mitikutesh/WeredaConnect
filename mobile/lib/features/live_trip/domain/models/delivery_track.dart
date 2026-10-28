import 'package:flutter/foundation.dart';

/// A point on the map in decimal degrees.
@immutable
class GeoPoint {
  const GeoPoint(this.latitude, this.longitude);

  final double latitude;
  final double longitude;

  @override
  bool operator ==(Object other) =>
      other is GeoPoint &&
      other.latitude == latitude &&
      other.longitude == longitude;

  @override
  int get hashCode => Object.hash(latitude, longitude);

  @override
  String toString() => 'GeoPoint($latitude, $longitude)';
}

/// The `sharing` state of a track (live-trip-api.md section 2).
enum TrackSharing {
  /// Before the delivery's pickup: no position yet.
  notStarted('not_started'),

  /// Check-ins are flowing.
  live('live'),

  /// The driver paused sharing until `pausedUntil`.
  paused('paused'),

  /// The driver declined sharing ("Scans only"): only the QR scans send a fix.
  declined('declined'),

  /// The delivery is delivered, cancelled or disputed. No coordinates.
  ended('ended'),

  /// A value this app version does not know yet.
  unknown('');

  const TrackSharing(this.wire);

  /// The value on the wire.
  final String wire;

  static TrackSharing fromWire(String? value) => TrackSharing.values.firstWhere(
        (s) => s != TrackSharing.unknown && s.wire == value,
        orElse: () => TrackSharing.unknown,
      );
}

/// One check-in: `lastFix` or a `trail` entry. `lastFix` carries
/// [accuracyM], trail entries carry [source].
@immutable
class TrackFix {
  const TrackFix({
    required this.point,
    required this.capturedAt,
    this.receivedAt,
    this.accuracyM,
    this.source,
  });

  final GeoPoint point;

  /// When the phone took the fix (UTC).
  final DateTime capturedAt;

  /// When the server stored it (UTC).
  final DateTime? receivedAt;

  final double? accuracyM;

  /// `hourly`, `scan`, `near_stop`, `manual`, `arrived` or `start`.
  final String? source;

  /// A fix received more than this long after capture is tagged "sent late".
  static const sentLateAfter = Duration(minutes: 15);

  /// Whether the fix waited on the phone (no network) before it arrived.
  bool get sentLate =>
      receivedAt != null && receivedAt!.difference(capturedAt) > sentLateAfter;

  static TrackFix? _parse(Object? json) {
    if (json is! Map) return null;
    final point = _point(json['latitude'], json['longitude']);
    final capturedAt = _date(json['capturedAt']);
    if (point == null || capturedAt == null) return null;
    return TrackFix(
      point: point,
      capturedAt: capturedAt,
      receivedAt: _date(json['receivedAt']),
      accuracyM: _double(json['accuracyM']),
      source: json['source'] is String ? json['source'] as String : null,
    );
  }
}

/// A pause of the driver inside the viewer's window.
@immutable
class TrackPause {
  const TrackPause({required this.from, required this.until});

  final DateTime from;
  final DateTime until;

  static TrackPause? _parse(Object? json) {
    if (json is! Map) return null;
    final from = _date(json['from']);
    final until = _date(json['until']);
    if (from == null || until == null) return null;
    return TrackPause(from: from, until: until);
  }
}

/// Another order's pickup or drop-off on the same truck: never named, at
/// 2 decimals (about 1 km).
@immutable
class TrackStop {
  const TrackStop({
    required this.kind,
    required this.point,
    required this.done,
  });

  /// `pickup` or `dropoff`.
  final String kind;
  final GeoPoint point;
  final bool done;

  static TrackStop? _parse(Object? json) {
    if (json is! Map) return null;
    final point = _point(json['latitude'], json['longitude']);
    if (point == null) return null;
    return TrackStop(
      kind: json['kind'] is String ? json['kind'] as String : '',
      point: point,
      done: json['done'] == true,
    );
  }
}

/// The arrival window, on 5 minute marks (UTC).
@immutable
class TrackEta {
  const TrackEta({required this.from, required this.to});

  final DateTime from;
  final DateTime to;

  static TrackEta? _parse(Object? json) {
    if (json is! Map) return null;
    final from = _date(json['from']);
    final to = _date(json['to']);
    if (from == null || to == null) return null;
    return TrackEta(from: from, to: to);
  }
}

/// The trip after delivery: only when `ended`.
@immutable
class TrackSummary {
  const TrackSummary({
    required this.distanceKm,
    required this.durationMin,
    required this.fixes,
  });

  final double distanceKm;
  final int durationMin;
  final int fixes;

  static TrackSummary? _parse(Object? json) {
    if (json is! Map) return null;
    return TrackSummary(
      distanceKm: _double(json['distanceKm']) ?? 0,
      durationMin: _int(json['durationMin']) ?? 0,
      fixes: _int(json['fixes']) ?? 0,
    );
  }
}

/// The answer of `GET /deliveries/{deliveryId}/track`
/// (specs/contracts/live-trip-api.md section 2), parsed by hand from the
/// server's camelCase JSON so mobile and web read it the same way.
@immutable
class DeliveryTrack {
  const DeliveryTrack({
    required this.deliveryId,
    required this.sharing,
    required this.asOf,
    this.pausedUntil,
    this.lastFix,
    this.trail = const [],
    this.pauses = const [],
    this.pickup,
    this.pickedUpAt,
    this.dropoff,
    this.dropoffNote,
    this.otherStops = const [],
    this.eta,
    this.remainingKm,
    this.summary,
  });

  final String deliveryId;
  final TrackSharing sharing;

  /// The server time of the answer (UTC).
  final DateTime asOf;

  /// The running pause's end, only when [sharing] is `paused`.
  final DateTime? pausedUntil;

  /// The latest check-in of the window, or null.
  final TrackFix? lastFix;

  /// Every check-in of the window, in capture order.
  final List<TrackFix> trail;

  final List<TrackPause> pauses;

  /// The pickup point; null when unknown and always when `ended`.
  final GeoPoint? pickup;

  /// The delivery's pickup time.
  final DateTime? pickedUpAt;

  /// The planned drop-off point; null without one (the server may not know
  /// it yet) and always when `ended`.
  final GeoPoint? dropoff;

  /// The buyer's landmark for the drop-off (LT-12), or null.
  final String? dropoffNote;

  /// The other stops on the truck, unnamed.
  final List<TrackStop> otherStops;

  final TrackEta? eta;
  final double? remainingKm;
  final TrackSummary? summary;

  /// Parses the response body. Throws a [FormatException] when a required
  /// field (`deliveryId`, `sharing`, `asOf`) is missing; a malformed list
  /// entry is skipped so one bad check-in never hides the others.
  factory DeliveryTrack.fromJson(Map<String, dynamic> json) {
    final deliveryId = json['deliveryId'];
    final sharing = json['sharing'];
    final asOf = _date(json['asOf']);
    if (deliveryId is! String || sharing is! String || asOf == null) {
      throw const FormatException('Unexpected track shape');
    }
    final pickup = json['pickup'];
    final dropoff = json['dropoff'];
    return DeliveryTrack(
      deliveryId: deliveryId,
      sharing: TrackSharing.fromWire(sharing),
      asOf: asOf,
      pausedUntil: _date(json['pausedUntil']),
      lastFix: TrackFix._parse(json['lastFix']),
      trail: _list(json['trail'], TrackFix._parse),
      pauses: _list(json['pauses'], TrackPause._parse),
      pickup: pickup is Map
          ? _point(pickup['latitude'], pickup['longitude'])
          : null,
      pickedUpAt: pickup is Map ? _date(pickup['at']) : null,
      dropoff: dropoff is Map
          ? _point(dropoff['latitude'], dropoff['longitude'])
          : null,
      dropoffNote: dropoff is Map && dropoff['note'] is String
          ? dropoff['note'] as String
          : null,
      otherStops: _list(json['otherStops'], TrackStop._parse),
      eta: TrackEta._parse(json['eta']),
      remainingKm: _double(json['remainingKm']),
      summary: TrackSummary._parse(json['summary']),
    );
  }
}

List<T> _list<T>(Object? json, T? Function(Object?) parse) {
  if (json is! List) return const [];
  return List.unmodifiable(json.map(parse).whereType<T>());
}

GeoPoint? _point(Object? latitude, Object? longitude) =>
    latitude is num && longitude is num
        ? GeoPoint(latitude.toDouble(), longitude.toDouble())
        : null;

double? _double(Object? value) => value is num ? value.toDouble() : null;

int? _int(Object? value) => value is num ? value.toInt() : null;

DateTime? _date(Object? value) =>
    value is String ? DateTime.tryParse(value)?.toUtc() : null;
