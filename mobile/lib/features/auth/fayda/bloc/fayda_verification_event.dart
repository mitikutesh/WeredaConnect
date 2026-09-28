import 'package:equatable/equatable.dart';

/// Events for [FaydaVerificationBloc].
sealed class FaydaVerificationEvent extends Equatable {
  const FaydaVerificationEvent();

  @override
  List<Object?> get props => [];
}

/// The user tapped "Verify with Fayda": ask the server for an authorize URL.
class FaydaVerificationStarted extends FaydaVerificationEvent {
  const FaydaVerificationStarted();
}

/// The browser could not be opened for the authorize URL.
class FaydaBrowserLaunchFailed extends FaydaVerificationEvent {
  const FaydaBrowserLaunchFailed();
}

/// A callback arrived (only the Fayda callback, see `FaydaOidcService.isCallbackUri`, is handled).
class FaydaCallbackReceived extends FaydaVerificationEvent {
  final Uri uri;

  const FaydaCallbackReceived(this.uri);

  @override
  List<Object?> get props => [uri];
}

/// The app came back from the browser and no callback followed.
class FaydaBrowserReturnedWithoutCallback extends FaydaVerificationEvent {
  const FaydaBrowserReturnedWithoutCallback();
}

/// The Fayda state expired (fayda-start's `expiresAt`) with no callback.
class FaydaCallbackTimedOut extends FaydaVerificationEvent {
  const FaydaCallbackTimedOut();
}

/// The user cancelled while waiting for the browser.
class FaydaVerificationCancelled extends FaydaVerificationEvent {
  const FaydaVerificationCancelled();
}
