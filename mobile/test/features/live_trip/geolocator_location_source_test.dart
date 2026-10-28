import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:geolocator/geolocator.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';

import 'package:wereda_connect/features/live_trip/data/geolocator_location_source.dart';

class _FakeGeolocator extends GeolocatorPlatform
    with MockPlatformInterfaceMixin {
  // Closed in tearDown.
  // ignore: close_sinks
  final StreamController<Position> positions =
      StreamController<Position>.broadcast();
  LocationSettings? settings;
  bool cancelled = false;

  @override
  Future<bool> isLocationServiceEnabled() async => true;

  @override
  Future<LocationPermission> checkPermission() async =>
      LocationPermission.whileInUse;

  @override
  Stream<Position> getPositionStream({LocationSettings? locationSettings}) {
    settings = locationSettings;
    positions.onCancel = () => cancelled = true;
    return positions.stream;
  }
}

Position _position() => Position(
      latitude: 8.75,
      longitude: 38.98,
      timestamp: DateTime.utc(2026, 10, 28, 6),
      accuracy: 30,
      altitude: 0,
      altitudeAccuracy: 0,
      heading: 0,
      headingAccuracy: 0,
      speed: 0,
      speedAccuracy: 0,
    );

/// LT-08: the foreground service runs while geolocator's position stream is
/// listened to; a failed stream must read as stopped so the engine starts it
/// again.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late _FakeGeolocator platform;
  late GeolocatorLocationSource source;
  late int wakes;

  setUp(() async {
    platform = _FakeGeolocator();
    GeolocatorPlatform.instance = platform;
    source = GeolocatorLocationSource(supportsBackground: true);
    wakes = 0;
    await source.startBackground(
      notice: (title: 'T', text: 'X', channel: 'C'),
      onWake: () => wakes++,
    );
  });

  tearDown(() => platform.positions.close());

  test('runs the service with a low power stream and its notification', () {
    expect(source.isBackgroundRunning, isTrue);
    final settings = platform.settings! as AndroidSettings;
    expect(settings.accuracy, LocationAccuracy.low);
    expect(
      settings.intervalDuration,
      GeolocatorLocationSource.backgroundInterval,
    );
    expect(settings.foregroundNotificationConfig?.notificationTitle, 'T');
    expect(settings.foregroundNotificationConfig?.notificationText, 'X');
    expect(settings.foregroundNotificationConfig?.notificationChannelName, 'C');
  });

  test('each position wakes the cadence', () async {
    platform.positions.add(_position());
    await Future<void>.delayed(Duration.zero);
    expect(wakes, 1);
  });

  test('a stream error ends it, so it reads as stopped', () async {
    platform.positions.addError(const LocationServiceDisabledException());
    await Future<void>.delayed(Duration.zero);

    expect(source.isBackgroundRunning, isFalse);
    expect(platform.cancelled, isTrue);

    // And it can be started again.
    await source.startBackground(
      notice: (title: 'T', text: 'X', channel: 'C'),
      onWake: () => wakes++,
    );
    expect(source.isBackgroundRunning, isTrue);
  });

  test('stopBackground cancels the stream', () async {
    await source.stopBackground();
    expect(source.isBackgroundRunning, isFalse);
    expect(platform.cancelled, isTrue);
  });
}
