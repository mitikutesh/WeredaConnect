import 'dart:math';

import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/account/data/repositories/account_deletion_repository.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_event.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_state.dart';

/// Runs "Delete account" (specs/contracts/account-deletion.md).
///
/// Online only: offline it says so and sends nothing; nothing is ever queued
/// for sync. A deletion the server started but did not finish (`503`) is
/// sent again automatically with backoff. On success [onDeleted] wipes the
/// device and signs out; a missing server account ([onAccountMissing]) only
/// signs out. No other outcome touches local data.
///
/// Each send carries [bearerFor]'s token: [userId]'s own ID token, read
/// right before the first send and before every resume. When there is none
/// (another account signed in on a shared phone, or in another tab on web)
/// the bloc stops without sending and says so. A `200` naming another
/// account is not a success.
class AccountDeletionBloc
    extends Bloc<AccountDeletionEvent, AccountDeletionState> {
  final AccountDeletionRepository _repository;
  final String _userId;
  final Future<bool> Function() _isOnline;
  final Future<String?> Function() _bearerFor;
  final void Function() _onDeleted;
  final void Function() _onAccountMissing;
  final Future<void> Function(Duration) _wait;

  /// How many times a started deletion is resumed before the user is asked
  /// to try again. With the server's 2 s hint that is about 30 s in all.
  static const maxResumeAttempts = 4;

  /// Upper bound for one wait between resumes.
  static const maxResumeDelay = Duration(seconds: 30);

  AccountDeletionBloc({
    required AccountDeletionRepository repository,
    required String userId,
    required Future<bool> Function() isOnline,
    required Future<String?> Function() bearerFor,
    required void Function() onDeleted,
    required void Function() onAccountMissing,
    Future<void> Function(Duration)? wait,
  })  : _repository = repository,
        _userId = userId,
        _isOnline = isOnline,
        _bearerFor = bearerFor,
        _onDeleted = onDeleted,
        _onAccountMissing = onAccountMissing,
        _wait = wait ?? Future<void>.delayed,
        super(const AccountDeletionIdle()) {
    on<AccountDeletionRequested>(_onRequested);
    on<AccountDeletionStepUpCancelled>(
      (event, emit) => emit(const AccountDeletionIdle()),
    );
  }

  /// The wait before resume [attempt] (1-based): the server's hint, doubled
  /// on each further try.
  static Duration resumeDelay(int retryAfterSeconds, int attempt) {
    final seconds = retryAfterSeconds * pow(2, attempt - 1);
    return Duration(seconds: min(seconds.toInt(), maxResumeDelay.inSeconds));
  }

  Future<String?> _bearer() async {
    try {
      return await _bearerFor();
    } on Object {
      return null;
    }
  }

  Future<void> _onRequested(
    AccountDeletionRequested event,
    Emitter<AccountDeletionState> emit,
  ) async {
    if (state is AccountDeletionSubmitting) return;
    emit(const AccountDeletionSubmitting());

    bool online;
    try {
      online = await _isOnline();
    } on Object {
      online = false;
    }
    if (!online) {
      emit(const AccountDeletionFailed(AccountDeletionFailure.offline));
      return;
    }

    var resumes = 0;
    while (true) {
      final bearer = await _bearer();
      if (bearer == null) {
        emit(const AccountDeletionFailed(AccountDeletionFailure.otherAccount));
        return;
      }
      final result =
          await _repository.deleteAccount(userId: _userId, bearer: bearer);
      switch (result) {
        case DeleteAccountDone():
          _onDeleted();
          emit(const AccountDeletionSucceeded());
          return;
        case DeleteAccountBlocked(:final blockers, :final blockerCount):
          emit(
            AccountDeletionBlocked(
              blockers: blockers,
              blockerCount: blockerCount,
            ),
          );
          return;
        case DeleteAccountStepUpRequired():
          emit(const AccountDeletionStepUpRequired());
          return;
        case DeleteAccountPending(:final retryAfterSeconds):
          if (resumes >= maxResumeAttempts) {
            emit(
              const AccountDeletionFailed(AccountDeletionFailure.incomplete),
            );
            return;
          }
          resumes++;
          emit(const AccountDeletionSubmitting(finishing: true));
          // Keeps going if the screen was closed meanwhile: the server has
          // started, so a finished deletion must still wipe the device. It
          // stops at the next account check once the user signed out.
          await _wait(resumeDelay(retryAfterSeconds, resumes));
        case DeleteAccountFailed(:final reason):
          if (reason == AccountDeletionFailure.accountNotFound) {
            _onAccountMissing();
          }
          emit(AccountDeletionFailed(reason));
          return;
      }
    }
  }
}
