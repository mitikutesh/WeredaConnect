import 'dart:async';

import 'package:wereda_connect/core/network/data_usage_tracker.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

/// Keeps [tracker] pointed at the signed-in account: a login switches to
/// that user's counters, a logout stops counting, and a deleted account's
/// counters are removed. Transient states (loading, errors, OTP prompts)
/// leave the current account in place.
StreamSubscription<AuthState> bindDataUsageToAuth(
  DataUsageTracker tracker,
  AuthBloc authBloc,
) {
  void apply(AuthState state) {
    if (state is AuthAuthenticated) {
      tracker.setUser(state.userId);
    } else if (state is AuthAccountWiped) {
      unawaited(tracker.forgetCurrentUser());
    } else if (state is AuthUnauthenticated) {
      tracker.setUser(null);
    }
  }

  apply(authBloc.state);
  return authBloc.stream.listen(apply);
}
