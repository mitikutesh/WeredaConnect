import 'dart:async';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// How long the farmer's optional fix may take before "Crops are ready" goes
/// without it: GPS never holds the farmer's check-in back for long.
const Duration farmerCheckInFixWait = Duration(seconds: 5);

/// ESC-34 (FUP-285): the arrival check-ins of an order stamped for the pickup
/// judgement, for its three parties (`delivery-verification-api.md` 2.6.0
/// P.2). One card: a row for the arrival (the trucker, or the buyer on a
/// self-pickup order) and one for the farmer's "Crops are ready", each with
/// the time the server received it (late ones marked), or the viewer's own
/// check-in as it waits on the phone; then the judged outcome, or what the
/// check-ins are for while the pickup waits. Inside the window the viewer
/// gets their own button: "I have arrived" takes a GPS fix and needs one;
/// "Crops are ready" takes one if it can and goes without it otherwise.
class PickupCheckInCard extends StatefulWidget {
  final Order order;
  final String userId;

  /// An action or a reload runs: the button takes no taps.
  final bool busy;

  /// Where the fix comes from; the device's GPS when null.
  final GpsCaptureService? gpsService;

  /// The time now (UTC); the device clock when null. Tests move it.
  final DateTime Function()? clock;

  const PickupCheckInCard({
    required this.order,
    required this.userId,
    this.busy = false,
    this.gpsService,
    this.clock,
    super.key,
  });

  /// Whether the card shows on [order]: stamped and still waiting for its
  /// pickup, or judged anything but a normal pickup. A scanned order needs
  /// no card: the delivery tells the rest.
  static bool showsFor(Order order) {
    final judgement = order.pickupJudgement;
    if (!isPickupJudgementStamped(order)) return false;
    if (judgement == PickupJudgementOutcome.pending) {
      return order.status == OrderStatus.accepted;
    }
    return judgement != PickupJudgementOutcome.scanned;
  }

  @override
  State<PickupCheckInCard> createState() => _PickupCheckInCardState();
}

class _PickupCheckInCardState extends State<PickupCheckInCard> {
  /// The GPS fix is being taken.
  bool _locating = false;

  /// Fires at the next edge of the viewer's window (it opens, or the
  /// deadline passes) so the button comes and goes while the screen stays
  /// open, instead of a tap past the deadline getting refused.
  Timer? _edge;

  Order get _order => widget.order;

  DateTime _now() => (widget.clock?.call() ?? DateTime.now()).toUtc();

  @override
  void initState() {
    super.initState();
    _scheduleEdge();
  }

  @override
  void didUpdateWidget(PickupCheckInCard oldWidget) {
    super.didUpdateWidget(oldWidget);
    _scheduleEdge();
  }

  @override
  void dispose() {
    _edge?.cancel();
    super.dispose();
  }

