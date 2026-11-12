import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/reputation/domain/models/reputation_event.dart';
import 'package:wereda_connect/features/reputation/presentation/bloc/my_reputation_bloc.dart';
import 'package:wereda_connect/features/reputation/presentation/bloc/my_reputation_event.dart';
import 'package:wereda_connect/features/reputation/presentation/bloc/my_reputation_state.dart';

import '../../../helpers/wait_for.dart';
import '../reputation_fixtures.dart';

/// ESC-31: the My reputation screen's bloc. Only the caller's own data:
/// every read names the bloc's own user id.
void main() {
  late MockReputationRepository repository;
  late MyReputationBloc bloc;
  late List<MyReputationState> states;
  late StreamSubscription<MyReputationState> subscription;

  setUp(() {
    repository = MockReputationRepository();
    bloc = MyReputationBloc(repository: repository, userId: 'f-1');
    states = [];
    subscription = bloc.stream.listen(states.add);
  });

  tearDown(() async {
    await subscription.cancel();
    await bloc.close();
  });

  void answerOwn({bool penaltiesEnabled = true, bool stale = false}) => when(
        () => repository.loadOwn('f-1', force: any(named: 'force')),
      ).thenAnswer(
        (_) async => snapshot(
          ownReputation(penaltiesEnabled: penaltiesEnabled, points: 4),
          stale: stale,
        ),
      );

  Future<void> settle() => waitFor(
        () =>
            bloc.state.status != ReputationLoad.loading &&
            (bloc.state.status == ReputationLoad.unavailable ||
                bloc.state.eventsStatus != ReputationLoad.loading),
      );

  test('loads the standing, then the first page of events', () async {
    answerOwn();
    when(() => repository.loadEvents('f-1')).thenAnswer(
      (_) async => eventsResult(
        [reputationEvent('CANCEL#ord-1')],
        nextToken: 'tok-2',
      ),
    );

    bloc.add(const MyReputationRequested());
    await settle();

    expect(bloc.state.status, ReputationLoad.ready);
    expect(bloc.state.reputation!.activePoints, 4);
    expect(bloc.state.stale, isFalse);
    expect(bloc.state.eventsStatus, ReputationLoad.ready);
    expect(bloc.state.events.single.eventId, 'CANCEL#ord-1');
    expect(bloc.state.nextToken, 'tok-2');
    // The standing was shown before the events arrived.
    expect(
      states.any(
        (s) =>
            s.status == ReputationLoad.ready &&
            s.eventsStatus == ReputationLoad.loading,
      ),
      isTrue,
    );
    verify(() => repository.loadOwn('f-1')).called(1);
  });

  test('with penalties switched off no events are asked for', () async {
    answerOwn(penaltiesEnabled: false);

    bloc.add(const MyReputationRequested());
    await settle();

    expect(bloc.state.status, ReputationLoad.ready);
    expect(bloc.state.eventsStatus, ReputationLoad.ready);
    expect(bloc.state.events, isEmpty);
    verifyNever(
      () => repository.loadEvents(any(), nextToken: any(named: 'nextToken')),
    );
  });

  test('offline with nothing saved is unavailable', () async {
    when(() => repository.loadOwn('f-1', force: any(named: 'force')))
        .thenThrow(const NetworkException());

    bloc.add(const MyReputationRequested());
    await settle();

    expect(bloc.state.status, ReputationLoad.unavailable);
    expect(bloc.state.reputation, isNull);
    verifyNever(
      () => repository.loadEvents(any(), nextToken: any(named: 'nextToken')),
    );
  });

  test('the saved copy is shown as stale', () async {
    answerOwn(stale: true);
    when(() => repository.loadEvents('f-1')).thenAnswer(
      (_) async => eventsResult([reputationEvent('CANCEL#ord-1')], stale: true),
    );

    bloc.add(const MyReputationRequested());
    await settle();

    expect(bloc.state.status, ReputationLoad.ready);
    expect(bloc.state.stale, isTrue);
    expect(bloc.state.events, hasLength(1));
  });

  test('the standing stays when the history cannot be read', () async {
    answerOwn();
    when(() => repository.loadEvents('f-1'))
        .thenThrow(const NetworkException());

    bloc.add(const MyReputationRequested());
    await settle();

    expect(bloc.state.status, ReputationLoad.ready);
    expect(bloc.state.eventsStatus, ReputationLoad.unavailable);
  });

  test('a refresh forces the read and keeps what is shown meanwhile', () async {
    answerOwn();
    when(() => repository.loadEvents('f-1')).thenAnswer(
      (_) async => eventsResult([reputationEvent('CANCEL#ord-1')]),
    );
    bloc.add(const MyReputationRequested());
    await settle();
    states.clear();

    bloc.add(const MyReputationRequested(force: true));
    await waitFor(
      () => verifyCalled(() => repository.loadOwn('f-1', force: true)),
    );
    await settle();

    expect(states.every((s) => s.reputation != null), isTrue);
    expect(states.every((s) => s.status == ReputationLoad.ready), isTrue);
  });

  test('a refresh that fails keeps the standing and marks it stale', () async {
    answerOwn();
    when(() => repository.loadEvents('f-1')).thenAnswer(
      (_) async => eventsResult([reputationEvent('CANCEL#ord-1')]),
    );
    bloc.add(const MyReputationRequested());
    await settle();
    when(() => repository.loadOwn('f-1', force: true))
        .thenThrow(const NetworkException());

    bloc.add(const MyReputationRequested(force: true));
    await waitFor(() => bloc.state.stale);

    expect(bloc.state.status, ReputationLoad.ready);
    expect(bloc.state.reputation, isNotNull);
    expect(bloc.state.stale, isTrue);
    expect(bloc.state.events, hasLength(1));
  });

  group('the next page', () {
    setUp(() async {
      answerOwn();
      when(() => repository.loadEvents('f-1')).thenAnswer(
        (_) async => eventsResult(
          [reputationEvent('CANCEL#ord-1')],
          nextToken: 'tok-2',
        ),
      );
      bloc.add(const MyReputationRequested());
      await settle();
    });

    test('is appended, without a row the first page already had', () async {
      when(() => repository.loadEvents('f-1', nextToken: 'tok-2')).thenAnswer(
        (_) async => eventsResult([
          reputationEvent('CANCEL#ord-1'),
          reputationEvent('CANCEL#ord-2'),
        ]),
      );

      bloc.add(const MyReputationMoreRequested());
      await waitFor(() => bloc.state.events.length == 2);

      expect(
        bloc.state.events.map((e) => e.eventId),
        ['CANCEL#ord-1', 'CANCEL#ord-2'],
      );
      expect(bloc.state.nextToken, isNull);
      expect(bloc.state.loadingMore, isFalse);
    });

    test('a failure keeps the list and the token to try again', () async {
      when(() => repository.loadEvents('f-1', nextToken: 'tok-2'))
          .thenThrow(const NetworkException());

      bloc.add(const MyReputationMoreRequested());
      await waitFor(() => bloc.state.moreFailed);

      expect(bloc.state.moreFailed, isTrue);
      expect(bloc.state.loadingMore, isFalse);
      expect(bloc.state.events, hasLength(1));
      expect(bloc.state.nextToken, 'tok-2');
    });
  });

  test('a sent appeal changes that event only', () async {
    answerOwn();
    when(() => repository.loadEvents('f-1')).thenAnswer(
      (_) async => eventsResult([
        reputationEvent('CANCEL#ord-1', canAppeal: true),
        reputationEvent('CANCEL#ord-2', canAppeal: true),
      ]),
    );
    bloc.add(const MyReputationRequested());
    await settle();

    bloc.add(
      const MyReputationAppealSent(
        eventId: 'CANCEL#ord-1',
        appeal: ReputationAppeal.sent,
      ),
    );
    await waitFor(() => bloc.state.events.first.appeal.status == 'pending');

    expect(bloc.state.events.first.appeal.status, 'pending');
    expect(bloc.state.events.first.appeal.canAppeal, isFalse);
    expect(bloc.state.events.last.appeal.canAppeal, isTrue);
  });
}

/// Whether [call] has happened at least once (for [waitFor]).
bool verifyCalled(void Function() call) {
  try {
    verify(call).called(greaterThanOrEqualTo(1));
    return true;
  } on TestFailure {
    return false;
  }
}
