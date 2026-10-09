import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
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
/// - [SyncOperationRejected]: the server refused a queued change for good
///   (a refused order change was already put back); a snackbar says why.
///   It never waits for, or closes, an open prompt. Refusals that arrive
///   together (one sync batch) share snackbars: one per distinct message.
///   Another user's refusal (shared device) is not shown.
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
            event.paymentReference,
            paymentMethod: event.paymentMethod!,
          );

  @override
  State<SecurityPromptListener> createState() => _SecurityPromptListenerState();
}

class _SecurityPromptListenerState extends State<SecurityPromptListener> {
  /// How long refusals are gathered before they are shown, so a refused
  /// batch reads as one message instead of a queue of identical snackbars.
  static const _rejectionBurst = Duration(milliseconds: 300);

  StreamSubscription<SecurityEvent>? _sub;
  bool _showing = false;
  final List<String> _rejectionTexts = [];
  Timer? _rejectionTimer;

  SecurityEvents get _events => widget.events ?? SecurityEvents.instance;

  @override
  void initState() {
    super.initState();
    _sub = _events.events.listen(_onEvent);
  }

  @override
  void dispose() {
    _sub?.cancel();
    _rejectionTimer?.cancel();
    super.dispose();
  }

  Future<void> _onEvent(SecurityEvent event) async {
    if (event is SyncOperationRejected) {
      _explainRejection(event);
      return;
    }
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
        case SyncOperationRejected():
          // Shown by _explainRejection without the one-prompt gate.
          break;
      }
    } finally {
      _showing = false;
    }
  }

  void _explainRejection(SyncOperationRejected event) {
    if (!mounted) return;
    final auth = context.read<AuthBloc>().state;
    if (auth is! AuthAuthenticated) return;
    if (event.userId != null && event.userId != auth.userId) return;
    final text = _rejectionText(event, AppLocalizations.of(context)!);
    if (!_rejectionTexts.contains(text)) _rejectionTexts.add(text);
    _rejectionTimer ??= Timer(_rejectionBurst, _showRejections);
  }

  /// The fraud checks keep the wording the listing form uses; everything
  /// else goes through [syncRejectionErrorCode].
  String _rejectionText(SyncOperationRejected event, AppLocalizations l10n) {
    final message = event.message;
    if (message.startsWith('FRAUD_DUPLICATE_LISTING')) {
      return l10n.fraudDuplicateListing;
    }
    if (message.startsWith('FRAUD_VELOCITY_EXCEEDED')) {
      // The server's listing limit resets at the next UTC hour.
      return l10n.fraudVelocityExceeded(60 - DateTime.now().toUtc().minute);
    }
    return localizeErrorCode(
      syncRejectionErrorCode(
        entityType: event.entityType,
        action: event.action,
        message: message,
      ),
      l10n,
    );
  }

  void _showRejections() {
    _rejectionTimer = null;
    final texts = List.of(_rejectionTexts);
    _rejectionTexts.clear();
    if (!mounted) return;
    final messenger = ScaffoldMessenger.maybeOf(context);
    for (final text in texts) {
      messenger?.showSnackBar(SnackBar(content: Text(text)));
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