  /// A rebuild at the next window edge of [_order] still ahead: when check-
  /// ins open, else just after the deadline. None once both passed or the
  /// order takes no check-ins.
  void _scheduleEdge() {
    _edge?.cancel();
    final now = _now();
    _edge = checkInEdgeTimer(nextPickupCheckInEdge(_order, now), now, () {
      if (!mounted) return;
      setState(() {});
      _scheduleEdge();
    });
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final now = _now();
    final chance = checkInChanceFor(_order, widget.userId, now);
    final ownKind = checkInKindFor(_order, widget.userId);
    final note = _note(l10n, now);
    final opensAt = checkInOpensAt(_order);

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: FwCard(
        key: const Key('order_check_in_card'),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Semantics(
              header: true,
              child: Text(
                l10n.checkInTitle,
                style: const TextStyle(
                  fontSize: 15,
                  fontWeight: FontWeight.w800,
                  color: AppTheme.soilDark,
                ),
              ),
            ),
            const SizedBox(height: 12),
            _CheckInRow(
              key: const Key('check_in_row_arrived_pickup'),
              icon: Icons.local_shipping_outlined,
              label: isSelfPickupOrder(_order)
                  ? l10n.checkInPartyBuyer
                  : l10n.checkInPartyTrucker,
              state: _stateOf(CheckInKind.arrivedPickup, l10n, clock),
            ),
            const SizedBox(height: 12),
            _CheckInRow(
              key: const Key('check_in_row_crops_ready'),
              icon: Icons.inventory_2_outlined,
              label: l10n.checkInPartyFarmer,
              state: _stateOf(CheckInKind.cropsReady, l10n, clock),
            ),
            if (note != null) ...[
              const SizedBox(height: 12),
              Container(
                key: const Key('check_in_note'),
                padding: const EdgeInsets.only(top: 10),
                decoration: const BoxDecoration(
                  border: Border(top: BorderSide(color: AppTheme.segmentTrack)),
                ),
                child: Text(
                  note.text,
                  style: TextStyle(
                    fontSize: 13,
                    height: 1.5,
                    fontWeight: note.strong ? FontWeight.w700 : FontWeight.w400,
                    color:
                        note.strong ? AppTheme.actionInk : AppTheme.mutedSteel,
                  ),
                ),
              ),
            ],
            if (chance == CheckInChance.notYet && opensAt != null) ...[
              const SizedBox(height: 12),
              ClockText(
                clock.deadline(
                  opensAt,
                  date: DateFormat.MMMd(),
                  sentence: l10n.checkInOpensAt,
                ),
                key: const Key('check_in_opens_at'),
                style: const TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.money,
                ),
              ),
            ],
            if (chance == CheckInChance.open && ownKind != null) ...[
              const SizedBox(height: 12),
              AbsorbPointer(
                absorbing: widget.busy,
                child: FilledButton(
                  key: const Key('check_in_button'),
                  onPressed: _locating ? null : () => _checkIn(ownKind),
                  style: FilledButton.styleFrom(
                    minimumSize: const Size.fromHeight(52),
                  ),
                  child: _locating
                      ? Row(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            const SizedBox(
                              width: 16,
                              height: 16,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            ),
                            const SizedBox(width: 10),
                            Text(l10n.checkInLocating),
                          ],
                        )
                      : Text(
                          ownKind == CheckInKind.cropsReady
                              ? l10n.checkInCropsReadyButton
                              : l10n.checkInArrivedButton,
                        ),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  /// One row's state: the server's check-in with the time it received it
  /// (late ones marked), else the check-in made on this phone, else none.
  _RowState _stateOf(String kind, AppLocalizations l10n, ClockFormat clock) {
    final received = checkInOf(_order, kind);
    final at = received?.receivedAt;
    if (received != null && at != null) {
      return _RowState(
        clock.deadline(
          at,
          date: DateFormat.MMMd(),
          sentence:
              received.isLate ? l10n.checkInReceivedLate : l10n.checkInReceived,
        ),
        received.isLate ? _Tone.late : _Tone.done,
      );
    }
    final queued = _order.queuedCheckIns[kind];
    if (queued != null) {
      return _RowState(
        clock.deadline(
          queued.madeAt,
          date: DateFormat.MMMd(),
          sentence: queued.sent
              ? l10n.checkInSentFromPhone
              : l10n.checkInSavedOnPhone,
        ),
        _Tone.waiting,
      );
    }
    return _RowState(ClockLabel.text(l10n.checkInNotYet), _Tone.idle);
  }

  /// The judged outcome, or while the pickup waits what the check-ins are
  /// for (or that the deadline passed and the outcome follows).
  ({String text, bool strong})? _note(AppLocalizations l10n, DateTime now) {
    final judgement = _order.pickupJudgement;
    if (judgement == PickupJudgementOutcome.pending) {
      final deadline = _order.pickupDeadlineAt;
      return deadline != null && now.isAfter(deadline)
          ? (text: l10n.checkInDeadlinePassedNote, strong: false)
          : (text: l10n.checkInPendingNote, strong: false);
    }
    return (text: pickupJudgementLabel(l10n, judgement), strong: true);
  }

  /// Take the fix, then send or queue the check-in. The arrival needs a
  /// usable fix and says so without one: a permission denied for good needs
  /// the app settings (offered on the phone; the browser's site settings on
  /// web), anything else needs location on. The farmer's goes without it.
  Future<void> _checkIn(String kind) async {
    final bloc = context.read<OrderBloc>();
    final messenger = ScaffoldMessenger.of(context);
    final l10n = AppLocalizations.of(context)!;
    final gps = widget.gpsService ?? GpsCaptureService();
    setState(() => _locating = true);
    final (:fix, :deniedForever) = await _takeFix(gps, kind);
    if (!mounted) return;
    setState(() => _locating = false);
    if (kind == CheckInKind.arrivedPickup && fix == null) {
      messenger.showSnackBar(
        deniedForever
            ? SnackBar(
                content: Text(
                  kIsWeb
                      ? l10n.checkInLocationBlockedWeb
                      : l10n.checkInLocationBlocked,
                ),
                action: kIsWeb
                    ? null
                    : SnackBarAction(
                        label: l10n.checkInOpenSettings,
                        onPressed: () => gps.openAppSettings().ignore(),
                      ),
              )
            : SnackBar(content: Text(l10n.checkInNeedsLocation)),
      );
      return;
    }
    if (bloc.isClosed) return;
    bloc.add(
      CheckInAtPickup(
        orderId: _order.orderId,
        userId: widget.userId,
        kind: kind,
        fix: fix,
      ),
    );
  }

  /// A fix the server takes, or null, and whether the location permission
  /// was denied for good. The farmer's waits at most [farmerCheckInFixWait].
  /// Never throws.
  Future<({HandoffCheckInFix? fix, bool deniedForever})> _takeFix(
    GpsCaptureService gps,
    String kind,
  ) async {
    try {
      var capture = gps.captureCurrentPosition();
      if (kind == CheckInKind.cropsReady) {
        capture = capture.timeout(
          farmerCheckInFixWait,
          onTimeout: () => const GpsCaptureResult.unavailable('timeout'),
        );
      }
      final result = await capture;
      final latitude = result.latitude;
      final longitude = result.longitude;
      if (!result.isAvailable || latitude == null || longitude == null) {
        return (fix: null, deniedForever: result.permissionDeniedForever);
      }
      final fix = HandoffCheckInFix(
        latitude: latitude,
        longitude: longitude,
        accuracyM: result.accuracy,
      );
      return (fix: isUsableCheckInFix(fix) ? fix : null, deniedForever: false);
    } on Object {
      return (fix: null, deniedForever: false);
    }
  }
}

/// What a check-in ([CheckInSaved]) tells its giver, wherever it was given
/// (the order detail, the delivery detail): sent, or saved on the phone to
/// send later (E-5). A repeat sent nothing: it says it was already given.
void showCheckInSaved(BuildContext context, CheckInSaved state) {
  final l10n = AppLocalizations.of(context)!;
  if (state.alreadyGiven) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(l10n.checkInAlreadyGiven)),
    );
  } else {
    showSavedOrSent(context, sent: l10n.checkInSent).ignore();
  }
}

