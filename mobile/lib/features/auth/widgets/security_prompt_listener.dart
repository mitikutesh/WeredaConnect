import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/widgets/kyc_required_dialog.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Sends a KYC-rejected payment again after the buyer verified.
typedef KycPaymentResubmitter = Future<void> Function(
  BuildContext context,
  KycRequired event,
);

/// Shows the prompts that background work cannot resolve on its own:
///
/// - [StepUpRequired]: a queued payment hit AUTH_040 (password) or AUTH_041
///   (Fayda login, payments above 10,000 ETB). After the user confirms,
///   [SecurityEvents.stepUpCompleted] fires and the sync workers retry the
///   still-pending operations.
/// - [KycRequired]: a queued payment hit ORDER_KYC_REQUIRED (the local order
///   was already reverted). Once the buyer verifies with Fayda as this
///   account's owner, the same payment is queued again via
///   [SecurityPromptListener.resubmitPayment]; a failed or cancelled
///   verification leaves it unsent.
///
/// Sits around the authenticated shell. Only one prompt shows at a time and
/// repeats of the same kind while it is open are dropped. Cancelling the
/// step-up prompt quiets it for [SecurityEvents.stepUpCooldown].
class SecurityPromptListener extends StatefulWidget {
  final Widget child;
  final SecurityEvents? events;

  /// Defaults to the app-wide OrderBloc's repository (no bloc state change,
  /// so an open list screen is not disturbed). Tests inject one.
  final KycPaymentResubmitter? resubmitPayment;

  /// Replaces the Fayda screen the KYC and Fayda step-up prompts open (tests).
  final WidgetBuilder? verificationScreen;

  const SecurityPromptListener({
    required this.child,
    this.events,
    this.resubmitPayment,
    this.verificationScreen,
    super.key,
  });

  static Future<void> _resubmitViaOrderRepository(
    BuildContext context,
    KycRequired event,
  ) =>
      context.read<OrderBloc>().repository.submitPayment(
            event.orderId,
            event.paymentReference!,
            paymentMethod: event.paymentMethod!,
          );

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
        case StepUpRequired(:final withFayda):
          final outcome = await _confirmStepUp(withFayda: withFayda);
          // Cancelled: stay quiet for a while instead of reopening on every
          // background sync. A Fayda attempt that did not finish is not a
          // "no": the next sync asks again.
          if (outcome == StepUpOutcome.declined) _events.stepUpDeclined();
          if (outcome == StepUpOutcome.verified && mounted) {
            ScaffoldMessenger.maybeOf(context)?.showSnackBar(
              SnackBar(
                content: Text(AppLocalizations.of(context)!.stepUpDone),
              ),
            );
          }
        case KycRequired():
          final verified = await showKycRequiredDialog(
            context,
            reason: event.reason,
            verificationScreen: widget.verificationScreen,
          );
          if (verified && mounted) await _resubmit(event);
      }
    } finally {
      _showing = false;
    }
  }

  /// The password prompt, or above 10,000 ETB (AUTH_041) the Fayda login.
  /// The password prompt only closes unconfirmed when cancelled.
  Future<StepUpOutcome> _confirmStepUp({required bool withFayda}) async {
    if (withFayda) {
      return showFaydaStepUpDialog(
        context,
        securityEvents: _events,
        verificationScreen: widget.verificationScreen,
      );
    }
    final ok = await showStepUpDialog(context, securityEvents: _events);
    return ok ? StepUpOutcome.verified : StepUpOutcome.declined;
  }

  Future<void> _resubmit(KycRequired event) async {
    final l10n = AppLocalizations.of(context)!;
    final messenger = ScaffoldMessenger.maybeOf(context);
    if (!event.canResubmit) {
      messenger?.showSnackBar(
        SnackBar(content: Text(l10n.kycVerifiedPayAgain)),
      );
      return;
    }
    try {
      await (widget.resubmitPayment ??
          SecurityPromptListener._resubmitViaOrderRepository)(context, event);
      messenger?.showSnackBar(
        SnackBar(content: Text(l10n.kycPaymentResubmitted)),
      );
    } on Object catch (e) {
      debugPrint('[SecurityPromptListener] payment resubmit failed: $e');
      messenger?.showSnackBar(
        SnackBar(content: Text(l10n.kycVerifiedPayAgain)),
      );
    }
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
