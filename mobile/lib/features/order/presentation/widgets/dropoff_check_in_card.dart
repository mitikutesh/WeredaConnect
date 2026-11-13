import 'dart:async';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/order_dropoff_check_in.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// Takes the trucker's arrival photo of a delivery: the camera, compressed
/// and kept on the phone (the sync queue uploads it), or on web uploaded at
/// once.
typedef ArrivalPhotoTaker = Future<PhotoResult> Function(String deliveryId);

/// ESC-36, the client part (FUP-336): the trucker's drop-off check-in and the
/// drop-off judgement of an order the server stamped when it left `accepted`
/// after the pickup (`delivery-verification-api.md` 2.10.0 P.4), for its
/// three parties. One card: the trucker's row with the time the server
/// received the check-in (a late one marked), or the trucker's own check-in
/// as it waits on the phone; then what happens next (the buyer: by when to
/// confirm before the delivery may be confirmed for them, T9), or the judged
/// outcome. On the road the trucker gets "Arrived at drop-off": the arrival
/// photo first (unless the delivery has it), then a GPS fix, which it needs.
class DropoffCheckInCard extends StatefulWidget {
  final Order order;
  final String userId;

  /// An action or a reload runs: the button takes no taps.
  final bool busy;

  /// Offers the trucker's button. False where only the state is shown (the
  /// delivery detail).
  final bool actions;

  /// Where the fix comes from; the device's GPS when null.
  final GpsCaptureService? gpsService;

  /// Takes the arrival photo; the camera ([PhotoManager]) when null.
  final ArrivalPhotoTaker? takeArrivalPhoto;

  /// The time now (UTC); the device clock when null. Tests move it.
  final DateTime Function()? clock;

  const DropoffCheckInCard({
    required this.order,
    required this.userId,
    this.busy = false,
    this.actions = true,
    this.gpsService,
    this.takeArrivalPhoto,
    this.clock,
    super.key,
  });

  /// Whether the card shows on [order]: stamped for the drop-off judgement,
  /// pending or judged (every outcome has its label).
  static bool showsFor(Order order) => isDropoffJudgementStamped(order);

  @override
  State<DropoffCheckInCard> createState() => _DropoffCheckInCardState();
}

enum _Step { idle, photo, locating }

class _DropoffCheckInCardState extends State<DropoffCheckInCard> {
  _Step _step = _Step.idle;

  /// The order's delivery, looked up for the trucker's button: its id and
  /// whether it has the arrival photo already.
  Delivery? _delivery;

  /// The arrival photo taken on a tap whose GPS fix then failed: the next
  /// tap uses it instead of asking for another.
  String? _takenPhoto;

  /// Fires at the next deadline still ahead (T8, or T9 once the trucker
  /// checked in) so the button and the notes follow while the screen stays
  /// open.
  Timer? _edge;

  static const Duration _maxEdgeWait = Duration(hours: 12);

  Order get _order => widget.order;

  DateTime _now() => (widget.clock?.call() ?? DateTime.now()).toUtc();

  bool get _offersButton =>
      widget.actions &&
      dropoffCheckInChanceFor(_order, widget.userId, _now()) ==
          CheckInChance.open;

  @override
  void initState() {
    super.initState();
    _scheduleEdge();
    _lookUpDelivery();
  }

  @override
  void didUpdateWidget(DropoffCheckInCard oldWidget) {
    super.didUpdateWidget(oldWidget);
    _scheduleEdge();
    // A reload may follow a refusal that changed the delivery's photo.
    if (!identical(oldWidget.order, widget.order)) _lookUpDelivery();
  }

  @override
  void dispose() {
    _edge?.cancel();
    super.dispose();
  }

  /// The trucker's delivery for this order, while the button is offered.
  /// No delivery side above this card (or a failed lookup): no button.
  Future<void> _lookUpDelivery() async {
    if (!_offersButton) return;
    Delivery? found;
    try {
      found = await context
          .read<DeliveryBloc>()
          .repository
          .findActiveDeliveryForOrder(
            _order.orderId,
            userId: widget.userId,
            role: 'trucker',
            orderCreatedAt: _order.createdAt,
          );
    } on Object catch (e) {
      debugPrint('[DropoffCheckIn] delivery lookup failed: $e');
    }
    if (!mounted) return;
    setState(() => _delivery = found);
  }

