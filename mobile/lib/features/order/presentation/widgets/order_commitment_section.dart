import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/utils/date_locale.dart';
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

/// ESC-15: who has agreed to a paid order, as the order boards draw it
/// (design canvas page M: BuyerOrderDetailM and FarmerOrderRequestM): a row
/// per party (the buyer who paid, the farmer, then the trucker on a trip
/// order) with a round icon, the name in bold and the answer as a pill on
/// the right. The buyer reads it on the raised paper card, with under a
/// rule who the order waits for ("You do not need to do anything") and why
/// it cannot be cancelled yet. The farmer and the trucker read it on a flat
/// sand tile under the section label; the farmer whose answer is due reads
/// "It is your turn" under their own row.
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
    final isBuyer = userId == order.buyerId;
    final waitingOn = isBuyer ? _buyerWaitingNote(l10n, clock) : null;
    final yourTurn = _farmerTurnNote(l10n, clock);

    final label = Row(
      children: [
        const Icon(
          Icons.handshake_outlined,
          size: 16,
          color: AppTheme.slate,
        ),
        const SizedBox(width: 8),
        Expanded(child: FwSectionLabel(l10n.commitmentTitle)),
      ],
    );
    final rows = <Widget>[
      // The buyer paid: the money is in.
      _PartyRow(
        icon: Icons.check,
        tone: _Tone.paid,
        onSand: !isBuyer,
        text: _bold(
          l10n.detailPartyPaid(isBuyer ? l10n.detailYou : buyer),
          isBuyer ? l10n.detailYou : buyer,
        ),
        state: _RowState.label(
          ClockLabel.text(clock.dateTime(paidAt, date: formatMonthDay)),
          _Tone.idle,
        ),
      ),
      const SizedBox(height: 12),
      _PartyRow(
        icon: _farmerIcon,
        tone: _farmerTone,
        onSand: !isBuyer,
        text: _bold(
          l10n.detailPartyRole(
            userId == order.farmerId ? l10n.detailYou : farmer,
            l10n.farmer.toLowerCase(),
          ),
          userId == order.farmerId ? l10n.detailYou : farmer,
        ),
        state: _farmerState(l10n, clock),
      ),
      if (yourTurn != null) ...[
        const SizedBox(height: 10),
        // Under the farmer's own row, in line with its text.
        Padding(
          padding: const EdgeInsetsDirectional.only(start: 44),
          child: ClockText(
            yourTurn,
            key: const Key('commitment_your_turn_note'),
            style: const TextStyle(
              fontSize: 14,
              height: 1.4,
              color: AppTheme.ink,
            ),
          ),
        ),
      ],
      if (orderHasTrip(order)) ...[
        const SizedBox(height: 12),
        _PartyRow(
          icon: _truckerIcon,
          tone: _truckerTone,
          onSand: !isBuyer,
          text: _bold(
            trucker.isEmpty ||
                    truckerResponseOf(order) == CommitmentResponse.notAsked
                ? l10n.trucker
                : l10n.detailPartyRole(
                    userId == order.truckerId ? l10n.detailYou : trucker,
                    l10n.trucker.toLowerCase(),
                  ),
            userId == order.truckerId
                ? l10n.detailYou
                : (trucker.isEmpty ||
                        truckerResponseOf(order) == CommitmentResponse.notAsked
                    ? l10n.trucker
                    : trucker),
          ),
          state: _truckerState(l10n, clock),
        ),
      ],
      if (waitingOn != null || noteLabel != null) ...[
        const SizedBox(height: 12),
        const Divider(height: 1, thickness: 1, color: AppTheme.sketch),
        const SizedBox(height: 10),
        if (waitingOn != null) ...[
          // Who the order waits for, and that the buyer has nothing to do.
          ClockText(
            waitingOn,
            key: const Key('commitment_waiting_on_note'),
            style: const TextStyle(
              fontSize: 14,
              height: 1.45,
              fontWeight: FontWeight.w700,
              color: AppTheme.ink,
            ),
          ),
          if (noteLabel != null) const SizedBox(height: 8),
        ],
        if (noteLabel != null)
          _CommitmentNote(label: noteLabel, warning: warning),
      ],
    ];

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: isBuyer
          // BuyerOrderDetailM: the raised card the page leads with.
          ? FwCard(
              raised: true,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [label, const SizedBox(height: 12), ...rows],
              ),
            )
          // FarmerOrderRequestM: the label, then a flat sand tile.
          : Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                label,
                const SizedBox(height: 10),
                FwCard.sand(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: rows,
                  ),
                ),
              ],
            ),
    );
  }

  /// BuyerOrderDetailM: "Now waiting for Dawit Tesfaye, the trucker, to
  /// answer by 16:00. You do not need to do anything. When the trucker
  /// accepts, contact opens here." Null unless the paid order waits for the
  /// farmer's or the trucker's answer.
  ClockLabel? _buyerWaitingNote(AppLocalizations l10n, ClockFormat clock) {
    if (order.status != OrderStatus.funded) return null;
    final String who;
    final DateTime? deadline;
    final String next;
    if (farmerResponseOf(order) == CommitmentResponse.pending) {
      final name = partyDisplayName(order.farmerName, l10n);
      who = name.isEmpty
          ? l10n.commitmentTheFarmer
          : l10n.commitmentWhoFarmer(name);
      deadline = order.farmerDeadlineAt;
      next = orderHasTrip(order) ? l10n.commitmentThenTruckerAsked : '';
    } else if (truckerResponseOf(order) == CommitmentResponse.pending) {
      final name = partyDisplayName(order.truckerName ?? '', l10n);
      who = name.isEmpty
          ? l10n.commitmentTheTrucker
          : l10n.commitmentWhoTrucker(name);
      deadline = order.truckerDeadlineAt;
      next = l10n.commitmentContactOpensOnTruckerAccept;
    } else {
      return null;
    }
    final tail = next.isEmpty ? '' : ' $next';
    if (deadline == null) {
      return ClockLabel.text('${l10n.commitmentBuyerWaitingOn(who)}$tail');
    }
    final label = clock.deadline(
      deadline,
      date: formatMonthDay,
      sentence: (time) => l10n.commitmentBuyerWaitingOnBy(who, time),
    );
    return ClockLabel(
      before: label.before,
      time: label.time,
      time24: label.time24,
      after: '${label.after}$tail',
    );
  }

  /// FarmerOrderRequestM: "It is your turn: accept or reject by today
  /// 18:00. When you accept, the trucker is asked next and the buyer's
  /// payment stays in escrow." Null unless the viewer is the farmer whose
  /// answer is due.
  ClockLabel? _farmerTurnNote(AppLocalizations l10n, ClockFormat clock) {
    if (!canFarmerRespond(order, userId)) return null;
    final next = orderHasTrip(order)
        ? l10n.commitmentFarmerAcceptThenTrucker
        : l10n.commitmentFarmerAcceptEscrowStays;
    final deadline = order.farmerDeadlineAt;
    if (deadline == null) {
      return ClockLabel.text('${l10n.commitmentYourTurn} $next');
    }
    final label = clock.deadline(
      deadline,
      date: formatMonthDay,
      sentence: l10n.commitmentYourTurnBy,
    );
    return ClockLabel(
      before: label.before,
      time: label.time,
      time24: label.time24,
      after: '${label.after} $next',
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
        CommitmentResponse.expired ||
        CommitmentResponse.noShow =>
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
        CommitmentResponse.cancelled ||
        CommitmentResponse.noShow =>
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
      // ESC-34: the pickup judgement found the crops not ready.
      CommitmentResponse.noShow =>
        _RowState(l10n.commitmentFarmerNoShow, _Tone.failed),
      _ => _waiting(
          l10n,
          clock,
          order.farmerDeadlineAt,
          own: userId == order.farmerId,
        ),
    };
  }

  _RowState _truckerState(AppLocalizations l10n, ClockFormat clock) {
    return switch (truckerResponseOf(order)) {
      CommitmentResponse.accepted =>
        _RowState(l10n.commitmentAccepted, _Tone.done),
      CommitmentResponse.pending => _waiting(
          l10n,
          clock,
          order.truckerDeadlineAt,
          own: userId.isNotEmpty && userId == order.truckerId,
        ),
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
      // ESC-34: the pickup judgement found nobody at the farm.
      CommitmentResponse.noShow =>
        _RowState(l10n.commitmentTruckerNoShow, _Tone.failed),
      _ => _RowState(l10n.commitmentNotAskedYet, _Tone.idle),
    };
  }

  static _RowState _waiting(
    AppLocalizations l10n,
    ClockFormat clock,
    DateTime? deadline, {
    required bool own,
  }) =>
      _RowState.label(
        // The viewer's own row says it waits for them (their deadline is in
        // the banner and the note); another party's says when it is due.
        deadline == null || own
            ? ClockLabel.text(l10n.commitmentWaiting)
            : clock.deadline(
                deadline,
                date: formatMonthDay,
                sentence: l10n.commitmentAnswerDue,
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
                date: formatMonthDay,
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
        date: formatMonthDay,
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
      // ESC-34: the pickup judgement (order-lifecycle 2.15.0).
      'trucker_no_show' => l10n.transportFailureTruckerNoShow,
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
    backgroundColor: AppTheme.ground,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
    ),
    builder: (sheetContext) => SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: Container(
                width: 44,
                height: 4,
                decoration: BoxDecoration(
                  color: AppTheme.line,
                  borderRadius: BorderRadius.circular(2),
                ),
              ),
            ),
            const SizedBox(height: 18),
            Semantics(
              header: true,
              child: Text(
                l10n.declineReasonTitle,
                style: const TextStyle(
                  fontSize: 22,
                  height: 1.15,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -0.6,
                  color: AppTheme.ink,
                ),
              ),
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

/// One party: a 32 px round icon (the payment's check, the role tint when
/// done, orange tint while waiting, red failed, a dashed ring while not
/// asked), the sentence with the name in bold, and the answer on the right:
/// a pill for an answer or a deadline, plain muted text for a time or for
/// "not asked yet".
class _PartyRow extends StatelessWidget {
  final IconData icon;
  final _Tone tone;
  final TextSpan text;
  final _RowState state;

  /// The row sits on the sand tile (the farmer's and the trucker's view),
  /// not on the buyer's paper card.
  final bool onSand;

  const _PartyRow({
    required this.icon,
    required this.tone,
    required this.text,
    required this.state,
    this.onSand = false,
  });

  @override
  Widget build(BuildContext context) {
    final (background, foreground) = switch (tone) {
      _Tone.paid => onSand
          ? (AppTheme.ink, Colors.white)
          : (AppTheme.sand, AppTheme.inkSoft),
      _Tone.done => (context.roleColors.tint, context.roleColors.primary),
      _Tone.waiting => (AppTheme.orangeTint, AppTheme.orangeInk),
      _Tone.failed => (
          AppTheme.statusErrorContainer,
          AppTheme.onStatusErrorContainer
        ),
      _Tone.idle => (Colors.transparent, AppTheme.slate),
    };
    final muted = onSand ? AppTheme.inkSoft : AppTheme.slate;
    final Widget answer = switch (state.tone) {
      _Tone.done => _AnswerPill(
          label: state.text,
          background: context.roleColors.tint,
          foreground: context.roleColors.primary,
        ),
      // The one thing still open: the orange pill with the ink edge.
      _Tone.waiting => _AnswerPill(
          label: state.text,
          background: AppTheme.orange,
          foreground: AppTheme.ink,
          edged: true,
        ),
      _Tone.failed => _AnswerPill(
          label: state.text,
          background: AppTheme.statusErrorContainer,
          foreground: AppTheme.onStatusErrorContainer,
        ),
      _ => ClockText(
          state.text,
          textAlign: TextAlign.end,
          style: TextStyle(fontSize: 13, color: muted),
        ),
    };
    final circle = Container(
      width: 32,
      height: 32,
      decoration: BoxDecoration(color: background, shape: BoxShape.circle),
      child: Icon(icon, size: 16, color: foreground),
    );
    return Row(
      children: [
        // Not asked yet: a dashed ring, as the board draws the party the
        // order has not reached.
        if (tone == _Tone.idle)
          CustomPaint(painter: const _DashedRingPainter(), child: circle)
        else
          circle,
        const SizedBox(width: 12),
        Expanded(
          child: Text.rich(
            text,
            style: const TextStyle(fontSize: 14, color: AppTheme.ink),
          ),
        ),
        const SizedBox(width: 12),
        // A pill keeps its words on one line when it can; a time or "not
        // asked yet" shares the row.
        if (answer is _AnswerPill)
          ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 190),
            child: answer,
          )
        else
          Flexible(child: answer),
      ],
    );
  }
}

