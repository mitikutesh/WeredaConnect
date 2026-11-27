import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_labels.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// A check-in farther than this from every town is not named after one.
const double namedPlaceWithinKm = 30;

/// [duration] in words: "28 min", "1 h 29 min".
String liveTripDuration(AppLocalizations l10n, Duration duration) {
  final minutes = duration.isNegative ? 0 : duration.inMinutes;
  if (minutes < 60) return l10n.liveTripDurationMinutes(minutes);
  return l10n.liveTripDurationHours(minutes ~/ 60, minutes % 60);
}

/// How old a check-in is: "Just now", "28 min ago".
String checkInAge(AppLocalizations l10n, Duration age) =>
    age < const Duration(minutes: 1)
        ? l10n.liveTripJustNow
        : l10n.liveTripAgo(liveTripDuration(l10n, age));

/// The town nearest to [point] in [languageCode], or null when none is
/// within [namedPlaceWithinKm] (or the place list is not loaded yet).
///
/// The answers are remembered for the last place list, so the chip, the
/// ribbon and the trip log ask the list (about 3,860 towns) once per
/// check-in, not on every minute's repaint.
String? placeNameNear(
  List<Place> places,
  GeoPoint? point,
  String languageCode,
) {
  if (point == null || places.isEmpty) return null;
  if (!identical(places, _namedFor)) {
    _namedFor = places;
    _named.clear();
  }
  final key = (point, languageCode);
  if (_named.containsKey(key)) return _named[key];
  if (_named.length >= _maxNamed) _named.remove(_named.keys.first);
  return _named[key] = nearestPlace(places, point, maxKm: namedPlaceWithinKm)
      ?.place
      .displayName(languageCode);
}

const int _maxNamed = 256;
List<Place>? _namedFor;
final Map<(GeoPoint, String), String?> _named = {};

/// A distance the way the boards write it: whole km from 10 km ("47", "134"),
/// one decimal below ("3.2", "0.8"). The server sends one decimal.
String kmText(double km) =>
    NumberFormat(km.abs() >= 10 ? '#,##0' : '0.#', 'en').format(km);

/// An amount of birr with thousands separators: "91,200".
String birrText(double amount) => NumberFormat('#,##0.##', 'en').format(amount);

/// The first word of a display name ("Dawit" of "Dawit Tesfaye"), how the
/// boards address a party on a short button; the whole name when it is one
/// word or empty.
String firstNameOf(String display) {
  final words = display.trim().split(RegExp(r'\s+'));
  return words.isEmpty ? display : words.first;
}

/// What a trip log row says about a check-in.
String tripLogDescription(AppLocalizations l10n, TripLogEntry entry) =>
    switch (entry.kind) {
      TripLogKind.pickup => l10n.liveTripLogPickup,
      TripLogKind.otherStopScan => l10n.liveTripLogOtherStop,
      TripLogKind.paused => l10n.liveTripLogPaused,
      TripLogKind.checkIn => switch (entry.fix?.source) {
          'hourly' => l10n.liveTripLogHourly,
          'near_stop' => l10n.liveTripLogNearStop,
          'manual' => l10n.liveTripLogManual,
          'arrived' => l10n.liveTripLogArrived,
          'start' => l10n.liveTripLogStart,
          _ => l10n.liveTripLogCheckIn,
        },
    };
