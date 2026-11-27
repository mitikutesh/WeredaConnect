import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/live_trip/check_in_engine_binding.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';
import 'package:wereda_connect/features/live_trip/domain/sharing_state.dart';

import '../../helpers/wait_for.dart';
import 'check_in_fakes.dart';

class _MockAuthBloc extends Mock implements AuthBloc {}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

AuthAuthenticated _signedIn(String userId, UserRole role) =>
    AuthAuthenticated(userId: userId, tokens: _tokens, role: role);

/// LT-08: only a signed-in trucker has a check-in session (all 3 roles).
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late StreamController<AuthState> auth;
  late _MockAuthBloc authBloc;
  late FakeClock clock;
  late FakeLocation location;
  late FakeTrips trips;
  late MemorySharingStateStore states;
  late CheckInEngine engine;
  late CheckInEngineBinding binding;

  setUp(() {
    auth = StreamController<AuthState>.broadcast();
    authBloc = _MockAuthBloc();
    when(() => authBloc.state).thenReturn(const AuthUnauthenticated());
    when(() => authBloc.stream).thenAnswer((_) => auth.stream);
    clock = FakeClock(DateTime.utc(2026, 10, 28, 6));
    location = FakeLocation(clock);
    trips = FakeTrips()..set('trip-1', [leg('d-1', DeliveryStatus.pickedUp)]);
    states = MemorySharingStateStore();
    engine = CheckInEngine(
      pings: FakePings(),
      location: location,
      trips: trips,
      states: states,
      now: () => clock.now,
      timer: FakeTimers().call,
    );
    binding = CheckInEngineBinding(engine: engine, authBloc: authBloc)
      ..attach();
  });

  tearDown(() async {
    await binding.detach();
    await engine.dispose();
    await auth.close();
  });

  Future<CheckInResult> startAs(AuthState state) async {
    auth.add(state);
    await engine.refresh();
    return engine.start('trip-1');
  }

  test('a trucker can share', () async {
    expect(
      await startAs(_signedIn('trucker-1', UserRole.trucker)),
      CheckInResult.recorded,
    );
  });

  for (final role in [UserRole.farmer, UserRole.buyer]) {
    test('a ${role.name} never shares', () async {
      expect(
        await startAs(_signedIn('${role.name}-1', role)),
        CheckInResult.notSharing,
      );
    });
  }

  test('sign out stops the foreground service', () async {
    await startAs(_signedIn('trucker-1', UserRole.trucker));
    expect(location.running, isTrue);

    auth.add(const AuthUnauthenticated());
    await waitFor(() => !location.running);

    expect(location.running, isFalse);
    expect(engine.status.mode, SharingMode.off);
    // The answer stays for the next sign in.
    expect((await states.load('trucker-1')).trips, isNotEmpty);
  });

  test('a deleted account forgets its sharing state', () async {
    await startAs(_signedIn('trucker-1', UserRole.trucker));

    auth.add(const AuthAccountWiped());
    await waitFor(() async => (await states.load('trucker-1')).trips.isEmpty);

    expect((await states.load('trucker-1')).trips, isEmpty);
  });

  test('the lifecycle sets visibility', () async {
    binding.didChangeAppLifecycleState(AppLifecycleState.hidden);
    binding.didChangeAppLifecycleState(AppLifecycleState.resumed);
    await engine.refresh();
    expect(engine.status.mode, SharingMode.off);
  });
}
