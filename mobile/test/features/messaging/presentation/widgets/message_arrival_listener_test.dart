import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';
import 'package:wereda_connect/features/messaging/presentation/widgets/message_arrival_listener.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockInboxBloc extends Mock implements InboxBloc {}

class MockMessageRepository extends Mock implements BaseMessageRepository {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

class FakeInboxEvent extends Fake implements InboxEvent {}

const _tokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

const _userOne = AuthAuthenticated(
  userId: 'user-1',
  tokens: _tokens,
  role: UserRole.farmer,
);

const _userTwo = AuthAuthenticated(
  userId: 'user-2',
  tokens: _tokens,
  role: UserRole.buyer,
);

void main() {
  late MockAuthBloc authBloc;
  late MockInboxBloc inboxBloc;
  late MockMessageRepository repository;
  late StreamController<AuthState> authStates;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeInboxEvent());
  });

  setUp(() {
    authBloc = MockAuthBloc();
    inboxBloc = MockInboxBloc();
    repository = MockMessageRepository();
    authStates = StreamController<AuthState>.broadcast();

    when(() => authBloc.stream).thenAnswer((_) => authStates.stream);
    // BlocProvider.value subscribes to the provided bloc's stream/state.
    when(() => inboxBloc.stream)
        .thenAnswer((_) => const Stream<InboxState>.empty());
    when(() => inboxBloc.state).thenReturn(const InboxInitial());
    when(() => inboxBloc.repository).thenReturn(repository);
    when(() => repository.watchInbound())
        .thenAnswer((_) => const Stream<List<Message>>.empty());
  });

  tearDown(() async {
    await authStates.close();
  });

  Widget buildTestWidget() {
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<InboxBloc>.value(value: inboxBloc),
      ],
      child: const MaterialApp(
        home: MessageArrivalListener(child: SizedBox()),
      ),
    );
  }

  group('MessageArrivalListener inbox bootstrap', () {
    testWidgets('dispatches LoadInbox for the signed-in user on mount',
        (tester) async {
      when(() => authBloc.state).thenReturn(_userOne);

      await tester.pumpWidget(buildTestWidget());

      // The unread badge + arrival poll must start without the user having
      // to open a screen that happens to contain MessagesBadgeButton (the
      // web/desktop shell mounts no such widget).
      verify(() => inboxBloc.add(const LoadInbox('user-1'))).called(1);
    });

    testWidgets('bootstraps once when authentication arrives later',
        (tester) async {
      when(() => authBloc.state).thenReturn(const AuthInitial());

      await tester.pumpWidget(buildTestWidget());
      verifyNever(() => inboxBloc.add(any()));

      authStates.add(_userOne);
      await tester.pump();
      // A re-emission for the same user (e.g. token refresh) must not
      // re-dispatch — LoadInbox flashes InboxLoading through the open inbox.
      authStates.add(_userOne);
      await tester.pump();

      verify(() => inboxBloc.add(const LoadInbox('user-1'))).called(1);
    });

    testWidgets('re-bootstraps for a different user after sign-out',
        (tester) async {
      when(() => authBloc.state).thenReturn(_userOne);

      await tester.pumpWidget(buildTestWidget());
      authStates.add(const AuthUnauthenticated());
      await tester.pump();
      authStates.add(_userTwo);
      await tester.pump();

      verify(() => inboxBloc.add(const LoadInbox('user-1'))).called(1);
      verify(() => inboxBloc.add(const LoadInbox('user-2'))).called(1);
    });

    testWidgets('sign-out resets the inbox (stops the signed-out poll)',
        (tester) async {
      when(() => authBloc.state).thenReturn(_userOne);

      await tester.pumpWidget(buildTestWidget());
      authStates.add(const AuthUnauthenticated());
      await tester.pump();

      verify(() => inboxBloc.add(const InboxReset())).called(1);
    });

    testWidgets('never touches InboxBloc when nobody was signed in',
        (tester) async {
      when(() => authBloc.state).thenReturn(const AuthInitial());

      await tester.pumpWidget(buildTestWidget());
      authStates.add(const AuthUnauthenticated());
      await tester.pump();

      // Reading the bloc while signed out would lazily create it and touch
      // the local store for nothing (broke smoke tests before).
      verifyNever(() => inboxBloc.add(any()));
    });
  });

  group('MessageArrivalListener lifecycle-aware polling', () {
    // NOTE: the binding synthesizes intermediate lifecycle states (resumed →
    // paused walks through inactive and hidden), so InboxPaused may fire more
    // than once per transition — the event is idempotent, and these tests
    // assert "at least once", not exact counts.
    testWidgets('pauses and resumes the inbox poll with the app lifecycle',
        (tester) async {
      when(() => authBloc.state).thenReturn(_userOne);
      await tester.pumpWidget(buildTestWidget());

      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
      await tester.pump();
      verify(() => inboxBloc.add(const InboxPaused()))
          .called(greaterThanOrEqualTo(1));

      clearInteractions(inboxBloc);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pump();
      verify(() => inboxBloc.add(const InboxResumed())).called(1);
    });

    testWidgets('hidden pauses too (web tab switch has no paused state)',
        (tester) async {
      when(() => authBloc.state).thenReturn(_userOne);
      await tester.pumpWidget(buildTestWidget());

      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.hidden);
      await tester.pump();

      verify(() => inboxBloc.add(const InboxPaused()))
          .called(greaterThanOrEqualTo(1));
      verifyNever(() => inboxBloc.add(const InboxResumed()));

      // Restore for subsequent tests.
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pump();
    });

    testWidgets('ignores lifecycle changes while signed out', (tester) async {
      when(() => authBloc.state).thenReturn(const AuthInitial());
      await tester.pumpWidget(buildTestWidget());

      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.paused);
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pump();

      verifyNever(() => inboxBloc.add(any()));

      // Leave the binding in the resumed state for any later suites.
      tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
      await tester.pump();
    });
  });
}
