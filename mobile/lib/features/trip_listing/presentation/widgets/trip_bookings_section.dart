import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
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

/// ESC-15: the paid orders booked on the trucker's own trip, each with its
/// status (a waiting transport request reads "Answer the transport request").
/// Tapping one opens the order, where the trucker accepts or declines. Reads
/// the trucker's orders through the app's order repository (Isar on mobile,
/// `GET /orders?truckerId` on web).
class TripBookingsSection extends StatefulWidget {
  final Trip trip;

  const TripBookingsSection({required this.trip, super.key});

  @override
  State<TripBookingsSection> createState() => _TripBookingsSectionState();
}

class _TripBookingsSectionState extends State<TripBookingsSection> {
  late Future<List<Order>> _orders;

  @override
  void initState() {
    super.initState();
    _orders = _load();
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
    } on Object {
      return const [];
    }
  }

  Future<void> _open(Order order) async {
    await openOrderDetail(context, order.orderId);
    if (mounted) setState(() => _orders = _load());
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    return FutureBuilder<List<Order>>(
      future: _orders,
      builder: (context, snapshot) {
        final orders = snapshot.data ?? const <Order>[];
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              l10n.tripBookings,
              style: theme.textTheme.titleMedium
                  ?.copyWith(fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 12),
            if (orders.isEmpty)
              Text(
                l10n.tripNoBookings,
                style: theme.textTheme.bodyMedium
                    ?.copyWith(color: AppTheme.textMuted),
              )
            else
              for (final order in orders)
                Card(
                  margin: const EdgeInsets.only(bottom: 8),
                  child: ListTile(
                    onTap: () => _open(order),
                    title: Text(
                      '${CropType.labelFor(order.cropType, l10n)} · '
                      '${l10n.quantityKg(order.quantity.toStringAsFixed(0))}',
                    ),
                    subtitle: Text(
                      '${l10n.farmer}: '
                      '${partyDisplayName(order.farmerName, l10n)}',
                    ),
                    trailing: canTruckerRespond(order, widget.trip.truckerId)
                        ? StatusChip(
                            label: l10n.actionAnswerTransport,
                            kind: StatusKind.warning,
                          )
                        : StatusChip(
                            label: orderStatusLabel(order.status, l10n),
                            kind: orderStatusKind(order.status),
                          ),
                  ),
                ),
          ],
        );
      },
    );
  }
}
