import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
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
  late MemoryDropoffHandoffStore handoffs;

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
    handoffs = MemoryDropoffHandoffStore();
    binding = CheckInEngineBinding(
      engine: engine,
      authBloc: authBloc,
      handoffs: handoffs,
    )..attach();
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

  // T-3: a kept drop-off handoff is the delivery's QR secret, and on web
  // its storage is the browser's: none survives a sign out.
  for (final (name, state) in [
    ('sign out', const AuthUnauthenticated()),
    ('a deleted account', const AuthAccountWiped()),
  ]) {
    test('$name drops every kept drop-off handoff', () async {
      auth.add(_signedIn('trucker-1', UserRole.trucker));
      await handoffs.keep(
        'trucker-1',
        'd-1',
        const KeptHandoff(token: 'tok', code: '757555978'),
      );

      auth.add(state);
      Future<bool> dropped() async =>
          await handoffs.read('trucker-1', 'd-1') == null;
      await waitFor(dropped);

      expect(await handoffs.read('trucker-1', 'd-1'), isNull);
    });
  }

  test('a signed-in trucker keeps them', () async {
    await handoffs.keep('trucker-1', 'd-1', const KeptHandoff(code: '1'));

    auth.add(_signedIn('trucker-1', UserRole.trucker));
    await engine.refresh();

    expect(await handoffs.read('trucker-1', 'd-1'), isNotNull);
  });

  test('the lifecycle sets visibility', () async {
    binding.didChangeAppLifecycleState(AppLifecycleState.hidden);
    binding.didChangeAppLifecycleState(AppLifecycleState.resumed);
    await engine.refresh();
    expect(engine.status.mode, SharingMode.off);
  });
}
