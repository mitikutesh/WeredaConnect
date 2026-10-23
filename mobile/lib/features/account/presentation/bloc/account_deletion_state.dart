import 'package:equatable/equatable.dart';

import 'package:wereda_connect/features/account/data/repositories/account_deletion_repository.dart';

abstract class AccountDeletionState extends Equatable {
  const AccountDeletionState();

  @override
  List<Object?> get props => [];
}

/// Nothing sent yet (or the password prompt was cancelled).
class AccountDeletionIdle extends AccountDeletionState {
  const AccountDeletionIdle();
}

/// The request is on its way. [finishing] is true while the server resumes
/// a deletion it already started (`503 ACCOUNT_005`).
class AccountDeletionSubmitting extends AccountDeletionState {
  final bool finishing;

  const AccountDeletionSubmitting({this.finishing = false});

  @override
  List<Object?> get props => [finishing];
}

/// The server wants the password again (`AUTH_040`).
class AccountDeletionStepUpRequired extends AccountDeletionState {
  const AccountDeletionStepUpRequired();
}

/// Active items block the deletion (`409 ACCOUNT_002`).
class AccountDeletionBlocked extends AccountDeletionState {
  final List<AccountDeletionBlocker> blockers;
  final int blockerCount;

  const AccountDeletionBlocked({
    required this.blockers,
    required this.blockerCount,
  });

  @override
  List<Object?> get props => [blockers, blockerCount];
}

class AccountDeletionFailed extends AccountDeletionState {
  final AccountDeletionFailure reason;

  const AccountDeletionFailed(this.reason);

  /// Sending the same request again can help; for a suspended account, or
  /// while another account holds the session, it cannot.
  bool get canRetry =>
      reason != AccountDeletionFailure.suspended &&
      reason != AccountDeletionFailure.otherAccount;

  @override
  List<Object?> get props => [reason];
}

/// The account is deleted and the local wipe has been started.
class AccountDeletionSucceeded extends AccountDeletionState {
  const AccountDeletionSucceeded();
}
