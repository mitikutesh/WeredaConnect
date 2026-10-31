import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/escrow_error_l10n.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// Screen for raising a dispute on an escrowed payment (a sibling of the
/// create boards: the payment named in the header, the time left in a
/// note, the reasons in a card under their section label, Submit pinned
/// at the foot).
///
/// Features:
/// - 48-hour countdown timer
/// - 6 dispute reason radio buttons (large touch targets)
/// - Optional description text field (max 1000 chars)
/// - Online-only enforcement (disputes cannot be queued offline)
/// - Expired window detection (form disabled)
class RaiseDisputeScreen extends StatefulWidget {
  final String escrowId;
  final double amount;
  final String deliveryId;
  final DateTime? disputeWindowEndsAt;

  /// Replaces the Fayda screen the AUTH_041 prompt opens (tests).
  final WidgetBuilder? faydaVerificationScreen;

  const RaiseDisputeScreen({
    required this.escrowId,
    required this.amount,
    required this.deliveryId,
    this.disputeWindowEndsAt,
    this.faydaVerificationScreen,
    super.key,
  });

  @override
  State<RaiseDisputeScreen> createState() => _RaiseDisputeScreenState();
}

class _RaiseDisputeScreenState extends State<RaiseDisputeScreen> {
  /// The dispute last sent, replayed after a step-up re-authentication.
  RaisePaymentDispute? _lastDispute;

  Future<void> _retryAfterStepUp(BuildContext context) async {
    final event = _lastDispute;
    if (event == null) return;
    final ok = await showStepUpDialog(context);
    if (ok && context.mounted) {
      context.read<EscrowBloc>().add(event);
    }
  }

  /// AUTH_041: only a fresh Fayda login satisfies the server; the password
  /// prompt would loop. A verified login sends the same dispute again.
  Future<void> _retryAfterFaydaStepUp(BuildContext context) async {
    final event = _lastDispute;
    if (event == null) return;
    final outcome = await showFaydaStepUpDialog(
      context,
      verificationScreen: widget.faydaVerificationScreen,
    );
    if (outcome == StepUpOutcome.verified && context.mounted) {
      context.read<EscrowBloc>().add(event);
    }
  }

  String? _selectedReason;
  final _descriptionController = TextEditingController();
  Timer? _countdownTimer;
  Duration _remaining = Duration.zero;

  static const _reasonKeys = [
    'damaged_goods',
    'wrong_quantity',
    'wrong_crop',
    'not_delivered',
    'quality_mismatch',
    'other',
  ];

  static String _reasonLabel(String key, AppLocalizations l10n) {
    return switch (key) {
      'damaged_goods' => l10n.escrowReasonDamagedGoods,
      'wrong_quantity' => l10n.escrowReasonWrongQuantity,
      'wrong_crop' => l10n.escrowReasonWrongCrop,
      'not_delivered' => l10n.escrowReasonNotDelivered,
      'quality_mismatch' => l10n.escrowReasonQualityMismatch,
      _ => l10n.escrowReasonOther,
    };
  }

  @override
  void initState() {
    super.initState();
    _startCountdown();
  }

  void _startCountdown() {
    if (widget.disputeWindowEndsAt == null) return;
    _updateRemaining();
    _countdownTimer = Timer.periodic(const Duration(seconds: 30), (_) {
      _updateRemaining();
    });
  }

  void _updateRemaining() {
    final now = DateTime.now().toUtc();
    final ends = widget.disputeWindowEndsAt!;
    setState(() {
      _remaining = ends.isAfter(now) ? ends.difference(now) : Duration.zero;
    });
  }

  @override
  void dispose() {
    _countdownTimer?.cancel();
    _descriptionController.dispose();
    super.dispose();
  }

  bool get _isExpired =>
      widget.disputeWindowEndsAt != null && _remaining == Duration.zero;

  bool get _canSubmit => _selectedReason != null && !_isExpired;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final shortId = widget.deliveryId.length > 8
        ? widget.deliveryId.substring(0, 8)
        : widget.deliveryId;

