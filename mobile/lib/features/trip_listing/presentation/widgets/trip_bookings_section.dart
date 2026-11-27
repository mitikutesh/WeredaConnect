import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_delivery_location.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/utils/date_locale.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_commitment_section.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Order statuses shown as bookings of a trip: paid, and the money not
/// settled or refunded yet.
const Set<OrderStatus> tripBookingStatuses = {
  OrderStatus.funded,
  OrderStatus.accepted,
  OrderStatus.pickupConfirmed,
  OrderStatus.inTransit,
  OrderStatus.delivered,
  OrderStatus.disbursing,
  OrderStatus.disputed,
};

/// ESC-15: the paid orders booked on the trucker's own trip, as the Trucker
/// · Trip and bookings board draws them: one card per order (crop and
/// weight, who sends it to whom, the trucker's pay in the role's colour), a
/// transport request the trucker must answer on the raised card with the
/// deadline, "It is your turn to answer", Accept transport and Decline, and
/// a status pill on the others (accepted on sand, waiting for the farmer in
/// a dashed outline). Tapping a card opens the order.
///
/// Reads the trucker's orders through the app's order repository (Isar on
/// mobile, `GET /orders?truckerId` on web). The inline answer goes through a
/// bloc of this section's own, so the Orders tab never sees its states.
class TripBookingsSection extends StatefulWidget {
  final Trip trip;

  const TripBookingsSection({required this.trip, super.key});

  @override
  State<TripBookingsSection> createState() => _TripBookingsSectionState();
}

class _TripBookingsSectionState extends State<TripBookingsSection> {
  late Future<List<Order>> _orders;

  /// The section's own bloc for the inline answer, on the app's order
  /// repository; null when no OrderBloc is above (a test without one).
  OrderBloc? _answerBloc;

  /// The order whose answer is on its way to the server, if any.
  String? _answering;

  @override
  void initState() {
    super.initState();
    try {
      _answerBloc = OrderBloc(
        repository: context.read<OrderBloc>().repository,
      );
    } on ProviderNotFoundException {
      _answerBloc = null;
    }
    _orders = _load();
  }

  @override
  void dispose() {
    _answerBloc?.close();
    super.dispose();
  }

  Future<List<Order>> _load() async {
    try {
      final orders = await context
          .read<OrderBloc>()
          .repository
          .getOrdersByUser(widget.trip.truckerId, role: 'trucker');
      final booked = orders
          .where(
            (o) =>
                o.tripId == widget.trip.tripId &&
                tripBookingStatuses.contains(o.status),
          )
          .toList()
        ..sort((a, b) => b.createdAt.compareTo(a.createdAt));
      // LT-14: the bundled place list names each order's own drop-off.
      await loadPlacesToName(booked);
      return booked;
    } on Object catch (e) {
      // Not "no bookings": a request waiting for an answer may be among
      // them, so the section says it could not read them and offers Retry.
      debugPrint('[TripBookingsSection] orders unreadable: $e');
      rethrow;
    }
  }

  void _reload() {
    if (!mounted) return;
    // A block body: an arrow would hand setState the Future it assigns.
    setState(() {
      _orders = _load();
    });
  }

  Future<void> _open(Order order) async {
    await openOrderDetail(context, order.orderId);
    _reload();
  }

  void _accept(Order order) {
    final bloc = _answerBloc;
    if (bloc == null) return;
    setState(() => _answering = order.orderId);
    bloc.add(
      TruckerAcceptOrder(
        orderId: order.orderId,
        truckerId: widget.trip.truckerId,
      ),
    );
  }

  Future<void> _decline(Order order) async {
    final bloc = _answerBloc;
    if (bloc == null) return;
    final reason = await showDeclineReasonSheet(context);
    if (reason == null || !mounted) return;
    setState(() => _answering = order.orderId);
    bloc.add(
      TruckerDeclineOrder(
        orderId: order.orderId,
        truckerId: widget.trip.truckerId,
        declineReason: reason,
      ),
    );
  }

