import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/data_usage_auth_binding.dart';
import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

const _tokens = AuthTokens(
  accessToken: 'a',
  refreshToken: 'r',
  idToken: 'i',
);

AuthAuthenticated _signedIn(String userId, UserRole role) =>
    AuthAuthenticated(userId: userId, tokens: _tokens, role: role);

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late DataUsageTracker tracker;
  late MockAuthBloc authBloc;
  late StreamController<AuthState> states;

  setUp(() async {
    SharedPreferences.setMockInitialValues({});
    tracker = DataUsageTracker();
    await tracker.initialize();
    authBloc = MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    when(() => authBloc.stream).thenAnswer((_) => states.stream);
    when(() => authBloc.state).thenReturn(const AuthInitial());
  });

  tearDown(() => states.close());

  test('applies an already-authenticated state immediately', () {
    when(() => authBloc.state)
        .thenReturn(_signedIn('farmer-1', UserRole.farmer));

    bindDataUsageToAuth(tracker, authBloc);

    expect(tracker.userId, 'farmer-1');
  });

  for (final role in UserRole.values) {
    test('switches to the signed-in ${role.name} and back out on logout',
        () async {
      bindDataUsageToAuth(tracker, authBloc);

      states.add(_signedIn('${role.name}-1', role));
      await Future<void>.delayed(Duration.zero);
      expect(tracker.userId, '${role.name}-1');

      states.add(const AuthUnauthenticated());
      await Future<void>.delayed(Duration.zero);
      expect(tracker.userId, isNull);
    });
  }

  test('a wiped (deleted) account loses its counters', () async {
    bindDataUsageToAuth(tracker, authBloc);
    states.add(_signedIn('farmer-1', UserRole.farmer));
    await Future<void>.delayed(Duration.zero);
    tracker.addDownloaded(300);
    await Future<void>.delayed(Duration.zero);
    final prefs = await SharedPreferences.getInstance();
    expect(prefs.getKeys(), isNotEmpty);

    states.add(const AuthAccountWiped());
    await Future<void>.delayed(Duration.zero);

    expect(tracker.userId, isNull);
    expect(prefs.getKeys(), isEmpty);
  });

  test('transient states keep the current account', () async {
    bindDataUsageToAuth(tracker, authBloc);
    states.add(_signedIn('buyer-1', UserRole.buyer));
    await Future<void>.delayed(Duration.zero);

    states.add(const AuthLoading());
    await Future<void>.delayed(Duration.zero);

    expect(tracker.userId, 'buyer-1');
  });
}
