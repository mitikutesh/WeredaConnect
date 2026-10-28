import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:geolocator/geolocator.dart';

import 'package:wereda_connect/features/live_trip/domain/check_in_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/location_source.dart';

/// Positions from `geolocator` (LT-08). Works on Android and web; only
/// Android runs the foreground service.
///
/// On Android the service is `geolocator_android`'s own
/// `GeolocatorLocationService` (merged from the plugin's manifest with
/// `foregroundServiceType="location"`): it runs while a position stream with
/// a [ForegroundNotificationConfig] is listened to, and shows the
/// notification. The stream is opened from the consent action, while the app
/// is in the foreground, so the "while in use" permission is enough. The
/// stream asks for a low power position about every [backgroundInterval]
/// (cell and wifi, no GPS); each one only wakes the cadence when the phone
/// sleeps, because Dart timers do not run in deep sleep. The check-ins
/// themselves are [currentFix] calls with the medium accuracy setting.
class GeolocatorLocationSource implements LocationSource {
  GeolocatorLocationSource({bool? supportsBackground})
      : supportsBackground = supportsBackground ??
            (!kIsWeb && defaultTargetPlatform == TargetPlatform.android);

  /// How often the foreground service asks for a wake-up position.
  static const backgroundInterval = Duration(minutes: 5);

  @override
  final bool supportsBackground;

  StreamSubscription<Position>? _subscription;

  @override
  bool get isBackgroundRunning => _subscription != null;

  @override
  Future<CheckInFix?> currentFix({bool askPermission = false}) async {
    if (!await _canLocate(askPermission: askPermission)) return null;
    try {
      final position = await Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
          accuracy: LocationAccuracy.medium,
          timeLimit: CheckInCadence.fixTimeout,
        ),
      );
      return _toFix(position);
    } on Object catch (e) {
      // Timeout, services switched off, no signal: the caller skips the slot.
      debugPrint('[LiveTrip] no position: $e');
      return null;
    }
  }

  @override
  Future<void> startBackground({
    required BackgroundNotice notice,
    required void Function() onWake,
  }) async {
    if (!supportsBackground || _subscription != null) return;
    if (!await _canLocate(askPermission: false)) return;
    final settings = AndroidSettings(
      accuracy: LocationAccuracy.low,
      intervalDuration: backgroundInterval,
      foregroundNotificationConfig: ForegroundNotificationConfig(
        notificationTitle: notice.title,
        notificationText: notice.text,
        notificationChannelName: notice.channel,
        setOngoing: true,
      ),
    );
    _subscription =
        Geolocator.getPositionStream(locationSettings: settings).listen(
      (_) => onWake(),
      onError: (Object e) => debugPrint('[LiveTrip] position stream: $e'),
      onDone: () => _subscription = null,
    );
  }

  @override
  Future<void> stopBackground() async {
    await _subscription?.cancel();
    _subscription = null;
  }

  Future<bool> _canLocate({required bool askPermission}) async {
    try {
      if (!await Geolocator.isLocationServiceEnabled()) return false;
      var permission = await Geolocator.checkPermission();
      if (permission == LocationPermission.denied && askPermission) {
        permission = await Geolocator.requestPermission();
      }
      return permission == LocationPermission.whileInUse ||
          permission == LocationPermission.always;
    } on Object catch (e) {
      debugPrint('[LiveTrip] location permission unknown: $e');
      return false;
    }
  }

  static CheckInFix _toFix(Position position) => CheckInFix(
        latitude: position.latitude,
        longitude: position.longitude,
        accuracyM: position.accuracy,
        capturedAt: position.timestamp.toUtc(),
        mocked: position.isMocked,
      );
}
