import 'dart:async';

/// App-wide signal that the server rejected the refresh token (400/401).
///
/// The token-refresh callback runs inside the Dio interceptor, below the
/// widget tree and before the AuthBloc exists, so it cannot talk to the bloc
/// directly. It raises [sessionExpired]; the AuthBloc listens and logs the
/// user out instead of leaving the UI signed in with wiped tokens. The API
/// client and the sync queue raise [accountGone] the same way when the server
/// says the account is deleted; the AuthBloc decides whether to wipe. The
/// interceptor raises [otherAccountSignedIn] when the stored session belongs
/// to another account than the one the app is signed in as (a web tab left
/// open after another tab signed someone else in).
///
/// [generation] lets a token refresh that outlives its session drop its
/// result instead of writing it back after a logout (FUP-82).
class SessionSignals {
  SessionSignals._();

  /// Shared instance used by the app. Tests may create their own via
  /// [SessionSignals.forTesting].
  static final SessionSignals instance = SessionSignals._();

  /// A separate instance for tests.
  factory SessionSignals.forTesting() => SessionSignals._();

  final StreamController<void> _expired = StreamController<void>.broadcast();
  final StreamController<String> _accountGone =
      StreamController<String>.broadcast();
  final StreamController<void> _otherAccount =
      StreamController<void>.broadcast();
  int _generation = 0;

  /// Moves on each time this app instance clears or replaces the stored
  /// session (logout, expiry, deletion, a new sign-in). A token refresh notes
  /// it before its request and writes the new tokens only if it has not
  /// moved on: a refresh that answers after a logout must not put the old
  /// session back, nor write over the next user's.
  int get generation => _generation;

  /// Report that the stored session is being cleared or replaced. Call it
  /// before the first storage write or delete.
  void sessionChanged() => _generation++;

  /// Fires each time the server rejects the refresh token.
  Stream<void> get onSessionExpired => _expired.stream;

  /// Report that the refresh token was rejected.
  void sessionExpired() {
    if (!_expired.isClosed) _expired.add(null);
  }

  /// Fires with the server's `deletionState` each time a request is refused
  /// because the account is being or has been deleted (`410 ACCOUNT_004`).
  Stream<String> get onAccountGone => _accountGone.stream;

  /// Report a `410 ACCOUNT_004` refusal and its `deletionState`.
  void accountGone(String deletionState) {
    if (!_accountGone.isClosed) _accountGone.add(deletionState);
  }

  /// Fires each time a request is refused because the stored session (on
  /// web, shared by every tab) belongs to another account (FUP-96).
  Stream<void> get onOtherAccountSignedIn => _otherAccount.stream;

  /// Report that the stored session is another account's.
  void otherAccountSignedIn() {
    if (!_otherAccount.isClosed) _otherAccount.add(null);
  }
}
