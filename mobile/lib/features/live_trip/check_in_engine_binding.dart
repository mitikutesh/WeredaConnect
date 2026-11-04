import 'dart:async';

import 'package:flutter/widgets.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/live_trip/domain/check_in_engine.dart';

/// Keeps the check-in engine on the signed-in trucker and on the app's
/// visibility (LT-08). Only a trucker has a session: a farmer or a buyer
/// never shares a location, so their scans record nothing. A sign out halts
/// the engine (the foreground service stops with it); a deleted account also
/// forgets its sharing state. On web the lifecycle follows the page's
/// visibility, so the check-in timer runs only while the page is visible.
///
/// A sign out (or a deleted account) also drops every kept drop-off handoff
/// on the device ([handoffs]): on web that storage is the browser's, and a
/// handoff is the delivery's QR secret.
class CheckInEngineBinding with WidgetsBindingObserver {
  CheckInEngineBinding({
    required CheckInEngine engine,
    required AuthBloc authBloc,
    DropoffHandoffStore? handoffs,
  })  : _engine = engine,
        _authBloc = authBloc,
        _handoffs = handoffs;

  final CheckInEngine _engine;
  final AuthBloc _authBloc;
  final DropoffHandoffStore? _handoffs;
  StreamSubscription<AuthState>? _subscription;

  /// Start following the account and the lifecycle.
  void attach() {
    _apply(_authBloc.state);
    _subscription = _authBloc.stream.listen(_apply);
    WidgetsBinding.instance.addObserver(this);
  }

  /// Stop following them.
  Future<void> detach() async {
    WidgetsBinding.instance.removeObserver(this);
    await _subscription?.cancel();
    _subscription = null;
  }

  void _apply(AuthState state) {
    if (state is AuthAuthenticated) {
      unawaited(
        state.role == UserRole.trucker
            ? _engine.signIn(state.userId)
            : _engine.signOut(),
      );
    } else if (state is AuthAccountWiped) {
      unawaited(_engine.signOut(forget: true));
      unawaited(_handoffs?.forgetAll());
    } else if (state is AuthUnauthenticated) {
      unawaited(_engine.signOut());
      unawaited(_handoffs?.forgetAll());
    }
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // `inactive` is still on screen (web: the window lost focus).
    _engine.setAppVisible(
      state == AppLifecycleState.resumed || state == AppLifecycleState.inactive,
    );
  }
}
