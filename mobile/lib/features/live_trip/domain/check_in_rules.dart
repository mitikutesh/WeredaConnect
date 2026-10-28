import 'dart:math';

import 'package:wereda_connect/core/models/location_ping.dart';

/// The check-in rules of the trucker's phone (LT-08, `live-trip.md` section
/// 5, `live-trip-api.md` section 1). Pure Dart, shared by mobile and web.

/// A point on the map.
typedef GeoPoint = ({double latitude, double longitude});

/// `kind` values of a `location_ping`.
abstract final class PingKind {
  static const fix = 'fix';
  static const paused = 'paused';
  static const resumed = 'resumed';
  static const declined = 'declined';
}

/// `source` values of a `fix`.
abstract final class CheckInSource {
  static const hourly = 'hourly';
  static const scan = 'scan';
  static const nearStop = 'near_stop';
  static const manual = 'manual';
  static const arrived = 'arrived';
  static const start = 'start';
}

/// The cadence (`live-trip.md` section 5.2, decision D5).
abstract final class CheckInCadence {
  /// A fix this long after the last fix of any source.
  static const hourly = Duration(minutes: 60);

  /// A fix this often while the last fix is near an undelivered drop-off.
  static const nearStop = Duration(minutes: 15);

  /// "Near" a drop-off.
  static const nearStopRadiusKm = 10.0;

  /// How long the phone waits for one fix before it skips to the next slot.
  static const fixTimeout = Duration(seconds: 60);

  /// The longest pause the server accepts after its capture time.
  static const maxPause = Duration(hours: 18);

  /// The interval after a fix at [lastFix]: 15 minutes when it is within
  /// [nearStopRadiusKm] of one of [undeliveredDropoffs], else hourly. Without
  /// a last fix or a drop-off point the phone stays hourly.
  static Duration intervalAfter(
    GeoPoint? lastFix,
    List<GeoPoint> undeliveredDropoffs,
  ) {
    if (lastFix == null) return hourly;
    final near = undeliveredDropoffs
        .any((d) => distanceKm(lastFix, d) <= nearStopRadiusKm);
    return near ? nearStop : hourly;
  }

  /// The source a cadence fix is sent with for [interval].
  static String sourceFor(Duration interval) =>
      interval == nearStop ? CheckInSource.nearStop : CheckInSource.hourly;

  /// When the next cadence fix is due: [interval] after the later of the last
  /// fix and the last attempt that found none (a missed slot is skipped, never
  /// retried in a loop). Null when neither happened yet.
  static DateTime? nextDue({
    required Duration interval,
    DateTime? lastFixAt,
    DateTime? lastAttemptAt,
  }) {
    final from = switch ((lastFixAt, lastAttemptAt)) {
      (final DateTime f, final DateTime a) => f.isAfter(a) ? f : a,
      (final DateTime f, null) => f,
      (null, final DateTime a) => a,
      (null, null) => null,
    };
    return from?.add(interval);
  }
}

/// The ingest bounds the server applies to a `fix`: a fix outside them is
/// refused for good, so the phone does not send it.
abstract final class FixBounds {
  static const minLatitude = 3.0;
  static const maxLatitude = 15.0;
  static const minLongitude = 32.5;
  static const maxLongitude = 48.5;
  static const maxAccuracyM = 5000.0;

  static bool accepts(double latitude, double longitude, double accuracyM) =>
      latitude >= minLatitude &&
      latitude <= maxLatitude &&
      longitude >= minLongitude &&
      longitude <= maxLongitude &&
      accuracyM >= 0 &&
      accuracyM <= maxAccuracyM;
}

/// Great circle distance in kilometres.
double distanceKm(GeoPoint a, GeoPoint b) {
  const earthRadiusKm = 6371.0;
  double rad(double degrees) => degrees * pi / 180.0;
  final dLat = rad(b.latitude - a.latitude);
  final dLon = rad(b.longitude - a.longitude);
  final h = sin(dLat / 2) * sin(dLat / 2) +
      cos(rad(a.latitude)) *
          cos(rad(b.latitude)) *
          sin(dLon / 2) *
          sin(dLon / 2);
  return 2 * earthRadiusKm * atan2(sqrt(h), sqrt(1 - h));
}

/// [time] in UTC cut to whole milliseconds: the entity id carries epoch
/// milliseconds and the server checks it against `capturedAt`.
DateTime toWholeMillisUtc(DateTime time) => DateTime.fromMillisecondsSinceEpoch(
      time.millisecondsSinceEpoch,
      isUtc: true,
    );

/// The sync entity id of a ping: `{tripId}#{capturedAt in epoch ms}`.
String locationPingId(String tripId, DateTime capturedAt) =>
    '$tripId#${capturedAt.millisecondsSinceEpoch}';

/// The `data` of a `location_ping` create (`live-trip-api.md` section 1).
/// Coordinates go at 5 decimals (about 1 m; the server keeps 4) and the
/// accuracy in whole metres; `mocked` only when true, to save bytes.
Map<String, Object> locationPingData(LocationPing ping) {
  double round5(double value) => (value * 100000).roundToDouble() / 100000;
  return {
    'tripId': ping.tripId,
    'kind': ping.kind,
    'capturedAt': ping.capturedAt.toUtc().toIso8601String(),
    if (ping.kind == PingKind.fix) ...{
      'source': ping.source ?? CheckInSource.manual,
      'latitude': round5(ping.latitude ?? 0),
      'longitude': round5(ping.longitude ?? 0),
      'accuracyM': (ping.accuracyM ?? 0).round(),
      if (ping.mocked) 'mocked': true,
    },
    if (ping.kind == PingKind.paused && ping.pausedUntil != null)
      'pausedUntil': ping.pausedUntil!.toUtc().toIso8601String(),
  };
}

/// The idempotency key of a ping in the format of `live-trip-api.md`
/// section 1: `{userId}:location_ping:{entityId}:create:{capturedAt ms}`.
String locationPingIdempotencyKey(String userId, LocationPing ping) =>
    '$userId:location_ping:${ping.pingId}:create:'
    '${ping.capturedAt.millisecondsSinceEpoch}';
