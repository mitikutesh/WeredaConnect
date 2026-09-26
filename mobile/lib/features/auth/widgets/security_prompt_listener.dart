import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/widgets/kyc_required_dialog.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Shows the prompts that background work cannot resolve on its own:
///
/// - [StepUpRequired]: a queued payment hit AUTH_040. After the user
///   confirms their password, [SecurityEvents.stepUpCompleted] fires and the
///   sync workers retry the still-pending operations.
/// - [KycRequired]: a queued payment hit ORDER_KYC_REQUIRED (the local order
///   was already reverted so the buyer can pay again after verifying).
///
/// Sits around the authenticated shell. Only one prompt shows at a time and
/// repeats of the same kind while it is open are dropped. Cancelling the
/// step-up prompt quiets it for [SecurityEvents.stepUpCooldown].
class SecurityPromptListener extends StatefulWidget {
  final Widget child;
  final SecurityEvents? events;

  const SecurityPromptListener({required this.child, this.events, super.key});

  @override
  State<SecurityPromptListener> createState() => _SecurityPromptListenerState();
}

class _SecurityPromptListenerState extends State<SecurityPromptListener> {
  StreamSubscription<SecurityEvent>? _sub;
  bool _showing = false;

  SecurityEvents get _events => widget.events ?? SecurityEvents.instance;

  @override
  void initState() {
    super.initState();
    _sub = _events.events.listen(_onEvent);
  }

  @override
  void dispose() {
    _sub?.cancel();
    super.dispose();
  }

  Future<void> _onEvent(SecurityEvent event) async {
    if (_showing || !mounted) return;
    if (context.read<AuthBloc>().state is! AuthAuthenticated) return;
    _showing = true;
    try {
      switch (event) {
        case StepUpRequired():
          final ok = await showStepUpDialog(context, securityEvents: _events);
          // Cancelled: stay quiet for a while instead of reopening on every
          // background sync.
          if (!ok) _events.stepUpDeclined();
          if (ok && mounted) {
            ScaffoldMessenger.maybeOf(context)?.showSnackBar(
              SnackBar(
                content: Text(AppLocalizations.of(context)!.stepUpDone),
              ),
            );
          }
        case KycRequired():
          await showKycRequiredDialog(
            context,
            phoneNotVerified: event.isPhoneNotVerified,
          );
      }
    } finally {
      _showing = false;
    }
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