  void _onAnswer(BuildContext context, OrderState state) {
    if (state is TruckerResponded) {
      _answering = null;
      _reload();
    } else if (state is OrderError) {
      setState(() => _answering = null);
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            localizeErrorCode(
              state.code,
              AppLocalizations.of(context)!,
              message: state.message,
            ),
          ),
        ),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final list = FutureBuilder<List<Order>>(
      future: _orders,
      builder: (context, snapshot) {
        final orders = snapshot.data ?? const <Order>[];
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                const Icon(
                  Icons.assignment_outlined,
                  size: 16,
                  color: AppTheme.slate,
                ),
                const SizedBox(width: 8),
                Expanded(child: FwSectionLabel(l10n.tripBookings)),
              ],
            ),
            const SizedBox(height: 10),
            if (snapshot.hasError)
              Row(
                children: [
                  Expanded(
                    child: Text(
                      l10n.tripBookingsLoadFailed,
                      style: const TextStyle(
                        fontSize: 14,
                        color: AppTheme.slate,
                      ),
                    ),
                  ),
                  TextButton(
                    key: const Key('trip_bookings_retry'),
                    onPressed: _reload,
                    child: Text(l10n.retry),
                  ),
                ],
              )
            else if (orders.isEmpty)
              Text(
                l10n.tripNoBookings,
                style: const TextStyle(
                  fontSize: 14,
                  color: AppTheme.slate,
                ),
              )
            else
              for (final order in orders)
                Padding(
                  padding: const EdgeInsets.only(bottom: 10),
                  child: _BookingCard(
                    order: order,
                    trip: widget.trip,
                    answering: _answering == order.orderId,
                    // One answer at a time: another card's buttons wait.
                    locked: _answering != null,
                    onOpen: () => _open(order),
                    onAccept: () => _accept(order),
                    onDecline: () => _decline(order),
                  ),
                ),
          ],
        );
      },
    );
    final bloc = _answerBloc;
    if (bloc == null) return list;
    return BlocListener<OrderBloc, OrderState>(
      bloc: bloc,
      listener: _onAnswer,
      child: list,
    );
  }
}

/// A stamped place, or null when it is missing or blank.
String? _place(String? value) {
  final trimmed = value?.trim() ?? '';
  return trimmed.isEmpty ? null : trimmed;
}

/// One booked order. A request the trucker must answer gets the raised
/// card, the farmer's accept with the deadline, and the two buttons.
class _BookingCard extends StatelessWidget {
  final Order order;
  final Trip trip;
  final bool answering;

  /// An answer is in flight (on this card or another): the buttons wait.
  final bool locked;
  final VoidCallback onOpen;
  final VoidCallback onAccept;
  final VoidCallback onDecline;

  const _BookingCard({
    required this.order,
    required this.trip,
    required this.answering,
    required this.locked,
    required this.onOpen,
    required this.onAccept,
    required this.onDecline,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final asked = canTruckerRespond(order, trip.truckerId);
    final farmer = partyDisplayName(order.farmerName, l10n);
    final buyer = partyDisplayName(order.buyerName, l10n);
    // The order's own places (order-lifecycle 2.2.0), else the trip's route
    // on an order from before them. LT-14: the drop-off place the buyer
    // picked (sent to the trucker while asked, so the answer reads the
    // real town) before the registered one.
    final from = _place(order.pickupPlace) ?? trip.fromWoreda;
    final to = deliveryPlaceOf(order, PlaceRepository.instance)
            ?.displayName(Localizations.localeOf(context).languageCode) ??
        _place(order.dropoffPlace) ??
        trip.toWoreda;
    final pay = order.transportCost ?? 0;

    // The board's three grounds: the request to answer leads on the
    // raised card, an accepted booking sits on sand, one still with the
    // farmer is only outlined (dashed).
    final waitingFarmer = !asked &&
        order.status == OrderStatus.funded &&
        farmerResponseOf(order) == CommitmentResponse.pending;
    final accepted = !asked && order.status == OrderStatus.accepted;
    final muted = accepted ? AppTheme.inkSoft : AppTheme.slate;

    final content = Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Padding(
                        padding: EdgeInsets.only(top: asked ? 3 : 1),
                        child: const Icon(
                          Icons.inventory_2_outlined,
                          size: 20,
                          color: AppTheme.ink,
                        ),
                      ),
                      const SizedBox(width: 6),
                      Expanded(
                        child: Text(
                          '${CropType.labelFor(order.cropType, l10n)} · '
                          '${kgQuantityText(order.quantity, l10n)}',
                          style: TextStyle(
                            fontSize: asked ? 19 : 15,
                            fontWeight: FontWeight.w800,
                            letterSpacing: asked ? -0.4 : -0.15,
                            color: AppTheme.ink,
                          ),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 3),
                  // The order's farm and drop-off (order-lifecycle
                  // 2.2.0), the trip's ends on an older order.
                  Text(
                    '$farmer, $from → $buyer, $to',
                    style: TextStyle(fontSize: 14, height: 1.35, color: muted),
                  ),
                ],
              ),
            ),
            const SizedBox(width: 12),
            Column(
              crossAxisAlignment: CrossAxisAlignment.end,
              children: [
                Text(
                  etbAmountText(pay, l10n),
                  style: TextStyle(
                    fontSize: asked ? 17 : 15,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -0.15,
                    color: context.roleColors.primary,
                  ),
                ),
                const SizedBox(height: 1),
                Text(
                  l10n.detailYourPay,
                  style: TextStyle(fontSize: 13, color: muted),
                ),
              ],
            ),
          ],
        ),
        SizedBox(height: asked ? 12 : 10),
        if (asked)
          _TransportRequest(
            order: order,
            answering: answering,
            locked: locked,
            onAccept: onAccept,
            onDecline: onDecline,
          )
        else
          Align(
            alignment: AlignmentDirectional.centerStart,
            child: _bookingPill(order, l10n),
          ),
      ],
    );

    if (asked) {
      return FwCard(
        raised: true,
        onTap: onOpen,
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 18),
        child: content,
      );
    }
    if (accepted) return FwCard.sand(onTap: onOpen, child: content);
    if (waitingFarmer) {
      return CustomPaint(
        foregroundPainter: const _DashedEdgePainter(),
        child: Material(
          color: Colors.transparent,
          borderRadius: BorderRadius.circular(24),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onOpen,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(16, 14, 16, 16),
              child: content,
            ),
          ),
        ),
      );
    }
    return FwCard(onTap: onOpen, child: content);
  }

  /// The booking's state in the board's words: accepted (contact open),
  /// waiting for the farmer (the trucker is asked next), else the status.
  Widget _bookingPill(Order order, AppLocalizations l10n) {
    if (order.status == OrderStatus.funded &&
        farmerResponseOf(order) == CommitmentResponse.pending) {
      return FwPill(
        label: '${l10n.detailBookingWaitingFarmer} · '
            '${l10n.detailBookingAskedNext}',
        background: AppTheme.sand,
        foreground: AppTheme.inkSoft,
      );
    }
    if (order.status == OrderStatus.accepted) {
      return FwPill(
        label: '${l10n.commitmentAccepted} · '
            '${l10n.detailBookingAcceptedContactOpen}',
      );
    }
    return StatusChip(
      label: orderStatusLabel(order.status, l10n),
      kind: orderStatusKind(order.status),
      icon: orderStatusIcon(order.status),
    );
  }
}