/// A party's answer as the boards' 26 px pill; the text wraps rather than
/// clips when a deadline is long.
class _AnswerPill extends StatelessWidget {
  final ClockLabel label;
  final Color background;
  final Color foreground;
  final bool edged;

  const _AnswerPill({
    required this.label,
    required this.background,
    required this.foreground,
    this.edged = false,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: const BoxConstraints(minHeight: 26),
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(13),
        border: edged ? Border.all(color: AppTheme.ink, width: 1.5) : null,
      ),
      child: ClockText(
        label,
        textAlign: TextAlign.center,
        style: TextStyle(
          fontSize: 12,
          fontWeight: FontWeight.w800,
          color: foreground,
        ),
      ),
    );
  }
}

/// A 1.5 px dashed ring around a 32 px circle.
class _DashedRingPainter extends CustomPainter {
  const _DashedRingPainter();

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5
      ..color = AppTheme.fieldBorder;
    final rect = Offset.zero & size;
    const dashes = 16;
    const sweep = 2 * 3.141592653589793 / dashes;
    for (var i = 0; i < dashes; i++) {
      canvas.drawArc(rect.deflate(0.75), i * sweep, sweep * 0.55, false, paint);
    }
  }

  @override
  bool shouldRepaint(_DashedRingPainter oldDelegate) => false;
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
          color: warning ? AppTheme.orangeInk : AppTheme.slate,
        ),
      );
    }
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(16),
        color: AppTheme.orangeTint,
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(Icons.warning_amber, size: 20, color: AppTheme.orangeInk),
          const SizedBox(width: 12),
          Expanded(
            child: ClockText(
              label,
              style: const TextStyle(color: AppTheme.orangeInk),
            ),
          ),
        ],
      ),
    );
  }
}
