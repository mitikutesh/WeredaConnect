import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/trip.dart';
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
/// weight, who sends it to whom, the trucker's pay), a transport request the
/// trucker must answer outlined in gold with Decline and Accept transport
/// inline, and a status pill on the others. Tapping a card opens the order.
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
      return orders
          .where(
            (o) =>
                o.tripId == widget.trip.tripId &&
                tripBookingStatuses.contains(o.status),
          )
          .toList()
        ..sort((a, b) => b.createdAt.compareTo(a.createdAt));
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
            localizeErrorCode(state.code, AppLocalizations.of(context)!),
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
            Semantics(
              header: true,
              child: Text(l10n.tripBookings, style: _sectionTitle),
            ),
            const SizedBox(height: 12),
            if (snapshot.hasError)
              Row(
                children: [
                  Expanded(
                    child: Text(
                      l10n.tripBookingsLoadFailed,
                      style: const TextStyle(
                        fontSize: 14,
                        color: AppTheme.mutedSteel,
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
                  color: AppTheme.mutedSteel,
                ),
              )
            else
              for (final order in orders)
                Padding(
                  padding: const EdgeInsets.only(bottom: 12),
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

const TextStyle _sectionTitle = TextStyle(
  fontSize: 15,
  fontWeight: FontWeight.w800,
  color: AppTheme.soilDark,
);

/// A stamped place, or null when it is missing or blank.
String? _place(String? value) {
  final trimmed = value?.trim() ?? '';
  return trimmed.isEmpty ? null : trimmed;
}

/// One booked order. A request the trucker must answer gets the gold
/// outline, the farmer's accept with the deadline, and the two buttons.
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
    // on an order from before them.
    final from = _place(order.pickupPlace) ?? trip.fromWoreda;
    final to = _place(order.dropoffPlace) ?? trip.toWoreda;
    final pay = order.transportCost ?? 0;

    return Material(
      color: AppTheme.surfaceWhite,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(20),
        side: asked
            ? const BorderSide(color: AppTheme.accentSoft, width: 2)
            : const BorderSide(color: AppTheme.whisperBorder),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onOpen,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          '${CropType.labelFor(order.cropType, l10n)} · '
                          '${l10n.quantityKg(wholeNumber(order.quantity))}',
                          style: const TextStyle(
                            fontSize: 15,
                            fontWeight: FontWeight.w800,
                            color: AppTheme.soilDark,
                          ),
                        ),
                        const SizedBox(height: 2),
                        // The order's farm and drop-off (order-lifecycle
                        // 2.2.0), the trip's ends on an older order.
                        Text(
                          '$farmer, $from → $buyer, $to',
                          style: const TextStyle(
                            fontSize: 12,
                            color: AppTheme.mutedSteel,
                          ),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(width: 10),
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.end,
                    children: [
                      Text(
                        l10n.etbCurrency(formatEtbAmount(pay)),
                        style: const TextStyle(
                          fontSize: 16,
                          fontWeight: FontWeight.w800,
                          color: AppTheme.soilDark,
                        ),
                      ),
                      Text(
                        l10n.detailYourPay,
                        style: const TextStyle(
                          fontSize: 11,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                    ],
                  ),
                ],
              ),
              const SizedBox(height: 10),
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
          ),
        ),
      ),
    );
  }

  /// The booking's state in the board's words: accepted (contact open),
  /// waiting for the farmer (the trucker is asked next), else the status.
  Widget _bookingPill(Order order, AppLocalizations l10n) {
    if (order.status == OrderStatus.funded &&
        farmerResponseOf(order) == CommitmentResponse.pending) {
      return FwPill(
        label: '${l10n.detailBookingWaitingFarmer} · '
            '${l10n.detailBookingAskedNext}',
        background: AppTheme.segmentTrack,
        foreground: AppTheme.unselectedNav,
      );
    }
    if (order.status == OrderStatus.accepted) {
      return FwPill(
        label: '${l10n.commitmentAccepted} · '
            '${l10n.detailBookingAcceptedContactOpen}',
        background: AppTheme.fieldGreenTint,
        foreground: AppTheme.fieldGreen,
      );
    }
    return StatusChip(
      label: orderStatusLabel(order.status, l10n),
      kind: orderStatusKind(order.status),
    );
  }
}

/// The farmer accepted and the trucker is asked: the deadline line and
/// Decline / Accept transport side by side.
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
            date: DateFormat.MMMd(),
            sentence: l10n.detailFarmerAcceptedAnswerBy,
          );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Padding(
              padding: EdgeInsets.only(top: 2),
              child: Icon(Icons.schedule, size: 16, color: AppTheme.money),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: ClockText(
                line,
                style: const TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w700,
                  height: 1.35,
                  color: AppTheme.money,
                ),
              ),
            ),
          ],
        ),
        const SizedBox(height: 12),
        Row(
          children: [
            Expanded(
              child: OutlinedButton(
                key: Key('booking_decline_${order.orderId}'),
                onPressed: locked ? null : onDecline,
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(48),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(14),
                  ),
                  textStyle: const TextStyle(
                    fontFamily: AppTheme.fontFamily,
                    fontFamilyFallback: AppTheme.ethiopicFontFallback,
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                  ),
                ),
                child: Text(l10n.truckerDeclineRequest),
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: FilledButton(
                key: Key('booking_accept_${order.orderId}'),
                onPressed: locked ? null : onAccept,
                style: FilledButton.styleFrom(
                  backgroundColor: AppTheme.soilDark,
                  minimumSize: const Size.fromHeight(48),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(14),
                  ),
                  textStyle: const TextStyle(
                    fontFamily: AppTheme.fontFamily,
                    fontFamilyFallback: AppTheme.ethiopicFontFallback,
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                  ),
                ),
                child: answering
                    ? const SizedBox(
                        width: 18,
                        height: 18,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          color: Colors.white,
                        ),
                      )
                    : Text(l10n.truckerAcceptRequest),
              ),
            ),
          ],
        ),
      ],
    );
  }
}