    return BlocListener<EscrowBloc, EscrowState>(
      listener: (context, state) {
        if (state is DisputeRaisedSuccess) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(l10n.escrowDisputeSuccess),
            ),
          );
          Navigator.of(context).pop();
        } else if (state is EscrowError && state.code == stepUpRequiredCode) {
          // High-value escrow with an old session: confirm the password,
          // then raise the same dispute again.
          _retryAfterStepUp(context);
        } else if (state is EscrowError &&
            state.code == faydaStepUpRequiredCode) {
          _retryAfterFaydaStepUp(context);
        } else if (state is EscrowError) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                localizeEscrowError(
                  AppLocalizations.of(context)!,
                  state.code,
                  state.message,
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
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // Payment reference
                  FwDetailHeader(
                    title: l10n.escrowRaiseDispute,
                    subtitle:
                        '${l10n.etbCurrency(formatEtbAmount(widget.amount))} · '
                        '${l10n.escrowDeliveryNumber(shortId)}',
                  ),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        // Countdown or expired note
                        if (widget.disputeWindowEndsAt != null) ...[
                          if (_isExpired)
                            FwNoteBox(
                              icon: Icons.cancel,
                              background: AppTheme.statusErrorContainer,
                              foreground: AppTheme.onStatusErrorContainer,
                              child: Text(l10n.escrowDisputeWindowExpired),
                            )
                          else
                            FwNoteBox.cream(
                              icon: Icons.timer,
                              child: Text(
                                l10n.escrowDisputeTimeRemaining(
                                  _remaining.inHours,
                                  _remaining.inMinutes.remainder(60),
                                ),
                              ),
                            ),
                          const SizedBox(height: 20),
                        ],

                        // Reason selection
                        FwSectionLabel(
                          l10n.escrowDisputeReasonLabel,
                          padding: const EdgeInsets.only(bottom: 8),
                        ),
                        IgnorePointer(
                          ignoring: _isExpired,
                          child: FwCard(
                            padding: const EdgeInsets.symmetric(
                              horizontal: 4,
                              vertical: 6,
                            ),
                            child: RadioGroup<String>(
                              groupValue: _selectedReason ?? '',
                              onChanged: (value) =>
                                  setState(() => _selectedReason = value),
                              child: Column(
                                children: [
                                  for (final key in _reasonKeys)
                                    RadioListTile<String>(
                                      value: key,
                                      title: Text(
                                        _reasonLabel(key, l10n),
                                        style: const TextStyle(
                                          fontSize: 14,
                                          fontWeight: FontWeight.w600,
                                          color: AppTheme.soilDark,
                                        ),
                                      ),
                                      contentPadding:
                                          const EdgeInsets.symmetric(
                                        horizontal: 8,
                                      ),
                                      dense: true,
                                      visualDensity:
                                          const VisualDensity(vertical: -2),
                                    ),
                                ],
                              ),
                            ),
                          ),
                        ),
                        const SizedBox(height: 20),

                        // Description (optional)
                        FwSectionLabel(
                          l10n.escrowDisputeDescriptionLabel,
                          padding: const EdgeInsets.only(bottom: 8),
                        ),
                        TextFormField(
                          controller: _descriptionController,
                          enabled: !_isExpired,
                          maxLines: 4,
                          maxLength: 1000,
                          decoration: InputDecoration(
                            hintText: l10n.escrowDisputeDescriptionHint,
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
        // Submit, pinned at the foot
        bottomNavigationBar: BlocBuilder<EscrowBloc, EscrowState>(
          builder: (context, state) {
            final isLoading = state is EscrowLoading;
            return FwBottomActions(
              children: [
                FilledButton.icon(
                  onPressed: _canSubmit && !isLoading
                      ? () => _showConfirmation(context)
                      : null,
                  icon: isLoading
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
                      : const Icon(Icons.warning_amber),
                  label: Text(l10n.escrowDisputeSubmit),
                  style: FilledButton.styleFrom(
                    backgroundColor: AppTheme.onStatusWarningContainer,
                    minimumSize: const Size.fromHeight(56),
                    textStyle: const TextStyle(
                      fontFamily: AppTheme.fontFamily,
                      fontFamilyFallback: AppTheme.ethiopicFontFallback,
                      fontSize: 16,
                      fontWeight: FontWeight.w800,
                    ),
                  ),
                ),
                FwActionNote(l10n.escrowDisputeReviewInfo),
              ],
            );
          },
        ),
      ),
    );
  }

  void _showConfirmation(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        final dl10n = AppLocalizations.of(context)!;
        return AlertDialog(
          title: Text(dl10n.escrowRaiseDispute),
          content: Text(dl10n.escrowDisputeConfirmDialog),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(dl10n.cancel),
            ),
            FilledButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                // Get current user ID from AuthBloc
                final authState = context.read<AuthBloc>().state;
                final userId =
                    authState is AuthAuthenticated ? authState.userId : '';
                final event = RaisePaymentDispute(
                  escrowId: widget.escrowId,
                  disputedBy: userId,
                  reason: _selectedReason!,
                  description: _descriptionController.text.isNotEmpty
                      ? _descriptionController.text
                      : null,
                );
                _lastDispute = event;
                context.read<EscrowBloc>().add(event);
              },
              style: FilledButton.styleFrom(
                backgroundColor: AppTheme.onStatusWarningContainer,
              ),
              child: Text(dl10n.escrowDisputeSubmit),
            ),
          ],
        );
      },
    );
  }
}
