import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/message.dart';
import 'package:wereda_connect/features/messaging/domain/repositories/base_message_repository.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_bloc.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_event.dart';
import 'package:wereda_connect/features/messaging/presentation/bloc/inbox_state.dart';

import '../../../../helpers/wait_for.dart';

class MockMessageRepository extends Mock implements BaseMessageRepository {}

void main() {
  late MockMessageRepository repository;
  late int pullCount;

  /// Delay applied inside pullMessages — lets tests create an in-flight pull.
  Duration pullDelay = Duration.zero;

  setUp(() {
    repository = MockMessageRepository();
    pullCount = 0;
    pullDelay = Duration.zero;

    when(() => repository.watchChanges())
        .thenAnswer((_) => const Stream<void>.empty());
    when(() => repository.watchInbound())
        .thenAnswer((_) => const Stream<List<Message>>.empty());
    when(() => repository.pullMessages(any())).thenAnswer((_) async {
      pullCount++;
      if (pullDelay > Duration.zero) await Future<void>.delayed(pullDelay);
      return 0;
    });
    when(() => repository.loadConversations(any()))
        .thenAnswer((_) async => const []);
  });

  InboxBloc buildBloc({
    Duration pollInterval = const Duration(milliseconds: 60),
    Duration resumeThrottle = InboxBloc.defaultResumeThrottle,
  }) =>
      InboxBloc(
        repository: repository,
        pollInterval: pollInterval,
        resumeThrottle: resumeThrottle,
      );

  group('InboxBloc lifecycle-aware polling', () {
    test('poll timer keeps pulling while active', () async {
      final bloc = buildBloc();
      addTearDown(bloc.close);

      bloc.add(const LoadInbox('user-1'));
      // Initial load pull + at least two timer ticks.
      await waitForOrFail(() => pullCount >= 3);

      expect(bloc.state, isA<InboxLoaded>());
    });

    test('InboxPaused stops the poll timer', () async {
      final bloc = buildBloc();
      addTearDown(bloc.close);

      bloc.add(const LoadInbox('user-1'));
      await waitForOrFail(() => pullCount >= 1);

      bloc.add(const InboxPaused());
      // Let the pause land, then give the (dead) timer time to have fired.
      await Future<void>.delayed(const Duration(milliseconds: 30));
      final countAfterPause = pullCount;
      await Future<void>.delayed(const Duration(milliseconds: 200));

      expect(
        pullCount,
        countAfterPause,
        reason: 'no pulls may happen while paused',
      );
    });

    test('InboxResumed restarts the timer and pulls immediately', () async {
      final bloc = buildBloc(resumeThrottle: Duration.zero);
      addTearDown(bloc.close);

      bloc.add(const LoadInbox('user-1'));
      await waitForOrFail(() => pullCount >= 1);
      bloc.add(const InboxPaused());
      await Future<void>.delayed(const Duration(milliseconds: 30));
      final countAfterPause = pullCount;

      bloc.add(const InboxResumed());
      // Immediate catch-up pull (no throttle) …
      await waitForOrFail(() => pullCount >= countAfterPause + 1);
      // … and the timer ticks again afterwards.
      await waitForOrFail(() => pullCount >= countAfterPause + 2);
    });

    test('InboxResumed skips the immediate pull when fresh (throttle)',
        () async {
      final bloc = buildBloc(
        pollInterval: const Duration(seconds: 60),
        resumeThrottle: const Duration(seconds: 60),
      );
      addTearDown(bloc.close);

      bloc.add(const LoadInbox('user-1'));
      await waitForOrFail(() => pullCount >= 1);

      bloc.add(const InboxPaused());
      bloc.add(const InboxResumed());
      await Future<void>.delayed(const Duration(milliseconds: 100));

      expect(
        pullCount,
        1,
        reason: 'a resume right after a pull must not pull again',
      );
    });

    test('InboxResumed before any LoadInbox is a no-op', () async {
      final bloc = buildBloc();
      addTearDown(bloc.close);

      bloc.add(const InboxResumed());
      await Future<void>.delayed(const Duration(milliseconds: 150));

      expect(pullCount, 0);
      expect(bloc.state, isA<InboxInitial>());
    });

    test('InboxReset returns to initial and stops all pulling', () async {
      final bloc = buildBloc();
      addTearDown(bloc.close);

      bloc.add(const LoadInbox('user-1'));
      await waitForOrFail(() => bloc.state is InboxLoaded);

      bloc.add(const InboxReset());
      await waitForOrFail(() => bloc.state is InboxInitial);
      final countAfterReset = pullCount;
      await Future<void>.delayed(const Duration(milliseconds: 200));

      expect(
        pullCount,
        countAfterReset,
        reason: 'no pulls may happen after reset (signed out)',
      );
    });

    test('burst of poll requests coalesces into at most two pulls', () async {
      pullDelay = const Duration(milliseconds: 120);
      final bloc = buildBloc(pollInterval: const Duration(seconds: 60));
      addTearDown(bloc.close);

      bloc.add(const LoadInbox('user-1'));
      await waitForOrFail(() => pullCount >= 1); // first pull is in flight

      // An FCM burst: several pushes each request a poll while one is running.
      bloc.add(const InboxPollRequested());
      bloc.add(const InboxPollRequested());
      bloc.add(const InboxPollRequested());

      // In-flight pull + exactly ONE trailing pull, not one per request.
      await waitForOrFail(() => bloc.state is InboxLoaded);
      await Future<void>.delayed(const Duration(milliseconds: 400));
      expect(pullCount, 2);
    });
  });
}
