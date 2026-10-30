import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// Order statuses at which the commitment rows are shown (ESC-15).
const Set<OrderStatus> commitmentSectionStatuses = {
  OrderStatus.funded,
  OrderStatus.awaitingTransport,
  OrderStatus.accepted,
};

/// ESC-15: who has agreed to a paid order, as the order boards draw it: one
/// card, a row per party (the buyer who paid, the farmer, then the trucker
/// on a trip order) with a round icon, the name in bold and the answer or
/// its deadline on the right, and under a rule the note that tells the
/// viewer what happens next or by when they must answer.
class OrderCommitmentSection extends StatelessWidget {
  final Order order;
  final String userId;

  /// Also after pickup, when both answers are in (the live trip's trip log,
  /// LT-11); the order screen shows the rows only while someone decides.
  final bool showAfterPickup;

  /// The order screen shows the viewer's own deadline in a banner above
  /// this card, so the card does not repeat it.
  final bool hideViewerDeadline;

  const OrderCommitmentSection({
    required this.order,
    required this.userId,
    this.showAfterPickup = false,
    this.hideViewerDeadline = false,
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
    final note = _viewerNote(l10n, clock);
    final failure = order.status == OrderStatus.awaitingTransport
        ? transportFailureLabel(l10n, order.transportFailure)
        : null;
    final noteLabel = switch ((failure, note)) {
      (final String f, final ClockLabel n) => n.prefixed('$f. '),
      (final String f, null) => ClockLabel.text('$f.'),
      (null, final ClockLabel n) => n,
      (null, null) => null,
    };
    final warning = order.status == OrderStatus.awaitingTransport;

    final buyer = partyDisplayName(order.buyerName, l10n);
    final farmer = partyDisplayName(order.farmerName, l10n);
    final trucker = partyDisplayName(order.truckerName ?? '', l10n);
    final paidAt = order.paymentVerifiedAt ?? order.createdAt;

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: FwCard(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Semantics(
              header: true,
              child: Text(
                l10n.commitmentTitle,
                style: const TextStyle(
                  fontSize: 15,
                  fontWeight: FontWeight.w800,
                  color: AppTheme.soilDark,
                ),
              ),
            ),
            const SizedBox(height: 12),
            // The buyer paid: the money is in.
            _PartyRow(
              icon: Icons.check,
              tone: _Tone.paid,
              text: _bold(
                l10n.detailPartyPaid(
                  userId == order.buyerId ? l10n.detailYou : buyer,
                ),
                userId == order.buyerId ? l10n.detailYou : buyer,
              ),
              state: _RowState.label(
                ClockLabel.text(
                  clock.dateTime(paidAt, date: DateFormat.MMMd()),
                ),
                _Tone.idle,
              ),
            ),
            const SizedBox(height: 12),
            _PartyRow(
              icon: _farmerIcon,
              tone: _farmerTone,
              text: _bold(
                l10n.detailPartyRole(
                  userId == order.farmerId ? l10n.detailYou : farmer,
                  l10n.farmer.toLowerCase(),
                ),
                userId == order.farmerId ? l10n.detailYou : farmer,
              ),
              state: _farmerState(l10n, clock),
            ),
            if (orderHasTrip(order)) ...[
              const SizedBox(height: 12),
              _PartyRow(
                icon: _truckerIcon,
                tone: _truckerTone,
                text: _bold(
                  trucker.isEmpty ||
                          truckerResponseOf(order) ==
                              CommitmentResponse.notAsked
                      ? l10n.trucker
                      : l10n.detailPartyRole(
                          userId == order.truckerId ? l10n.detailYou : trucker,
                          l10n.trucker.toLowerCase(),
                        ),
                  userId == order.truckerId ? l10n.detailYou : trucker,
                ),
                state: _truckerState(l10n, clock),
              ),
            ],
            if (noteLabel != null) ...[
              const SizedBox(height: 12),
              Container(
                padding: const EdgeInsets.only(top: 10),
                decoration: const BoxDecoration(
                  border: Border(top: BorderSide(color: AppTheme.segmentTrack)),
                ),
                child: _CommitmentNote(label: noteLabel, warning: warning),
              ),
            ],
          ],
        ),
      ),
    );
  }

  /// [sentence] with [name] in bold, as the board writes "<b>You</b> paid".
  static TextSpan _bold(String sentence, String name) {
    final at = name.isEmpty ? -1 : sentence.indexOf(name);
    const bold = TextStyle(fontWeight: FontWeight.w800);
    if (at < 0) return TextSpan(text: sentence);
    return TextSpan(
      children: [
        if (at > 0) TextSpan(text: sentence.substring(0, at)),
        TextSpan(text: name, style: bold),
        TextSpan(text: sentence.substring(at + name.length)),
      ],
    );
  }

  _Tone get _farmerTone => switch (farmerResponseOf(order)) {
        CommitmentResponse.accepted => _Tone.done,
        CommitmentResponse.declined ||
        CommitmentResponse.expired =>
          _Tone.failed,
        _ => _Tone.waiting,
      };

  IconData get _farmerIcon => switch (_farmerTone) {
        _Tone.done => Icons.check,
        _Tone.failed => Icons.close,
        _ => Icons.schedule,
      };

  _Tone get _truckerTone => switch (truckerResponseOf(order)) {
        CommitmentResponse.accepted => _Tone.done,
        CommitmentResponse.pending => _Tone.waiting,
        CommitmentResponse.declined ||
        CommitmentResponse.expired ||
        CommitmentResponse.failedCapacity ||
        CommitmentResponse.cancelled =>
          _Tone.failed,
        _ => _Tone.idle,
      };

  IconData get _truckerIcon => switch (_truckerTone) {
        _Tone.done => Icons.check,
        _Tone.failed => Icons.close,
        _Tone.waiting => Icons.schedule,
        _ => Icons.local_shipping_outlined,
      };

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
      if (hideViewerDeadline) return null;
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
              _CommitmentNote(
                label: ClockLabel.text(warning),
                warning: true,
                boxed: true,
              ),
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

