import 'dart:async';

import 'package:geolocator/geolocator.dart';

/// Ask for the location permission (FUP-441). On web the request ends within
/// [limit] and a "blocked" answer is checked before it is believed.
///
/// `geolocator_web` (4.1.3, and 4.1.4 is the same) asks by requesting a
/// position with a one day browser time limit, and it answers
/// [LocationPermission.deniedForever] for any error of that request, so a
/// dismissed prompt or location switched off reads as blocked. On web:
/// - a prompt still unanswered after [limit] reads as
///   [LocationPermission.denied], so the next capture asks again;
/// - a [LocationPermission.deniedForever] answer is replaced by the site's
///   permission state, read again: only a blocked site reads deniedForever,
///   a dismissed prompt reads denied (asked again), and a granted site whose
///   position failed reads whileInUse (the caller then tries for a fix).
///
/// On the phone the request is the system dialog, which holds the screen
/// until it is answered, so it is not bounded here.
Future<LocationPermission> requestLocationPermission({
  required bool isWeb,
  required Duration limit,
}) async {
  if (!isWeb) return Geolocator.requestPermission();
  final answer = await Geolocator.requestPermission().timeout(
    limit,
    onTimeout: () => LocationPermission.denied,
  );
  if (answer != LocationPermission.deniedForever) return answer;
  return Geolocator.checkPermission().timeout(
    limit,
    onTimeout: () => LocationPermission.denied,
  );
}
