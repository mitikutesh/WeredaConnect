import 'dart:async';

import 'package:geolocator/geolocator.dart';

/// Ask for the location permission (FUP-441). On web the request ends within
/// [limit] and only a grant is believed as it is.
///
/// `geolocator_web` (4.1.3, and 4.1.4 is the same) asks by requesting a
/// position with a one day browser time limit, so its request ends only
/// once the prompt is answered and a first fix came, and it answers
/// [LocationPermission.deniedForever] for any error of that request (a
/// dismissed prompt, location switched off). Neither a request still
/// running after [limit] nor that answer tells what the user chose, so on
/// web the site's permission state, read again, decides:
/// - granted reads whileInUse: the caller takes its own fix (a slow first
///   fix after "Allow" is not told to turn location on);
/// - blocked reads deniedForever;
/// - a dismissed or unanswered prompt reads denied, and so does a browser
///   without the Permissions API: the next capture asks again.
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
    onTimeout: () => LocationPermission.unableToDetermine,
  );
  if (answer == LocationPermission.whileInUse ||
      answer == LocationPermission.always) {
    return answer;
  }
  final state = await Geolocator.checkPermission().timeout(
    limit,
    onTimeout: () => LocationPermission.denied,
  );
  return state == LocationPermission.unableToDetermine
      ? LocationPermission.denied
      : state;
}