enum _Tone { paid, done, waiting, failed, idle }

class _RowState {
  /// What the party has done; a waiting row carries its deadline.
  final ClockLabel text;
  final _Tone tone;

  _RowState(String label, this.tone) : text = ClockLabel.text(label);

  const _RowState.label(this.text, this.tone);
}

/// One party: a 32 px round icon (blue for the payment, green done, cream
/// waiting, red failed, grey idle), the sentence with the name in bold,
/// and the answer on the right.
class _PartyRow extends StatelessWidget {
  final IconData icon;
  final _Tone tone;
  final TextSpan text;
  final _RowState state;

  const _PartyRow({
    required this.icon,
    required this.tone,
    required this.text,
    required this.state,
  });

  @override
  Widget build(BuildContext context) {
    final (background, foreground) = switch (tone) {
      _Tone.paid => (AppTheme.dropoffBlueTint, AppTheme.dropoffBlue),
      _Tone.done => (AppTheme.fieldGreenTint, AppTheme.fieldGreen),
      _Tone.waiting => (AppTheme.actionCream, AppTheme.money),
      _Tone.failed => (
          AppTheme.statusErrorContainer,
          AppTheme.onStatusErrorContainer
        ),
      _Tone.idle => (AppTheme.segmentTrack, AppTheme.mutedSteel),
    };
    final stateColor = switch (state.tone) {
      _Tone.done => AppTheme.fieldGreen,
      _Tone.waiting => AppTheme.money,
      _Tone.failed => AppTheme.onStatusErrorContainer,
      _ => AppTheme.mutedSteel,
    };
    return Row(
      children: [
        Container(
          width: 32,
          height: 32,
          decoration: BoxDecoration(color: background, shape: BoxShape.circle),
          child: Icon(icon, size: 16, color: foreground),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Text.rich(
            text,
            style: const TextStyle(fontSize: 14, color: AppTheme.soilDark),
          ),
        ),
        const SizedBox(width: 12),
        Flexible(
          child: ClockText(
            state.text,
            textAlign: TextAlign.end,
            style: TextStyle(
              fontSize: 12,
              fontWeight:
                  state.tone == _Tone.idle ? FontWeight.w500 : FontWeight.w700,
              color: stateColor,
            ),
          ),
        ),
      ],
    );
  }
}

/// The note under the rows: plain muted text inside the card, or (in the
/// reason sheet) the cream warning box.
class _CommitmentNote extends StatelessWidget {
  final ClockLabel label;
  final bool warning;
  final bool boxed;

  const _CommitmentNote({
    required this.label,
    this.warning = false,
    this.boxed = false,
  });

  @override
  Widget build(BuildContext context) {
    if (!boxed) {
      return ClockText(
        label,
        style: TextStyle(
          fontSize: 13,
          height: 1.5,
          fontWeight: warning ? FontWeight.w700 : FontWeight.w400,
          color: warning ? AppTheme.actionInk : AppTheme.mutedSteel,
        ),
      );
    }
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(16),
        color: AppTheme.actionCream,
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.warning_amber, size: 20, color: AppTheme.actionInk),
          const SizedBox(width: 12),
          Expanded(
            child: ClockText(
              label,
              style: const TextStyle(color: AppTheme.actionInk),
            ),
          ),
        ],
      ),
    );
  }
}
