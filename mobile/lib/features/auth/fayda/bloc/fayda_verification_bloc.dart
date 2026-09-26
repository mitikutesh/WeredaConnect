import 'package:flutter/foundation.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/auth/fayda_oidc_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/features/auth/fayda/bloc/fayda_verification_event.dart';
import 'package:wereda_connect/features/auth/fayda/bloc/fayda_verification_state.dart';

export 'package:wereda_connect/features/auth/fayda/bloc/fayda_verification_event.dart';
export 'package:wereda_connect/features/auth/fayda/bloc/fayda_verification_state.dart';

/// Stores a successful link on this device (Isar user on mobile, cached JSON
/// on web) so the profile shows the verified badge offline.
typedef FaydaVerifiedRecorder = Future<void> Function(FaydaLinkResult result);

/// Link-later Fayda verification for a signed-in user.
///
/// Flow: [FaydaVerificationStarted] → POST /user/fayda-start →
/// [FaydaAwaitingCallback] (the screen opens the URL) → deep link
/// [FaydaCallbackReceived] → state check → POST /user/fayda-link →
/// [FaydaVerificationSucceeded].
class FaydaVerificationBloc
    extends Bloc<FaydaVerificationEvent, FaydaVerificationState> {
  FaydaVerificationBloc({
    required FaydaOidcService service,
    required FaydaVerifiedRecorder recordVerified,
  })  : _service = service,
        _recordVerified = recordVerified,
        super(const FaydaVerificationIdle()) {
    on<FaydaVerificationStarted>(_onStarted);
    on<FaydaBrowserLaunchFailed>(
      (_, emit) => emit(
        const FaydaVerificationFailure(
          errorCode: AuthErrorCode.faydaServiceUnavailable,
        ),
      ),
    );
    on<FaydaCallbackReceived>(_onCallback);
    on<FaydaBrowserReturnedWithoutCallback>(_onReturnedWithoutCallback);
    on<FaydaCallbackTimedOut>(_onTimedOut);
    on<FaydaVerificationCancelled>(
      (_, emit) => emit(const FaydaVerificationIdle()),
    );
  }

  final FaydaOidcService _service;
  final FaydaVerifiedRecorder _recordVerified;

  Future<void> _onStarted(
    FaydaVerificationStarted event,
    Emitter<FaydaVerificationState> emit,
  ) async {
    emit(const FaydaVerificationStarting());
    final start = await _service.startLink();
    if (!start.isSuccess) {
      emit(
        FaydaVerificationFailure(
          errorCode: start.errorCode ?? AuthErrorCode.faydaServiceUnavailable,
          retryAfterSeconds: start.retryAfterSeconds,
        ),
      );
      return;
    }
    emit(
      FaydaAwaitingCallback(
        authorizeUrl: start.authorizeUrl!,
        state: start.state!,
        expiresAt: start.expiresAt,
      ),
    );
  }

  Future<void> _onCallback(
    FaydaCallbackReceived event,
    Emitter<FaydaVerificationState> emit,
  ) async {
    if (!FaydaOidcService.isCallbackUri(event.uri)) return;
    final current = state;
    // A callback we did not start (or a replay after completion) is ignored.
    if (current is! FaydaAwaitingCallback) return;

    final callback = _service.parseCallback(event.uri);
    // CSRF check: the state must be the one fayda-start issued.
    if (callback.state != current.state) {
      emit(
        const FaydaVerificationFailure(
          errorCode: AuthErrorCode.faydaTokenExpired,
        ),
      );
      return;
    }
    if (!callback.isSuccess) {
      emit(
        const FaydaVerificationFailure(
          errorCode: AuthErrorCode.faydaVerificationFailed,
        ),
      );
      return;
    }

    emit(const FaydaLinking());
    final result =
        await _service.link(authCode: callback.code!, state: current.state);
    if (!result.isSuccess) {
      emit(
        FaydaVerificationFailure(
          errorCode: result.errorCode ?? AuthErrorCode.faydaVerificationFailed,
        ),
      );
      return;
    }

    try {
      await _recordVerified(result);
    } on Object catch (e) {
      // The server already linked it; the badge catches up on next login.
      debugPrint('[FaydaVerificationBloc] local update failed: $e');
    }
    emit(
      FaydaVerificationSucceeded(
        verifiedName: result.verifiedName,
        verifiedAt: result.faydaVerifiedAt,
      ),
    );
  }

  /// Back in the app without the deep link: keep waiting (it can arrive
  /// late) and let the screen say so. Only the state expiry or the user's
  /// cancel ends the wait.
  void _onReturnedWithoutCallback(
    FaydaBrowserReturnedWithoutCallback event,
    Emitter<FaydaVerificationState> emit,
  ) {
    final current = state;
    if (current is FaydaAwaitingCallback && !current.browserReturned) {
      emit(current.returned());
    }
  }

  void _onTimedOut(
    FaydaCallbackTimedOut event,
    Emitter<FaydaVerificationState> emit,
  ) {
    if (state is FaydaAwaitingCallback) {
      emit(
        const FaydaVerificationFailure(
          errorCode: AuthErrorCode.faydaTokenExpired,
        ),
      );
    }
  }
}
