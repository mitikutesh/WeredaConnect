import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/features/account/data/repositories/account_deletion_repository.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_bloc.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_event.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_state.dart';

import '../../../helpers/wait_for.dart';

class MockAccountDeletionRepository extends Mock
    implements AccountDeletionRepository {}

const _blockers = [
  AccountDeletionBlocker(type: 'order', id: 'ord-1'),
  AccountDeletionBlocker(type: 'escrow', id: 'escrow-order-ord-1'),
  AccountDeletionBlocker(type: 'delivery', id: 'del-1'),
  AccountDeletionBlocker(type: 'dispute', id: 'ord-2'),
  AccountDeletionBlocker(type: 'payment_review', id: 'ord-3'),
  AccountDeletionBlocker(type: 'future_type', id: 'x'),
];

/// The account the screen was opened for, and its ID token.
const _me = 'user-1';
const _token = 'id-token-of-user-1';

void main() {
  late MockAccountDeletionRepository repository;
  late bool online;
  late bool sameAccount;
  late int deleted;
  late int missing;
  late List<Duration> waits;

  setUp(() {
    repository = MockAccountDeletionRepository();
    online = true;
    sameAccount = true;
    deleted = 0;
    missing = 0;
    waits = [];
  });

  AccountDeletionBloc build() {
    final bloc = AccountDeletionBloc(
      repository: repository,
      userId: _me,
      isOnline: () async => online,
      bearerFor: () async => sameAccount ? _token : null,
      onDeleted: () => deleted++,
      onAccountMissing: () => missing++,
      wait: (d) async => waits.add(d),
    );
    addTearDown(bloc.close);
    return bloc;
  }

  void answer(List<DeleteAccountResult> results) {
    final queue = [...results];
    when(() => repository.deleteAccount(userId: _me, bearer: _token))
        .thenAnswer((_) async => queue.removeAt(0));
  }

  /// Sends one request and returns every state up to the settled one.
  Future<List<AccountDeletionState>> request(AccountDeletionBloc bloc) async {
    final states = <AccountDeletionState>[];
    final sub = bloc.stream.listen(states.add);
    bloc.add(const AccountDeletionRequested());
    await bloc.stream
        .firstWhere((s) => s is! AccountDeletionSubmitting)
        .timeout(const Duration(seconds: 2));
    await Future<void>.delayed(Duration.zero);
    await sub.cancel();
    return states;
  }

  test('starts idle', () {
    expect(build().state, const AccountDeletionIdle());
  });

  test('offline: says so, sends nothing and wipes nothing', () async {
    online = false;
    final bloc = build();

    final states = await request(bloc);

    expect(states, [
      const AccountDeletionSubmitting(),
      const AccountDeletionFailed(AccountDeletionFailure.offline),
    ]);
    verifyNever(() => repository.deleteAccount(userId: _me, bearer: _token));
    expect(deleted, 0);
  });

  test('a connectivity check that throws counts as offline', () async {
    final bloc = AccountDeletionBloc(
      repository: repository,
      userId: _me,
      isOnline: () async => throw StateError('no plugin'),
      bearerFor: () async => _token,
      onDeleted: () => deleted++,
      onAccountMissing: () => missing++,
    );
    addTearDown(bloc.close);

    final states = await request(bloc);

    expect(
      states.last,
      const AccountDeletionFailed(AccountDeletionFailure.offline),
    );
    verifyNever(() => repository.deleteAccount(userId: _me, bearer: _token));
  });

  test('success: starts the wipe once, then reports success', () async {
    answer([const DeleteAccountDone()]);
    final bloc = build();

    final states = await request(bloc);

    expect(states, [
      const AccountDeletionSubmitting(),
      const AccountDeletionSucceeded(),
    ]);
    expect(deleted, 1);
    expect(missing, 0);
  });

  test('blocked: lists every blocker type and wipes nothing', () async {
    answer([
      const DeleteAccountBlocked(blockers: _blockers, blockerCount: 30),
    ]);
    final bloc = build();

    final states = await request(bloc);

    expect(
      states.last,
      const AccountDeletionBlocked(blockers: _blockers, blockerCount: 30),
    );
    expect(deleted, 0);
  });

  test('step-up required: asks for the password and wipes nothing', () async {
    answer([const DeleteAccountStepUpRequired()]);
    final bloc = build();

    final states = await request(bloc);

    expect(states.last, const AccountDeletionStepUpRequired());
    expect(deleted, 0);
  });

  test('step-up confirmed: the same request goes out again', () async {
    answer([const DeleteAccountStepUpRequired(), const DeleteAccountDone()]);
    final bloc = build();

    await request(bloc);
    final states = await request(bloc);

    expect(states.last, const AccountDeletionSucceeded());
    verify(() => repository.deleteAccount(userId: _me, bearer: _token))
        .called(2);
    expect(deleted, 1);
  });

  test('step-up cancelled: back to idle', () async {
    answer([const DeleteAccountStepUpRequired()]);
    final bloc = build();
    await request(bloc);

    bloc.add(const AccountDeletionStepUpCancelled());
    await bloc.stream.firstWhere((s) => s is AccountDeletionIdle);

    expect(deleted, 0);
  });

  test('503 resumes with backoff until the server finishes', () async {
    answer([
      const DeleteAccountPending(),
      const DeleteAccountPending(retryAfterSeconds: 3),
      const DeleteAccountDone(),
    ]);
    final bloc = build();

    final states = await request(bloc);

    expect(states, [
      const AccountDeletionSubmitting(),
      const AccountDeletionSubmitting(finishing: true),
      const AccountDeletionSucceeded(),
    ]);
    // The server's hint, doubled on the second try.
    expect(waits, [const Duration(seconds: 2), const Duration(seconds: 6)]);
    expect(deleted, 1);
  });

  test('503 that never finishes stops after the retry budget', () async {
    answer(
      List.filled(
        AccountDeletionBloc.maxResumeAttempts + 1,
        const DeleteAccountPending(),
      ),
    );
    final bloc = build();

    final states = await request(bloc);

    expect(
      states.last,
      const AccountDeletionFailed(AccountDeletionFailure.incomplete),
    );
    verify(() => repository.deleteAccount(userId: _me, bearer: _token))
        .called(AccountDeletionBloc.maxResumeAttempts + 1);
    expect(waits, hasLength(AccountDeletionBloc.maxResumeAttempts));
    expect(deleted, 0);
    expect((states.last as AccountDeletionFailed).canRetry, isTrue);
  });

  test('a started deletion still finishes and wipes after the screen closed',
      () async {
    answer([const DeleteAccountPending(), const DeleteAccountDone()]);
    late AccountDeletionBloc bloc;
    bloc = AccountDeletionBloc(
      repository: repository,
      userId: _me,
      isOnline: () async => true,
      bearerFor: () async => _token,
      onDeleted: () => deleted++,
      onAccountMissing: () => missing++,
      // The user leaves the screen while the app waits to resume.
      wait: (_) => bloc.close(),
    );

    bloc.add(const AccountDeletionRequested());
    await waitFor(() => deleted == 1);

    expect(bloc.isClosed, isTrue);
    verify(() => repository.deleteAccount(userId: _me, bearer: _token))
        .called(2);
    expect(deleted, 1);
  });

  // Review item 1: the request carries whatever session the device holds.
  test('another account signed in: nothing is sent, nothing is wiped',
      () async {
    sameAccount = false;
    answer([const DeleteAccountDone()]);
    final bloc = build();

    final states = await request(bloc);

    expect(states, [
      const AccountDeletionSubmitting(),
      const AccountDeletionFailed(AccountDeletionFailure.otherAccount),
    ]);
    expect((states.last as AccountDeletionFailed).canRetry, isFalse);
    verifyNever(
      () => repository.deleteAccount(
        userId: any(named: 'userId'),
        bearer: any(named: 'bearer'),
      ),
    );
    expect(deleted, 0);
  });

  test('the account changed during a resume: stops before sending again',
      () async {
    final queue = <DeleteAccountResult>[
      const DeleteAccountPending(),
      const DeleteAccountDone(),
    ];
    when(() => repository.deleteAccount(userId: _me, bearer: _token))
        .thenAnswer((_) async {
      // The user signs out, and someone else signs in, while it waits.
      sameAccount = false;
      return queue.removeAt(0);
    });
    final bloc = build();

    final states = await request(bloc);

    expect(
      states.last,
      const AccountDeletionFailed(AccountDeletionFailure.otherAccount),
    );
    verify(() => repository.deleteAccount(userId: _me, bearer: _token))
        .called(1);
    expect(deleted, 0);
  });

  test('a confirmed deletion still wipes if the user signed out meanwhile',
      () async {
    when(() => repository.deleteAccount(userId: _me, bearer: _token))
        .thenAnswer((_) async {
      sameAccount = false;
      return const DeleteAccountDone();
    });
    final bloc = build();

    await request(bloc);

    expect(deleted, 1, reason: 'the server confirmed this account is gone');
  });

  test('a failing account check counts as another account', () async {
    final bloc = AccountDeletionBloc(
      repository: repository,
      userId: _me,
      isOnline: () async => true,
      bearerFor: () async => throw StateError('storage'),
      onDeleted: () => deleted++,
      onAccountMissing: () => missing++,
    );
    addTearDown(bloc.close);

    final states = await request(bloc);

    expect(
      states.last,
      const AccountDeletionFailed(AccountDeletionFailure.otherAccount),
    );
    verifyNever(
      () => repository.deleteAccount(
        userId: any(named: 'userId'),
        bearer: any(named: 'bearer'),
      ),
    );
  });

  test('resume delay is capped', () {
    expect(
      AccountDeletionBloc.resumeDelay(20, 4),
      AccountDeletionBloc.maxResumeDelay,
    );
    expect(AccountDeletionBloc.resumeDelay(2, 1), const Duration(seconds: 2));
  });

  test('network error mid-request: offers a retry, never a success', () async {
    answer([
      const DeleteAccountFailed(AccountDeletionFailure.network),
      const DeleteAccountDone(),
    ]);
    final bloc = build();

    final first = await request(bloc);
    expect(
      first.last,
      const AccountDeletionFailed(AccountDeletionFailure.network),
    );
    expect(deleted, 0);

    final retried = await request(bloc);
    expect(retried.last, const AccountDeletionSucceeded());
    expect(deleted, 1);
  });

  test('suspended account cannot retry', () async {
    answer([const DeleteAccountFailed(AccountDeletionFailure.suspended)]);
    final bloc = build();

    final states = await request(bloc);

    final failed = states.last as AccountDeletionFailed;
    expect(failed.reason, AccountDeletionFailure.suspended);
    expect(failed.canRetry, isFalse);
    expect(deleted, 0);
  });

  test('missing server account signs out without the deletion wipe', () async {
    answer([const DeleteAccountFailed(AccountDeletionFailure.accountNotFound)]);
    final bloc = build();

    await request(bloc);

    expect(missing, 1);
    expect(deleted, 0);
  });

  test('a second request while one is running is ignored', () async {
    final pending = Completer<DeleteAccountResult>();
    when(() => repository.deleteAccount(userId: _me, bearer: _token))
        .thenAnswer((_) => pending.future);
    final bloc = build();

    bloc.add(const AccountDeletionRequested());
    await bloc.stream.firstWhere((s) => s is AccountDeletionSubmitting);
    bloc.add(const AccountDeletionRequested());
    await Future<void>.delayed(Duration.zero);
    pending.complete(const DeleteAccountDone());
    await bloc.stream.firstWhere((s) => s is AccountDeletionSucceeded);

    verify(() => repository.deleteAccount(userId: _me, bearer: _token))
        .called(1);
    expect(deleted, 1);
  });
}
