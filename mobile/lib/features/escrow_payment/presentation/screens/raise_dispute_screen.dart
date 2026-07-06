import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/escrow_error_l10n.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Screen for raising a dispute on an escrowed payment.
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

  const RaiseDisputeScreen({
    required this.escrowId,
    required this.amount,
    required this.deliveryId,
    this.disputeWindowEndsAt,
    super.key,
  });

  @override
  State<RaiseDisputeScreen> createState() => _RaiseDisputeScreenState();
}

class _RaiseDisputeScreenState extends State<RaiseDisputeScreen> {
  String? _selectedReason;
  final _descriptionController = TextEditingController();
  Timer? _countdownTimer;
  Duration _remaining = Duration.zero;

  static final _formatter = NumberFormat('#,##0.00', 'en_US');

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
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<EscrowBloc, EscrowState>(
      listener: (context, state) {
        if (state is DisputeRaisedSuccess) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(l10n.escrowDisputeSuccess),
            ),
          );
          Navigator.of(context).pop();
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
        appBar: AppBar(title: Text(l10n.escrowRaiseDispute)),
        body: SafeArea(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                // Payment reference
                Text(
                  l10n.escrowPaymentAmount(_formatter.format(widget.amount)),
                  style: theme.textTheme.titleMedium,
                ),
                Text(
                  l10n.escrowDeliveryNumber(
                    widget.deliveryId.length > 8
                        ? widget.deliveryId.substring(0, 8)
                        : widget.deliveryId,
                  ),
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                  ),
                ),
                const SizedBox(height: 16),

                // Countdown or expired banner
                if (widget.disputeWindowEndsAt != null) ...[
                  if (_isExpired)
                    _ExpiredBanner()
                  else
                    _CountdownBanner(remaining: _remaining),
                  const SizedBox(height: 24),
                ],

                // Reason selection
                Text(
                  l10n.escrowDisputeReasonLabel,
                  style: theme.textTheme.titleMedium?.copyWith(
                    fontWeight: FontWeight.bold,
                  ),
                ),
                const SizedBox(height: 8),
                IgnorePointer(
                  ignoring: _isExpired,
                  child: RadioGroup<String>(
                    groupValue: _selectedReason ?? '',
                    onChanged: (value) =>
                        setState(() => _selectedReason = value),
                    child: Column(
                      children: [
                        for (final key in _reasonKeys)
                          RadioListTile<String>(
                            value: key,
                            title: Text(_reasonLabel(key, l10n)),
                            contentPadding: EdgeInsets.zero,
                            dense: true,
                            visualDensity: const VisualDensity(vertical: -2),
                          ),
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 16),

                // Description (optional)
                Text(
                  l10n.escrowDisputeDescriptionLabel,
                  style: theme.textTheme.titleMedium?.copyWith(
                    fontWeight: FontWeight.bold,
                  ),
                ),
                const SizedBox(height: 8),
                TextFormField(
                  controller: _descriptionController,
                  enabled: !_isExpired,
                  maxLines: 4,
                  maxLength: 1000,
                  decoration: InputDecoration(
                    hintText: l10n.escrowDisputeDescriptionHint,
                    border: const OutlineInputBorder(),
                  ),
                ),
                const SizedBox(height: 24),

                // Submit button
                BlocBuilder<EscrowBloc, EscrowState>(
                  builder: (context, state) {
                    final isLoading = state is EscrowLoading;
                    return FilledButton.icon(
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
                        textStyle: const TextStyle(fontSize: 18),
                      ),
                    );
                  },
                ),
                const SizedBox(height: 16),

                // Info footer
                Row(
                  children: [
                    Icon(
                      Icons.info_outline,
                      size: 14,
                      color: theme.colorScheme.onSurface.withValues(alpha: 0.5),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: Text(
                        l10n.escrowDisputeReviewInfo,
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: theme.colorScheme.onSurface
                              .withValues(alpha: 0.5),
                        ),
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
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
                context.read<EscrowBloc>().add(
                      RaisePaymentDispute(
                        escrowId: widget.escrowId,
                        disputedBy: userId,
                        reason: _selectedReason!,
                        description: _descriptionController.text.isNotEmpty
                            ? _descriptionController.text
                            : null,
                      ),
                    );
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

class _ExpiredBanner extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppTheme.statusError.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: AppTheme.statusError.withValues(alpha: 0.3)),
      ),
      child: Row(
        children: [
          const Icon(Icons.cancel, color: AppTheme.statusError),
          const SizedBox(width: 12),
          Text(
            l10n.escrowDisputeWindowExpired,
            style: const TextStyle(
              color: AppTheme.onStatusErrorContainer,
              fontWeight: FontWeight.w600,
            ),
          ),
        ],
      ),
    );
  }
}

class _CountdownBanner extends StatelessWidget {
  final Duration remaining;

  const _CountdownBanner({required this.remaining});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: AppTheme.statusWarning.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(8),
        border:
            Border.all(color: AppTheme.statusWarning.withValues(alpha: 0.3)),
      ),
      child: Row(
        children: [
          const Icon(Icons.timer, color: AppTheme.statusWarning),
          const SizedBox(width: 12),
          Text(
            l10n.escrowDisputeTimeRemaining(
              remaining.inHours,
              remaining.inMinutes.remainder(60),
            ),
            style: const TextStyle(
              color: AppTheme.onStatusWarningContainer,
              fontWeight: FontWeight.w600,
            ),
          ),
        ],
      ),
    );
  }
}
