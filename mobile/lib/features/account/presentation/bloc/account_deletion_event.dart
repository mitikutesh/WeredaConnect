import 'package:equatable/equatable.dart';

abstract class AccountDeletionEvent extends Equatable {
  const AccountDeletionEvent();

  @override
  List<Object?> get props => [];
}

/// The user confirmed: send `DELETE /user/account`. Also sent again after a
/// step-up and from the retry button.
class AccountDeletionRequested extends AccountDeletionEvent {
  const AccountDeletionRequested();
}

/// The user closed the password prompt without confirming.
class AccountDeletionStepUpCancelled extends AccountDeletionEvent {
  const AccountDeletionStepUpCancelled();
}
