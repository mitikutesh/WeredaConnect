import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/core/auth/models/auth_result.dart';
import 'package:wereda_connect/core/auth/presentation/auth_error_l10n.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Re-authenticates the signed-in user with their password.
typedef StepUpAuthenticator = Future<AuthResult> Function(
  String phoneNumber,
  String password,
);

/// "Confirm it's you" (AUTH_040 step-up).
///
/// Asks for the password (phone prefilled from the session), calls
/// POST /auth/login again and stores the new tokens exactly like a normal
/// login without clearing any local data. Resolves to true once the fresh
/// tokens are active, so the caller can retry its request. Also raises
/// [SecurityEvents.stepUpCompleted] so queued money ops retry.
///
/// [authenticate] defaults to the AuthBloc's AuthService (tests inject one).
Future<bool> showStepUpDialog(
  BuildContext context, {
  StepUpAuthenticator? authenticate,
  SecurityEvents? securityEvents,
}) async {
  final bloc = context.read<AuthBloc>();
  final authState = bloc.state;
  if (authState is! AuthAuthenticated) return false;
  final phone = authState.phoneNumber ?? '';
  if (phone.isEmpty) return false;

  final ok = await showDialog<bool>(
    context: context,
    barrierDismissible: false,
    builder: (_) => BlocProvider<AuthBloc>.value(
      value: bloc,
      child: _StepUpDialog(
        phoneNumber: phone,
        authenticate: authenticate ??
            (p, pw) =>
                bloc.authService.loginOnline(phoneNumber: p, password: pw),
      ),
    ),
  );
  if (ok == true) {
    (securityEvents ?? SecurityEvents.instance).stepUpCompleted();
    return true;
  }
  return false;
}

class _StepUpDialog extends StatefulWidget {
  final String phoneNumber;
  final StepUpAuthenticator authenticate;

  const _StepUpDialog({required this.phoneNumber, required this.authenticate});

  @override
  State<_StepUpDialog> createState() => _StepUpDialogState();
}

class _StepUpDialogState extends State<_StepUpDialog> {
  final _passwordController = TextEditingController();
  bool _obscure = true;
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _passwordController.dispose();
    super.dispose();
  }

  Future<void> _confirm() async {
    final password = _passwordController.text;
    if (password.isEmpty || _busy) return;
    final l10n = AppLocalizations.of(context)!;
    final bloc = context.read<AuthBloc>();
    setState(() {
      _busy = true;
      _error = null;
    });

    AuthResult result;
    try {
      result = await widget.authenticate(widget.phoneNumber, password);
    } on Object {
      result = AuthResult.failure(
        AuthErrorCode.internalServerError,
        'Step-up failed',
      );
    }
    if (!mounted) return;

    final tokens = result.tokens;
    if (!result.isSuccess || tokens == null) {
      setState(() {
        _busy = false;
        _error = result.errorCode == AuthErrorCode.networkError
            ? l10n.stepUpOfflineError
            : localizeAuthError(
                l10n,
                result.errorCode ?? AuthErrorCode.internalServerError,
                retryAfterSeconds: result.retryAfterSeconds,
              );
      });
      return;
    }

    // Wait until the bloc has written the new tokens to the slots the
    // AuthInterceptor reads, so the caller's retry uses them.
    final applied = bloc.stream
        .firstWhere((s) => s is AuthAuthenticated && s.tokens == tokens)
        .timeout(const Duration(seconds: 3), onTimeout: () => bloc.state);
    bloc.add(AuthStepUpSucceeded(tokens: tokens));
    await applied;
    if (mounted) Navigator.of(context).pop(true);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return AlertDialog(
      title: Text(l10n.stepUpTitle),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(l10n.stepUpRequiredMessage),
          const SizedBox(height: 12),
          Text(
            l10n.stepUpPhoneLabel(widget.phoneNumber),
            style: Theme.of(context).textTheme.bodySmall,
          ),
          const SizedBox(height: 8),
          TextField(
            key: const Key('step_up_password_field'),
            controller: _passwordController,
            obscureText: _obscure,
            autofocus: true,
            enabled: !_busy,
            autofillHints: const [AutofillHints.password],
            textInputAction: TextInputAction.done,
            onSubmitted: (_) => _confirm(),
            onChanged: (_) => setState(() {}),
            decoration: InputDecoration(
              labelText: l10n.password,
              errorText: _error,
              errorMaxLines: 3,
              suffixIcon: IconButton(
                icon: Icon(_obscure ? Icons.visibility : Icons.visibility_off),
                onPressed: () => setState(() => _obscure = !_obscure),
              ),
            ),
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(false),
          child: Text(l10n.cancel),
        ),
        FilledButton(
          key: const Key('step_up_confirm_button'),
          onPressed:
              (_busy || _passwordController.text.isEmpty) ? null : _confirm,
          child: _busy
              ? const SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : Text(l10n.stepUpConfirm),
        ),
      ],
    );
  }
}
