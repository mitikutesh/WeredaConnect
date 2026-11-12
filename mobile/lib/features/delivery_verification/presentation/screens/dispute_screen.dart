import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_validation_exception.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/delivery_error_l10n.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// Raise a dispute within 48 hours of delivery confirmation (a sibling of
/// the create boards: the delivery named in the header, the time left in a
/// note, the reason under its section label, Submit pinned at the foot).
///
/// Shows delivery info, time remaining, reason input,
/// and confirmation dialog before submitting.
///
/// On web the server may answer the dispute with `AUTH_040` (a fresh
/// password is needed on a large order): the password prompt opens and the
/// same dispute is sent once more after it (FUP-43). On mobile the dispute
/// is queued and the sync queue prompts.
class DisputeScreen extends StatefulWidget {
  final Delivery delivery;
  final String userId;

  /// Asks for the password again (AUTH_040). Defaults to [showStepUpDialog];
  /// tests inject one.
  final Future<bool> Function(BuildContext context)? confirmPassword;

  const DisputeScreen({
    required this.delivery,
    required this.userId,
    this.confirmPassword,
    super.key,
  });

  @override
  State<DisputeScreen> createState() => _DisputeScreenState();
}

class _DisputeScreenState extends State<DisputeScreen> {
  final _formKey = GlobalKey<FormState>();
  final _reasonController = TextEditingController();
  bool _isSubmitting = false;

  /// The dispute last sent, sent again after the password step-up.
  RaiseDispute? _lastDispute;

  /// True once [_lastDispute] was sent again after a step-up: a second
  /// AUTH_040 then ends in the message, not in another prompt.
  bool _resentAfterStepUp = false;

