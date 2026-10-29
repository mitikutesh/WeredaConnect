import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';

/// Order statuses at which the commitment rows are shown (ESC-15).
const Set<OrderStatus> commitmentSectionStatuses = {
  OrderStatus.funded,
  OrderStatus.awaitingTransport,
  OrderStatus.accepted,
};

/// ESC-15: who has agreed to a paid order, one row per party (the farmer,
/// then the trucker on a trip order), and the note that tells the viewer
/// what happens next or by when they must answer.
class OrderCommitmentSection extends StatelessWidget {
  final Order order;
  final String userId;

  /// Also after pickup, when both answers are in (the live trip's trip log,
  /// LT-11); the order screen shows the rows only while someone decides.
  final bool showAfterPickup;

  const OrderCommitmentSection({
    required this.order,
    required this.userId,
    this.showAfterPickup = false,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    if (!showAfterPickup &&
        (!commitmentSectionStatuses.contains(order.status) ||
            !orderUsesCommitments(order))) {
      return const SizedBox.shrink();
    }
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final theme = Theme.of(context);
    final note = _viewerNote(l10n, clock);
    final failure = order.status == OrderStatus.awaitingTransport
        ? transportFailureLabel(l10n, order.transportFailure)
        : null;

    return Padding(
      padding: const EdgeInsets.only(bottom: 24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            l10n.commitmentTitle,
            style: theme.textTheme.titleMedium
                ?.copyWith(fontWeight: FontWeight.bold),
          ),
          const SizedBox(height: 12),
          Container(
            padding: const EdgeInsets.all(16),
            decoration: BoxDecoration(
              color: Colors.white,
              borderRadius: BorderRadius.circular(20),
              border: Border.all(color: AppTheme.whisperBorder),
            ),
            child: Column(
              children: [
                _PartyRow(
                  role: l10n.farmer,
                  name: partyDisplayName(order.farmerName, l10n),
                  state: _farmerState(l10n, clock),
                ),
                if (orderHasTrip(order)) ...[
                  const Divider(height: 16),
                  _PartyRow(
                    role: l10n.trucker,
                    name: partyDisplayName(order.truckerName, l10n),
                    state: _truckerState(l10n, clock),
                  ),
                ],
              ],
            ),
          ),
          if (failure != null || note != null) ...[
            const SizedBox(height: 12),
            _CommitmentNote(
              label: switch ((failure, note)) {
                (final String f, final ClockLabel n) => n.prefixed('$f. '),
                (final String f, null) => ClockLabel.text('$f.'),
                (null, final ClockLabel n) => n,
                (null, null) => const ClockLabel.text(''),
              },
              warning: order.status == OrderStatus.awaitingTransport,
            ),
          ],
        ],
      ),
    );
  }

  _RowState _farmerState(AppLocalizations l10n, ClockFormat clock) {
    return switch (farmerResponseOf(order)) {
      CommitmentResponse.accepted =>
        _RowState(l10n.commitmentAccepted, _Tone.done),
      CommitmentResponse.declined =>
        _RowState(l10n.commitmentDeclined, _Tone.failed),
      CommitmentResponse.expired =>
        _RowState(l10n.commitmentExpired, _Tone.failed),
      _ => _waiting(l10n, clock, order.farmerDeadlineAt),
    };
  }

  _RowState _truckerState(AppLocalizations l10n, ClockFormat clock) {
    return switch (truckerResponseOf(order)) {
      CommitmentResponse.accepted =>
        _RowState(l10n.commitmentAccepted, _Tone.done),
      CommitmentResponse.pending =>
        _waiting(l10n, clock, order.truckerDeadlineAt),
      CommitmentResponse.declined =>
        _RowState(l10n.commitmentDeclined, _Tone.failed),
      CommitmentResponse.expired =>
        _RowState(l10n.commitmentExpired, _Tone.failed),
      CommitmentResponse.failedCapacity =>
        _RowState(l10n.commitmentTripFull, _Tone.failed),
      CommitmentResponse.cancelled => _RowState(
          order.transportFailure == 'trip_cancelled'
              ? l10n.commitmentTripCancelled
              : l10n.commitmentDroppedOut,
          _Tone.failed,
        ),
      _ => _RowState(l10n.commitmentNotAskedYet, _Tone.idle),
    };
  }

  static _RowState _waiting(
    AppLocalizations l10n,
    ClockFormat clock,
    DateTime? deadline,
  ) =>
      _RowState.label(
        deadline == null
            ? ClockLabel.text(l10n.commitmentWaiting)
            : clock.deadline(
                deadline,
                date: DateFormat.MMMd(),
                sentence: l10n.commitmentWaitingUntil,
              ),
        _Tone.waiting,
      );

  /// What the viewer should know: their own answer and its deadline, why the
  /// buyer cannot cancel yet, or what to do after the transport failed.
  ClockLabel? _viewerNote(AppLocalizations l10n, ClockFormat clock) {
    if (order.status == OrderStatus.awaitingTransport) {
      if (userId == order.buyerId) {
        final deadline = order.buyerChoiceDeadlineAt;
        return deadline == null
            ? ClockLabel.text(l10n.transportFailedBuyerBodyNoDeadline)
            : clock.deadline(
                deadline,
                date: DateFormat.MMMd(),
                sentence: l10n.transportFailedBuyerBody,
              );
      }
      if (userId == order.farmerId &&
          farmerResponseOf(order) == CommitmentResponse.accepted) {
        return ClockLabel.text(l10n.transportFailedFarmerBody);
      }
      return null;
    }
    if (order.status != OrderStatus.funded) return null;
    final deadline = commitmentDeadlineFor(order, userId);
    if (deadline != null) {
      return clock.deadline(
        deadline,
        date: DateFormat.MMMd(),
        sentence: l10n.commitmentAnswerBy,
      );
    }
    if (userId == order.buyerId) {
      return ClockLabel.text(l10n.commitmentBuyerWaitingNote);
    }
    if (userId.isNotEmpty &&
        userId == order.truckerId &&
        truckerResponseOf(order) == CommitmentResponse.notAsked) {
      return ClockLabel.text(l10n.commitmentTruckerNotAskedNote);
    }
    return null;
  }
}

