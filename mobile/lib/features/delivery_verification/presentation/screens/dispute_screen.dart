import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/delivery_error_l10n.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Raise a dispute within 48 hours of delivery confirmation.
///
/// Shows delivery info, time remaining, reason input,
/// and confirmation dialog before submitting.
class DisputeScreen extends StatefulWidget {
  final Delivery delivery;
  final String userId;

  const DisputeScreen({
    required this.delivery,
    required this.userId,
    super.key,
  });

  @override
  State<DisputeScreen> createState() => _DisputeScreenState();
}

class _DisputeScreenState extends State<DisputeScreen> {
  final _formKey = GlobalKey<FormState>();
  final _reasonController = TextEditingController();
  bool _isSubmitting = false;

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
                context.read<DeliveryBloc>().add(
                      RaiseDispute(
                        deliveryId: widget.delivery.deliveryId,
                        userId: widget.userId,
                        reason: _reasonController.text.trim(),
                        tripId: widget.delivery.tripId,
                      ),
                    );
              },
              style: TextButton.styleFrom(foregroundColor: Colors.red),
              child: Text(l10n.deliveryDisputeSubmit),
            ),
          ],
        );
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    final dateFormat = DateFormat.yMMMd().add_jm();
    final remaining = _timeRemaining;

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
              backgroundColor: Colors.red,
            ),
          );
        }
      },
      child: Scaffold(
        appBar: AppBar(
          title: Text(AppLocalizations.of(context)!.deliveryRaiseDispute),
          elevation: 0,
        ),
        body: SafeArea(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: Form(
              key: _formKey,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  // Delivery info
                  Text(
                    l10n.deliveryIdLabel(
                      widget.delivery.deliveryId.substring(0, 8),
                    ),
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.bold,
                    ),
                  ),
                  if (widget.delivery.confirmedAt != null) ...[
                    const SizedBox(height: 4),
                    Text(
                      l10n.deliveryConfirmedAtLabel(
                        dateFormat.format(widget.delivery.confirmedAt!),
                      ),
                      style: theme.textTheme.bodyMedium?.copyWith(
                        color:
                            theme.colorScheme.onSurface.withValues(alpha: 0.6),
                      ),
                    ),
                  ],
                  const SizedBox(height: 16),

                  // Time remaining
                  if (_isExpired)
                    Container(
                      width: double.infinity,
                      padding: const EdgeInsets.all(16),
                      decoration: BoxDecoration(
                        color: Colors.red.withValues(alpha: 0.05),
                        borderRadius: BorderRadius.circular(8),
                        border: Border.all(
                          color: Colors.red.withValues(alpha: 0.2),
                        ),
                      ),
                      child: Row(
                        children: [
                          const Icon(
                            Icons.timer_off,
                            color: Colors.red,
                            size: 20,
                          ),
                          const SizedBox(width: 8),
                          Expanded(
                            child: Text(
                              l10n.deliveryDisputeExpired,
                              style: const TextStyle(
                                color: Colors.red,
                                fontWeight: FontWeight.w600,
                              ),
                            ),
                          ),
                        ],
                      ),
                    )
                  else
                    Container(
                      width: double.infinity,
                      padding: const EdgeInsets.all(16),
                      decoration: BoxDecoration(
                        color: Colors.orange.withValues(alpha: 0.05),
                        borderRadius: BorderRadius.circular(8),
                        border: Border.all(
                          color: Colors.orange.withValues(alpha: 0.2),
                        ),
                      ),
                      child: Row(
                        children: [
                          const Icon(
                            Icons.timer,
                            color: Colors.orange,
                            size: 20,
                          ),
                          const SizedBox(width: 8),
                          Expanded(
                            child: Text(
                              '${l10n.deliveryDisputeTimeRemaining}: ${remaining.inHours}h ${remaining.inMinutes.remainder(60)}m',
                              style: TextStyle(
                                color: Colors.orange.shade800,
                                fontWeight: FontWeight.w600,
                              ),
                            ),
                          ),
                        ],
                      ),
                    ),
                  const SizedBox(height: 24),

                  // Reason input
                  TextFormField(
                    controller: _reasonController,
                    enabled: !_isExpired,
                    maxLength: DeliveryConstants.maxDisputeReasonLength,
                    maxLines: 4,
                    decoration: InputDecoration(
                      labelText: l10n.deliveryDisputeReason,
                      border: const OutlineInputBorder(),
                      alignLabelWithHint: true,
                    ),
                    validator: (value) {
                      if (value == null || value.trim().isEmpty) {
                        return l10n.deliveryReasonRequired;
                      }
                      return null;
                    },
                  ),
                  const SizedBox(height: 8),

                  // Warning text
                  Text(
                    l10n.deliveryDisputeWarning,
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
                    ),
                  ),
                  const SizedBox(height: 24),

                  // Submit button
                  SizedBox(
                    width: double.infinity,
                    child: FilledButton(
                      onPressed:
                          _isExpired || _isSubmitting ? null : _submitDispute,
                      style: FilledButton.styleFrom(
                        backgroundColor: Colors.red,
                        minimumSize: const Size.fromHeight(48),
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
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