/// The farmer accepted and the trucker is asked: the deadline line, whose
/// turn it is, Accept transport and Decline under it.
class _TransportRequest extends StatelessWidget {
  final Order order;
  final bool answering;
  final bool locked;
  final VoidCallback onAccept;
  final VoidCallback onDecline;

  const _TransportRequest({
    required this.order,
    required this.answering,
    required this.locked,
    required this.onAccept,
    required this.onDecline,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final deadline = order.truckerDeadlineAt;
    final line = deadline == null
        ? ClockLabel.text(l10n.actionAnswerTransport)
        : clockFormatOf(context).deadline(
            deadline,
            date: formatMonthDay,
            sentence: l10n.detailFarmerAcceptedAnswerBy,
          );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // The deadline on the orange tint: the one thing that is urgent.
        Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          decoration: BoxDecoration(
            color: AppTheme.orangeTint,
            borderRadius: BorderRadius.circular(16),
          ),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Padding(
                padding: EdgeInsets.only(top: 1),
                child:
                    Icon(Icons.schedule, size: 16, color: AppTheme.orangeInk),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: ClockText(
                  line,
                  style: const TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w700,
                    height: 1.35,
                    color: AppTheme.orangeInk,
                  ),
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 12),
        // Whose move it is, and what the answer opens.
        Text(
          l10n.tripYourTurnNote,
          key: Key('booking_your_turn_${order.orderId}'),
          style: const TextStyle(
            fontSize: 14,
            height: 1.35,
            color: AppTheme.slate,
          ),
        ),
        const SizedBox(height: 12),
        // The one orange action, then the quiet way out under it.
        FilledButton(
          key: Key('booking_accept_${order.orderId}'),
          onPressed: locked ? null : onAccept,
          child: answering
              ? const SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(
                    strokeWidth: 2,
                    color: AppTheme.ink,
                  ),
                )
              : Text(l10n.truckerAcceptRequest),
        ),
        const SizedBox(height: 12),
        Center(
          child: OutlinedButton(
            key: Key('booking_decline_${order.orderId}'),
            onPressed: locked ? null : onDecline,
            style: OutlinedButton.styleFrom(
              minimumSize: const Size(48, 48),
              padding: const EdgeInsets.symmetric(horizontal: 28),
            ),
            child: Text(l10n.truckerDeclineRequest),
          ),
        ),
      ],
    );
  }
}

/// The dashed edge of a booking that still waits for the farmer (the
/// board: 1.5 px dashes, radius 24).
class _DashedEdgePainter extends CustomPainter {
  const _DashedEdgePainter();

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = AppTheme.fieldBorder
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5;
    final path = Path()
      ..addRRect(
        RRect.fromRectAndRadius(
          (Offset.zero & size).deflate(0.75),
          const Radius.circular(24),
        ),
      );
    for (final metric in path.computeMetrics()) {
      for (var d = 0.0; d < metric.length; d += 10) {
        canvas.drawPath(
          metric.extractPath(d, (d + 5).clamp(0, metric.length)),
          paint,
        );
      }
    }
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}
