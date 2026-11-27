import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/auth/session_signals.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_bloc.dart';
import 'package:wereda_connect/features/account/presentation/screens/delete_account_screen.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// FUP-104: offers "Finish deleting" when a request is refused with
/// `410 ACCOUNT_004` while the account's deletion is still `in_progress`
/// (`/sync`, escrow, Fayda, device tokens; the delete itself answers on its
/// own screen). The yes opens Delete account, which sends the deletion again
/// at once: the server resumes it without a new password
/// (account-deletion.md). Any other state is the AuthBloc's wipe.
///
/// Sits around the signed-in app on mobile and web, the same for Farmer,
/// Trucker and Buyer. One prompt at a time and none while a Delete account
/// screen is open; "Not now" quiets it for [cooldown] for that account, so
/// later background syncs do not ask again. Only the signed-in account, when
/// the stored session is also its own (web tabs share it), is asked. A
/// sign-out (the finished deletion's wipe among them) closes the prompt or
/// the screen it opened.
class FinishDeletionListener extends StatefulWidget {
  final Widget child;

  /// The `deletionState` of each `410 ACCOUNT_004`; defaults to
  /// [SessionSignals.onAccountGone].
  final Stream<String>? accountGone;

  /// Replaces the Delete account screen the prompt opens (tests).
  final WidgetBuilder? deleteScreen;

  /// The clock of the cooldown (tests).
  final DateTime Function()? now;

  static const cooldown = Duration(minutes: 10);

  const FinishDeletionListener({
    required this.child,
    this.accountGone,
    this.deleteScreen,
    this.now,
    super.key,
  });

  @override
  State<FinishDeletionListener> createState() => _FinishDeletionListenerState();
}

class _FinishDeletionListenerState extends State<FinishDeletionListener> {
  StreamSubscription<String>? _sub;
  bool _showing = false;
  String? _declinedFor;
  DateTime? _declinedAt;

  /// The prompt or the screen this listener has open.
  Route<Object?>? _open;

  DateTime _now() => (widget.now ?? DateTime.now)();

  @override
  void initState() {
    super.initState();
    _sub = (widget.accountGone ?? SessionSignals.instance.onAccountGone)
        .listen(_onAccountGone);
  }

  @override
  void dispose() {
    _sub?.cancel();
    super.dispose();
  }

  bool _quietFor(String userId) {
    final declinedAt = _declinedAt;
    return _declinedFor == userId &&
        declinedAt != null &&
        _now().difference(declinedAt) < FinishDeletionListener.cooldown;
  }

  Future<void> _onAccountGone(String deletionState) async {
    if (deletionState != deletionInProgressState) return;
    if (_showing || !mounted || AccountDeletionBloc.openCount > 0) return;
    final auth = context.read<AuthBloc>();
    final session = auth.state;
    if (session is! AuthAuthenticated || _quietFor(session.userId)) return;
    final userId = session.userId;
    _showing = true;
    try {
      if (!await auth.isSignedInAs(userId) || !mounted) return;
      final finish = await _show<bool>(
        DialogRoute<bool>(context: context, builder: _prompt),
      );
      // Closed by a sign-out: that is no answer.
      if (!mounted || auth.state is! AuthAuthenticated) return;
      if (finish != true) {
        _declinedFor = userId;
        _declinedAt = _now();
        return;
      }
      await _show<void>(
        MaterialPageRoute<void>(
          builder: widget.deleteScreen ??
              (_) => const DeleteAccountScreen(resume: true),
        ),
      );
    } finally {
      _showing = false;
    }
  }

  Future<T?> _show<T>(Route<T> route) async {
    _open = route;
    try {
      return await Navigator.of(context, rootNavigator: true).push(route);
    } finally {
      if (identical(_open, route)) _open = null;
    }
  }

  void _close() {
    final route = _open;
    _open = null;
    if (route != null && route.isActive) route.navigator?.removeRoute(route);
  }

  Widget _prompt(BuildContext dialogContext) {
    final l10n = AppLocalizations.of(dialogContext)!;
    return AlertDialog(
      key: const Key('finish_deleting_prompt'),
      title: Text(l10n.finishDeletingTitle),
      content: Text(l10n.finishDeletingBody),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(dialogContext).pop(false),
          child: Text(l10n.finishDeletingLater),
        ),
        FilledButton(
          key: const Key('finish_deleting_button'),
          onPressed: () => Navigator.of(dialogContext).pop(true),
          style: FilledButton.styleFrom(
            backgroundColor: AppTheme.statusError,
            foregroundColor: Colors.white,
            minimumSize: const Size(0, 48),
          ),
          child: Text(l10n.finishDeletingButton),
        ),
      ],
    );
  }

  @override
  Widget build(BuildContext context) {
    return BlocListener<AuthBloc, AuthState>(
      listenWhen: (_, state) => state is AuthUnauthenticated,
      listener: (_, __) => _close(),
      child: widget.child,
    );
  }
}
