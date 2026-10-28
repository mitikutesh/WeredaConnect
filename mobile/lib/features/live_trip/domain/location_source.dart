/// One position from the platform.
class CheckInFix {
  const CheckInFix({
    required this.latitude,
    required this.longitude,
    required this.accuracyM,
    required this.capturedAt,
    this.mocked = false,
  });

  final double latitude;
  final double longitude;

  /// Horizontal accuracy in metres.
  final double accuracyM;

  /// When the platform took it (UTC).
  final DateTime capturedAt;

  /// The platform flagged a mock location (`Position.isMocked`).
  final bool mocked;
}

/// The text of the notification Android shows while sharing is on.
typedef BackgroundNotice = ({String title, String text, String channel});

/// Where the check-in engine gets positions (geolocator on device, a fake in
/// tests).
abstract interface class LocationSource {
  /// Whether this platform keeps the cadence running in the background
  /// (Android, through a foreground service). Web does not: it checks in
  /// only while the page is open.
  bool get supportsBackground;

  /// One fix with the medium accuracy setting and a 60 second time limit, or
  /// null when there is none in time (no permission, services off, no
  /// signal). Never retried here: the caller skips to the next slot.
  /// [askPermission] asks for the location permission when it was not
  /// decided yet: only for an action of the driver, in the foreground.
  Future<CheckInFix?> currentFix({bool askPermission = false});

  /// Run the foreground service with [notice] while sharing is on, and call
  /// [onWake] each time it delivers a (low power) position: on Android that
  /// wakes the cadence when the phone sleeps, since Dart timers do not run
  /// in deep sleep. A due fix is still taken with [currentFix]. Must be
  /// called while the app is in the foreground. No-op without
  /// [supportsBackground] or when running.
  Future<void> startBackground({
    required BackgroundNotice notice,
    required void Function() onWake,
  });

  /// Stop the foreground service (and its notification).
  Future<void> stopBackground();

  /// Whether the foreground service runs.
  bool get isBackgroundRunning;
}
