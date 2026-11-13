import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:geolocator/geolocator.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';

import 'package:wereda_connect/features/live_trip/data/geolocator_location_source.dart';
import 'package:wereda_connect/features/live_trip/domain/location_source.dart';

class _FakeGeolocator extends GeolocatorPlatform
    with MockPlatformInterfaceMixin {
  // Closed in tearDown.
  // ignore: close_sinks
  final StreamController<Position> positions =
      StreamController<Position>.broadcast();
  LocationSettings? settings;
  bool cancelled = false;

  /// The permission as checked (on web: the site's state).
  LocationPermission state = LocationPermission.whileInUse;

  /// What a request answers; null never comes, like the web plugin's prompt
  /// nobody answers (FUP-441). `geolocator_web` answers deniedForever for
  /// any error of its request.
  LocationPermission? answer = LocationPermission.whileInUse;

  /// The state once a request ran; unchanged when null.
  LocationPermission? stateAfterRequest;

  /// The fix; null never comes, like the web plugin's under its microsecond
  /// time limit.
  Position? fix = _position();
  LocationSettings? fixSettings;
  int requests = 0;

  @override
  Future<bool> isLocationServiceEnabled() async => true;

  @override
  Future<LocationPermission> checkPermission() async => state;

  @override
  Future<LocationPermission> requestPermission() {
    requests++;
    state = stateAfterRequest ?? state;
    final answer = this.answer;
    return answer == null
        ? Completer<LocationPermission>().future
        : Future.value(answer);
  }

  @override
  Future<Position> getCurrentPosition({LocationSettings? locationSettings}) {
    fixSettings = locationSettings;
    final fix = this.fix;
    return fix == null ? Completer<Position>().future : Future.value(fix);
  }

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

  /// FUP-441: on web the plugin's own limits do not hold, and the web
  /// check-in engine runs its actions one at a time.
  group('currentFix', () {
    const limit = Duration(milliseconds: 50);

    GeolocatorLocationSource web() => GeolocatorLocationSource(
          supportsBackground: false,
          isWeb: true,
          fixTimeout: limit,
        );

    // A fix that does not end fails the test here, not after hours.
    Future<CheckInFix?> fixOf(
      GeolocatorLocationSource source, {
      bool askPermission = false,
    }) =>
        source
            .currentFix(askPermission: askPermission)
            .timeout(const Duration(seconds: 5));

    test('takes a medium accuracy fix with its time limit', () async {
      final fix = await fixOf(web());

      expect(fix?.latitude, 8.75);
      expect(fix?.accuracyM, 30);
      expect(platform.fixSettings?.accuracy, LocationAccuracy.medium);
      expect(platform.fixSettings?.timeLimit, limit);
    });

    test('a fix that never comes ends within the limit as none', () async {
      platform.fix = null;

      expect(await fixOf(web()), isNull);
    });

    test('a prompt nobody answers ends within the limit, asked again',
        () async {
      platform
        ..state = LocationPermission.denied
        ..answer = null;
      final source = web();

      expect(await fixOf(source, askPermission: true), isNull);
      expect(await fixOf(source, askPermission: true), isNull);
      expect(platform.requests, 2);
    });

    test('a granted site whose first position failed takes the fix', () async {
      platform
        ..state = LocationPermission.denied
        ..answer = LocationPermission.deniedForever
        ..stateAfterRequest = LocationPermission.whileInUse;

      expect(await fixOf(web(), askPermission: true), isNotNull);
    });

    test('a blocked site gives none', () async {
      platform
        ..state = LocationPermission.denied
        ..answer = LocationPermission.deniedForever
        ..stateAfterRequest = LocationPermission.deniedForever;

      expect(await fixOf(web(), askPermission: true), isNull);
      expect(platform.fixSettings, isNull);
    });
  });
}