  /// AUTH_040: ask for the password, then send the same dispute again. A
  /// cancelled prompt leaves it unsent and says so.
  Future<void> _retryAfterStepUp() async {
    final event = _lastDispute;
    var confirmed = false;
    if (event != null && !_resentAfterStepUp) {
      confirmed = await (widget.confirmPassword ??
          (BuildContext c) => showStepUpDialog(
                c,
                message: AppLocalizations.of(c)!.stepUpDisputeMessage,
              ))(context);
    }
    if (!mounted) return;
    if (confirmed && event != null) {
      _resentAfterStepUp = true;
      context.read<DeliveryBloc>().add(event);
      return;
    }
    setState(() => _isSubmitting = false);
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(
          AppLocalizations.of(context)!.deliveryDisputeNotConfirmed,
        ),
        backgroundColor: AppTheme.statusError,
      ),
    );
  }

  @override
  void dispose() {
    _reasonController.dispose();
    super.dispose();
  }

  bool get _isExpired {
    if (widget.delivery.confirmedAt == null) return true;
    final elapsed =
        DateTime.now().toUtc().difference(widget.delivery.confirmedAt!);
    return elapsed >= DeliveryConstants.disputeWindow;
  }

  Duration get _timeRemaining {
    if (widget.delivery.confirmedAt == null) return Duration.zero;
    final elapsed =
        DateTime.now().toUtc().difference(widget.delivery.confirmedAt!);
    final remaining = DeliveryConstants.disputeWindow - elapsed;
    return remaining.isNegative ? Duration.zero : remaining;
  }

  void _submitDispute() {
    if (!_formKey.currentState!.validate()) return;

    final l10n = AppLocalizations.of(context)!;
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        return AlertDialog(
          title: Text(l10n.deliveryDisputeConfirmTitle),
          content: Text(l10n.deliveryDisputeConfirmMessage),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(l10n.cancel),
            ),
            TextButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                setState(() => _isSubmitting = true);
                final dispute = RaiseDispute(
                  deliveryId: widget.delivery.deliveryId,
                  userId: widget.userId,
                  reason: _reasonController.text.trim(),
                  tripId: widget.delivery.tripId,
                );
                _lastDispute = dispute;
                _resentAfterStepUp = false;
                context.read<DeliveryBloc>().add(dispute);
              },
              style:
                  TextButton.styleFrom(foregroundColor: AppTheme.statusError),
              child: Text(l10n.deliveryDisputeSubmit),
            ),
          ],
        );
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final remaining = _timeRemaining;
    final confirmedAt = widget.delivery.confirmedAt;
    final shortId = widget.delivery.deliveryId.substring(0, 8);

    return BlocListener<DeliveryBloc, DeliveryState>(
      listener: (context, state) {
        if (state is DeliveriesLoaded || state is DisputeRaised) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                AppLocalizations.of(context)!.deliveryDisputeSubmit,
              ),
            ),
          );
          Navigator.of(context).pop();
        } else if (state is DeliveryError &&
            state.code == DeliveryErrorCode.stepUpRequired) {
          _retryAfterStepUp();
        } else if (state is DeliveryError) {
          setState(() => _isSubmitting = false);
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                localizeDeliveryError(
                  AppLocalizations.of(context)!,
                  state.code,
                ),
              ),
              backgroundColor: AppTheme.statusError,
            ),
          );
        }
      },
      child: Scaffold(
        body: SafeArea(
          bottom: false,
          child: SingleChildScrollView(
            child: FwContentWidth(
              child: Form(
                key: _formKey,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    FwDetailHeader(
                      title: l10n.deliveryRaiseDispute,
                      subtitle: confirmedAt == null
                          ? l10n.deliveryIdLabel(shortId)
                          : '${l10n.deliveryIdLabel(shortId)} · '
                              '${l10n.deliveryConfirmedAtLabel(clock.dateTime(confirmedAt))}',
                    ),
                    Padding(
                      padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          // How long the window stays open, or that it closed.
                          if (_isExpired)
                            FwNoteBox(
                              icon: Icons.timer_off,
                              background: AppTheme.statusErrorContainer,
                              foreground: AppTheme.onStatusErrorContainer,
                              child: Text(l10n.deliveryDisputeExpired),
                            )
                          else
                            FwNoteBox.cream(
                              icon: Icons.timer,
                              child: Text(
                                '${l10n.deliveryDisputeTimeRemaining}: '
                                '${remaining.inHours}h ${remaining.inMinutes.remainder(60)}m',
                              ),
                            ),
                          const SizedBox(height: 20),

                          FwSectionLabel(
                            l10n.deliveryDisputeReason,
                            padding: const EdgeInsets.only(bottom: 8),
                          ),
                          TextFormField(
                            controller: _reasonController,
                            enabled: !_isExpired,
                            maxLength: DeliveryConstants.maxDisputeReasonLength,
                            maxLines: 4,
                            validator: (value) {
                              if (value == null || value.trim().isEmpty) {
                                return l10n.deliveryReasonRequired;
                              }
                              return null;
                            },
                          ),
                          const SizedBox(height: 4),

                          Text(
                            l10n.deliveryDisputeWarning,
                            style: const TextStyle(
                              fontSize: 13,
                              height: 1.5,
                              color: AppTheme.mutedSteel,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
        bottomNavigationBar: FwBottomActions(
          children: [
            FilledButton(
              onPressed: _isExpired || _isSubmitting ? null : _submitDispute,
              style: FilledButton.styleFrom(
                backgroundColor: AppTheme.statusError,
                minimumSize: const Size.fromHeight(56),
                textStyle: const TextStyle(
                  fontFamily: AppTheme.fontFamily,
                  fontFamilyFallback: AppTheme.ethiopicFontFallback,
                  fontSize: 16,
                  fontWeight: FontWeight.w800,
                ),
              ),
              child: _isSubmitting
                  ? const SizedBox(
                      height: 20,
                      width: 20,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        valueColor: AlwaysStoppedAnimation<Color>(
                          Colors.white,
                        ),
                      ),
                    )
                  : Text(l10n.deliveryDisputeSubmit),
            ),
          ],
        ),
      ),
    );
  }
}
