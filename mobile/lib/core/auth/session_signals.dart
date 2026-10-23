import 'dart:async';

/// App-wide signal that the server rejected the refresh token (400/401).
///
/// The token-refresh callback runs inside the Dio interceptor, below the
/// widget tree and before the AuthBloc exists, so it cannot talk to the bloc
/// directly. It raises [sessionExpired]; the AuthBloc listens and logs the
/// user out instead of leaving the UI signed in with wiped tokens. The API
/// client and the sync queue raise [accountGone] the same way when the server
/// says the account is deleted; the AuthBloc decides whether to wipe.
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
}