  /// A rebuild at the next deadline still ahead.
  void _scheduleEdge() {
    _edge?.cancel();
    _edge = null;
    if (!isDropoffJudgementPending(_order)) return;
    final now = _now();
    final due = silentBuyerConfirmsAt(_order) ?? _order.dropoffDeadlineAt;
    if (due == null) return;
    final at = due.add(const Duration(seconds: 1));
    if (!at.isAfter(now)) return;
    // At most [_maxEdgeWait] at a time: a browser fires a longer timeout at
    // once, and a rebuild on the way is harmless.
    final wait = at.difference(now);
    _edge = Timer(wait > _maxEdgeWait ? _maxEdgeWait : wait, () {
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
    final note = _note(l10n, clock, now);
    final delivery = _delivery;
    final onTheRoad = delivery != null &&
        (delivery.status == DeliveryStatus.pickedUp ||
            delivery.status == DeliveryStatus.inTransit);

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: FwCard(
        key: const Key('dropoff_check_in_card'),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Semantics(
              header: true,
              child: Text(
                l10n.dropoffCheckInTitle,
                style: const TextStyle(
                  fontSize: 15,
                  fontWeight: FontWeight.w800,
                  color: AppTheme.soilDark,
                ),
              ),
            ),
            const SizedBox(height: 12),
            _TruckerRow(state: _rowState(l10n, clock)),
            if (note != null) ...[
              const SizedBox(height: 12),
              Container(
                key: const Key('dropoff_check_in_note'),
                padding: const EdgeInsets.only(top: 10),
                decoration: const BoxDecoration(
                  border: Border(top: BorderSide(color: AppTheme.segmentTrack)),
                ),
                child: ClockText(
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
            if (_offersButton && onTheRoad) ...[
              const SizedBox(height: 12),
              AbsorbPointer(
                absorbing: widget.busy,
                child: FilledButton.icon(
                  key: const Key('dropoff_check_in_button'),
                  onPressed:
                      _step == _Step.idle ? () => _checkIn(delivery) : null,
                  style: FilledButton.styleFrom(
                    minimumSize: const Size.fromHeight(52),
                  ),
                  icon: _step == _Step.idle
                      ? Icon(
                          _needsPhoto(delivery)
                              ? Icons.photo_camera_outlined
                              : Icons.place_outlined,
                        )
                      : const SizedBox(
                          width: 16,
                          height: 16,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        ),
                  label: Text(
                    _step == _Step.locating
                        ? l10n.checkInLocating
                        : l10n.dropoffCheckInButton,
                  ),
                ),
              ),
              if (_needsPhoto(delivery)) ...[
                const SizedBox(height: 6),
                Text(
                  l10n.dropoffCheckInPhotoHint,
                  key: const Key('dropoff_check_in_photo_hint'),
                  style: const TextStyle(
                    fontSize: 12,
                    color: AppTheme.mutedSteel,
                  ),
                ),
              ],
            ],
          ],
        ),
      ),
    );
  }

  /// Whether the tap still has to take the arrival photo: none on the
  /// delivery (the server's, or this phone's waiting to upload) and none
  /// kept from a tap whose fix failed.
  bool _needsPhoto(Delivery delivery) =>
      _takenPhoto == null && (delivery.arrivalPhotoRef?.isEmpty ?? true);

  /// The trucker's row: the server's check-in with the time it received it
  /// (late ones marked), else the check-in waiting on this phone, else none.
  _RowState _rowState(AppLocalizations l10n, ClockFormat clock) {
    final received = dropoffCheckInOf(_order);
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
    final queued = _order.queuedCheckIns[CheckInKind.arrivedDropoff];
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

  /// What happens next while the goods are on the road and the drop-off
  /// waits for its judgement, or the judged outcome. None while a party's
  /// confirm or dispute waits for the cron to follow it (still `pending`):
  /// the order's own status says what happened.
  ({ClockLabel text, bool strong})? _note(
    AppLocalizations l10n,
    ClockFormat clock,
    DateTime now,
  ) {
    if (!isDropoffJudgementPending(_order)) {
      return (
        text: ClockLabel.text(
          dropoffJudgementLabel(l10n, _order.dropoffJudgement),
        ),
        strong: true,
      );
    }
    if (!dropoffCheckInOrderStatuses.contains(_order.status)) return null;
    final isBuyer = widget.userId == _order.buyerId;
    final confirmsAt = silentBuyerConfirmsAt(_order);
    if (confirmsAt != null) {
      if (now.isAfter(confirmsAt)) {
        return (
          text: ClockLabel.text(l10n.dropoffCheckInOutcomeSoon),
          strong: false,
        );
      }
      return (
        text: clock.deadline(
          confirmsAt,
          date: DateFormat.MMMd(),
          sentence: isBuyer
              ? l10n.dropoffCheckInBuyerConfirmBy
              : l10n.dropoffCheckInPartyConfirmBy,
        ),
        strong: isBuyer,
      );
    }
    final deadline = _order.dropoffDeadlineAt;
    if (deadline == null || now.isAfter(deadline)) {
      return (
        text: ClockLabel.text(l10n.dropoffCheckInOutcomeSoon),
        strong: false,
      );
    }
    return (
      text: clock.deadline(
        deadline,
        date: DateFormat.MMMd(),
        sentence: isDropoffCheckInParty(_order, widget.userId)
            ? l10n.dropoffCheckInTruckerNote
            : l10n.dropoffCheckInWaitingNote,
      ),
      strong: false,
    );
  }

  /// The arrival photo (unless the delivery has it), then the fix, then the
  /// check-in. No fix, no check-in: a permission denied for good needs the
  /// app settings (offered on the phone; the browser's site settings on
  /// web), anything else needs location on. A photo taken before a failed
  /// fix is kept for the next tap.
  Future<void> _checkIn(Delivery delivery) async {
    final bloc = context.read<OrderBloc>();
    final messenger = ScaffoldMessenger.of(context);
    final l10n = AppLocalizations.of(context)!;
    final gps = widget.gpsService ?? GpsCaptureService();

    var photo = _takenPhoto;
    if (_needsPhoto(delivery)) {
      setState(() => _step = _Step.photo);
      final result = await _takePhoto(delivery.deliveryId);
      if (!mounted) return;
      setState(() => _step = _Step.idle);
      if (result.cancelled) return;
      final ref = result.photoRef;
      if (!result.success || ref == null) {
        messenger.showSnackBar(
          SnackBar(content: Text(l10n.deliveryPhotoFailed)),
        );
        return;
      }
      photo = _takenPhoto = ref;
    }

    setState(() => _step = _Step.locating);
    final (:fix, :deniedForever) = await _takeFix(gps);
    if (!mounted) return;
    setState(() => _step = _Step.idle);
    if (fix == null) {
      messenger.showSnackBar(
        deniedForever
            ? SnackBar(
                content: Text(
                  kIsWeb
                      ? l10n.dropoffCheckInLocationBlockedWeb
                      : l10n.dropoffCheckInLocationBlocked,
                ),
                action: kIsWeb
                    ? null
                    : SnackBarAction(
                        label: l10n.checkInOpenSettings,
                        onPressed: () => gps.openAppSettings().ignore(),
                      ),
              )
            : SnackBar(content: Text(l10n.dropoffCheckInNeedsLocation)),
      );
      return;
    }
    if (bloc.isClosed) return;
    _takenPhoto = null;
    bloc.add(
      CheckInAtDropoff(
        orderId: _order.orderId,
        userId: widget.userId,
        deliveryId: delivery.deliveryId,
        fix: fix,
        arrivalPhotoRef: photo,
      ),
    );
  }

  /// The camera, never throwing: a failure is an error result.
  Future<PhotoResult> _takePhoto(String deliveryId) async {
    try {
      final take = widget.takeArrivalPhoto ??
          (id) => PhotoManager().captureAndCompress(
                deliveryId: id,
                type: PhotoType.arrival,
              );
      return await take(deliveryId);
    } on Object catch (e) {
      debugPrint('[DropoffCheckIn] arrival photo failed: $e');
      return const PhotoResult.error('Photo capture failed');
    }
  }

  /// A fix the server takes, or null, and whether the location permission
  /// was denied for good. Never throws.
  Future<({HandoffCheckInFix? fix, bool deniedForever})> _takeFix(
    GpsCaptureService gps,
  ) async {
    try {
      final result = await gps.captureCurrentPosition();
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

/// The localized outcome of a drop-off judgement ([Order.dropoffJudgement]).
/// Plain and without blame: support decides every dispute.
String dropoffJudgementLabel(AppLocalizations l10n, String? judgement) =>
    switch (judgement) {
      DropoffJudgementOutcome.closed => l10n.dropoffJudgementClosed,
      DropoffJudgementOutcome.autoConfirmed =>
        l10n.dropoffJudgementAutoConfirmed,
      DropoffJudgementOutcome.silentBuyer => l10n.dropoffJudgementSilentBuyer,
      DropoffJudgementOutcome.deliveryOverdue =>
        l10n.dropoffJudgementDeliveryOverdue,
      DropoffJudgementOutcome.dropoffOutOfRange =>
        l10n.dropoffJudgementOutOfRange,
      DropoffJudgementOutcome.queued => l10n.dropoffJudgementQueued,
      _ => l10n.dropoffJudgementOther,
    };

enum _Tone { done, late, waiting, idle }

class _RowState {
  final ClockLabel text;
  final _Tone tone;

  const _RowState(this.text, this.tone);
}

/// The trucker's drop-off check-in: a round icon (green received, red late,
/// cream waiting on the phone, grey none), the label, and the state.
class _TruckerRow extends StatelessWidget {
  final _RowState state;

  const _TruckerRow({required this.state});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
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
      key: const Key('dropoff_check_in_row'),
      children: [
        Container(
          width: 32,
          height: 32,
          decoration: BoxDecoration(color: background, shape: BoxShape.circle),
          child: Icon(
            state.tone == _Tone.done ? Icons.check : Icons.local_shipping,
            size: 16,
            color: foreground,
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Text(
            l10n.dropoffCheckInPartyTrucker,
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