/// The localized outcome of a pickup judgement ([Order.pickupJudgement]).
String pickupJudgementLabel(AppLocalizations l10n, String? judgement) =>
    switch (judgement) {
      PickupJudgementOutcome.scanned => l10n.pickupJudgementScanned,
      PickupJudgementOutcome.truckerNoShow => l10n.pickupJudgementTruckerNoShow,
      PickupJudgementOutcome.farmerNoShow => l10n.pickupJudgementFarmerNoShow,
      PickupJudgementOutcome.buyerNoShow => l10n.pickupJudgementBuyerNoShow,
      PickupJudgementOutcome.bothPresentNoScan =>
        l10n.pickupJudgementBothPresent,
      PickupJudgementOutcome.uncheckedCheckIn => l10n.pickupJudgementUnchecked,
      _ => l10n.pickupJudgementOther,
    };

enum _Tone { done, late, waiting, idle }

class _RowState {
  final ClockLabel text;
  final _Tone tone;

  const _RowState(this.text, this.tone);
}

/// One party's check-in: a round icon (green received, red late, cream
/// waiting on the phone, grey none), the label, and the state on the right.
class _CheckInRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final _RowState state;

  const _CheckInRow({
    required this.icon,
    required this.label,
    required this.state,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final (background, foreground) = switch (state.tone) {
      _Tone.done => (AppTheme.fieldGreenTint, AppTheme.fieldGreen),
      _Tone.late => (
          AppTheme.statusErrorContainer,
          AppTheme.onStatusErrorContainer
        ),
      _Tone.waiting => (AppTheme.actionCream, AppTheme.money),
      _Tone.idle => (AppTheme.segmentTrack, AppTheme.mutedSteel),
    };
    return Row(
      children: [
        Container(
          width: 32,
          height: 32,
          decoration: BoxDecoration(color: background, shape: BoxShape.circle),
          child: Icon(
            state.tone == _Tone.done ? Icons.check : icon,
            size: 16,
            color: foreground,
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Text(
            label,
            style: const TextStyle(
              fontSize: 14,
              fontWeight: FontWeight.w700,
              color: AppTheme.soilDark,
            ),
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
              color:
                  state.tone == _Tone.idle ? AppTheme.mutedSteel : foreground,
            ),
          ),
        ),
      ],
    );
  }
}
