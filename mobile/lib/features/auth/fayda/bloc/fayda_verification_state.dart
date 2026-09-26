import 'package:equatable/equatable.dart';

/// States for [FaydaVerificationBloc].
sealed class FaydaVerificationState extends Equatable {
  const FaydaVerificationState();

  @override
  List<Object?> get props => [];
}

/// Nothing in progress: show the "Verify with Fayda" button.
class FaydaVerificationIdle extends FaydaVerificationState {
  const FaydaVerificationIdle();
}

/// Asking the server for the authorize URL.
class FaydaVerificationStarting extends FaydaVerificationState {
  const FaydaVerificationStarting();
}

/// The screen should open [authorizeUrl] and wait for the deep link.
///
/// The wait lasts until [expiresAt] (the state's expiry from fayda-start) or
/// until the user cancels. [browserReturned] is set once the app came back
/// without the deep link: the screen then says it is still waiting instead
/// of failing, because the link can arrive late.
class FaydaAwaitingCallback extends FaydaVerificationState {
  final Uri authorizeUrl;
  final String state;
  final DateTime? expiresAt;
  final bool browserReturned;

  const FaydaAwaitingCallback({
    required this.authorizeUrl,
    required this.state,
    this.expiresAt,
    this.browserReturned = false,
  });

  /// The same wait, after the app came back without the deep link.
  FaydaAwaitingCallback returned() => FaydaAwaitingCallback(
        authorizeUrl: authorizeUrl,
        state: state,
        expiresAt: expiresAt,
        browserReturned: true,
      );

  @override
  List<Object?> get props => [authorizeUrl, state, expiresAt, browserReturned];
}

/// Callback received; linking the identity on the server.
class FaydaLinking extends FaydaVerificationState {
  const FaydaLinking();
}

/// The account is now Fayda-verified.
class FaydaVerificationSucceeded extends FaydaVerificationState {
  final String? verifiedName;
  final DateTime? verifiedAt;

  const FaydaVerificationSucceeded({this.verifiedName, this.verifiedAt});

  @override
  List<Object?> get props => [verifiedName, verifiedAt];
}

/// Verification failed; [errorCode] is a FAYDA_* / AUTH_* code.
class FaydaVerificationFailure extends FaydaVerificationState {
  final String errorCode;
  final int? retryAfterSeconds;

  const FaydaVerificationFailure({
    required this.errorCode,
    this.retryAfterSeconds,
  });

  @override
  List<Object?> get props => [errorCode, retryAfterSeconds];
}
