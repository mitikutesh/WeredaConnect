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
  });
}