/// The localized reason a transport failed, or null for an unknown code.
String? transportFailureLabel(AppLocalizations l10n, String? code) =>
    switch (code) {
      'trucker_declined' => l10n.transportFailureTruckerDeclined,
      'trucker_expired' => l10n.transportFailureTruckerExpired,
      'trucker_cancelled' => l10n.transportFailureTruckerCancelled,
      'trip_cancelled' => l10n.transportFailureTripCancelled,
      'trip_full' => l10n.transportFailureTripFull,
      _ => null,
    };

/// The localized label of a decline reason code.
String declineReasonLabel(AppLocalizations l10n, String code) => switch (code) {
      'date' => l10n.declineReasonDate,
      'capacity' => l10n.declineReasonCapacity,
      'price' => l10n.declineReasonPrice,
      'route' => l10n.declineReasonRoute,
      'crop_not_ready' => l10n.declineReasonCropNotReady,
      _ => l10n.declineReasonOther,
    };

/// Asks why the trucker declines or drops out, or why the farmer declines a
/// new order ([reasons] = `farmerDeclineReasons`). Returns the chosen reason
/// code, or null when the sheet was dismissed. [warning] is shown above the
/// reasons (the drop-out's penalty and sign-in note, the farmer's refund).
Future<String?> showDeclineReasonSheet(
  BuildContext context, {
  String? warning,
  List<String> reasons = commitmentDeclineReasons,
}) {
  final l10n = AppLocalizations.of(context)!;
  return showModalBottomSheet<String>(
    context: context,
    isScrollControlled: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
    ),
    builder: (sheetContext) => SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(24, 20, 24, 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              l10n.declineReasonTitle,
              style: Theme.of(sheetContext)
                  .textTheme
                  .titleMedium
                  ?.copyWith(fontWeight: FontWeight.bold),
            ),
            if (warning != null) ...[
              const SizedBox(height: 12),
              _CommitmentNote(label: ClockLabel.text(warning), warning: true),
            ],
            const SizedBox(height: 8),
            for (final code in reasons)
              ListTile(
                contentPadding: EdgeInsets.zero,
                title: Text(declineReasonLabel(l10n, code)),
                trailing: const Icon(Icons.chevron_right),
                onTap: () => Navigator.of(sheetContext).pop(code),
              ),
            const SizedBox(height: 8),
            TextButton(
              onPressed: () => Navigator.of(sheetContext).pop(),
              child: Text(l10n.cancel),
            ),
          ],
        ),
      ),
    ),
  );
}

enum _Tone { done, waiting, failed, idle }

class _RowState {
  /// What the party has done; a waiting row carries its deadline.
  final ClockLabel text;
  final _Tone tone;

  _RowState(String label, this.tone) : text = ClockLabel.text(label);

  const _RowState.label(this.text, this.tone);
}

class _PartyRow extends StatelessWidget {
  final String role;
  final String name;
  final _RowState state;

  const _PartyRow({
    required this.role,
    required this.name,
    required this.state,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final (icon, color) = switch (state.tone) {
      _Tone.done => (Icons.check_circle, AppTheme.statusSuccess),
      _Tone.waiting => (Icons.schedule, AppTheme.accentGold),
      _Tone.failed => (Icons.cancel_outlined, AppTheme.statusError),
      _Tone.idle => (Icons.more_horiz, AppTheme.textMuted),
    };
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 20, color: color),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                name.isEmpty ? role : '$role: $name',
                style: theme.textTheme.bodyMedium
                    ?.copyWith(fontWeight: FontWeight.w600),
              ),
              const SizedBox(height: 2),
              ClockText(state.text, style: theme.textTheme.bodyMedium),
            ],
          ),
        ),
      ],
    );
  }
}

class _CommitmentNote extends StatelessWidget {
  final ClockLabel label;
  final bool warning;

  const _CommitmentNote({required this.label, this.warning = false});

  @override
  Widget build(BuildContext context) {
    final ink = warning ? AppTheme.onStatusWarningContainer : null;
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(16),
        color: warning ? const Color(0xFFFAEED8) : const Color(0xFFE3EEE6),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(
            warning ? Icons.warning_amber : Icons.info_outline,
            size: 20,
            color: ink,
          ),
          const SizedBox(width: 12),
          Expanded(
            child: ClockText(label, style: TextStyle(color: ink)),
          ),
        ],
      ),
    );
  }
}
