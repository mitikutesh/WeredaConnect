import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';

/// What an order is waiting for from the signed-in user (ESC-15/21).
enum ActionNeededKind {
  answerOrder,
  answerTransport,
  keepCrops,
  setUpDelivery,
  pay,
  payAgain,
  decideTransport,
  checkDelivery,
}

/// One "Action needed" card: the order, what to do, and by when.
class ActionNeededItem {
  final Order order;
  final ActionNeededKind kind;
  final DateTime? dueAt;

  const ActionNeededItem(this.order, this.kind, [this.dueAt]);
}

/// The cards [userId] (in [role]) has among [orders], most urgent first:
/// a stamped deadline sorts before none, then the newest order.
/// [ordersWithDelivery] holds the orders that already have a live delivery,
/// so a trucker is not asked to set one up again.
List<ActionNeededItem> actionNeededItems({
  required List<Order> orders,
  required String userId,
  required UserRole role,
  Set<String> ordersWithDelivery = const {},
}) {
  if (userId.isEmpty) return const [];
  final items = <ActionNeededItem>[];
  for (final order in orders) {
    final item = switch (role) {
      UserRole.farmer => _farmerItem(order, userId),
      UserRole.trucker => _truckerItem(order, userId, ordersWithDelivery),
      UserRole.buyer => _buyerItem(order, userId),
    };
    if (item != null) items.add(item);
  }
  items.sort((a, b) {
    final aDue = a.dueAt;
    final bDue = b.dueAt;
    if (aDue != null && bDue != null) return aDue.compareTo(bDue);
    if (aDue != null) return -1;
    if (bDue != null) return 1;
    return b.order.createdAt.compareTo(a.order.createdAt);
  });
  return items;
}

ActionNeededItem? _farmerItem(Order order, String userId) {
  if (order.farmerId != userId) return null;
  if (canFarmerRespond(order, userId)) {
    return ActionNeededItem(
      order,
      ActionNeededKind.answerOrder,
      order.farmerDeadlineAt,
    );
  }
  if (order.status == OrderStatus.awaitingTransport &&
      farmerResponseOf(order) == CommitmentResponse.accepted) {
    return ActionNeededItem(
      order,
      ActionNeededKind.keepCrops,
      order.buyerChoiceDeadlineAt,
    );
  }
  return null;
}

ActionNeededItem? _truckerItem(
  Order order,
  String userId,
  Set<String> ordersWithDelivery,
) {
  if (order.truckerId != userId) return null;
  if (canTruckerRespond(order, userId)) {
    return ActionNeededItem(
      order,
      ActionNeededKind.answerTransport,
      order.truckerDeadlineAt,
    );
  }
  if (order.status == OrderStatus.accepted &&
      (order.tripId?.isNotEmpty ?? false) &&
      truckerResponseOf(order) == CommitmentResponse.accepted &&
      !ordersWithDelivery.contains(order.orderId)) {
    return ActionNeededItem(
      order,
      ActionNeededKind.setUpDelivery,
      order.pickupDeadlineAt,
    );
  }
  return null;
}

ActionNeededItem? _buyerItem(Order order, String userId) {
  if (order.buyerId != userId) return null;
  return switch (order.status) {
    OrderStatus.created ||
    OrderStatus.pendingPayment =>
      ActionNeededItem(order, ActionNeededKind.pay),
    OrderStatus.paymentFailed =>
      ActionNeededItem(order, ActionNeededKind.payAgain),
    OrderStatus.awaitingTransport => ActionNeededItem(
        order,
        ActionNeededKind.decideTransport,
        order.buyerChoiceDeadlineAt,
      ),
    OrderStatus.delivered =>
      ActionNeededItem(order, ActionNeededKind.checkDelivery),
    _ => null,
  };
}

/// The card title of [kind].
String actionNeededLabel(AppLocalizations l10n, ActionNeededKind kind) =>
    switch (kind) {
      ActionNeededKind.answerOrder => l10n.actionAnswerOrder,
      ActionNeededKind.answerTransport => l10n.actionAnswerTransport,
      ActionNeededKind.keepCrops => l10n.actionKeepCrops,
      ActionNeededKind.setUpDelivery => l10n.actionSetUpDelivery,
      ActionNeededKind.pay => l10n.actionPay,
      ActionNeededKind.payAgain => l10n.actionPayAgain,
      ActionNeededKind.decideTransport => l10n.actionDecideTransport,
      ActionNeededKind.checkDelivery => l10n.actionCheckDelivery,
    };

/// Home: the orders waiting for the signed-in user, above "At a glance".
/// Reads the user's orders through the app's order repository (Isar on
/// mobile, `GET /orders` on web), and for a trucker the deliveries, so it
/// works offline on mobile; on mobile it first pulls the user's orders
/// (best-effort and throttled, as the Orders tab does) so a new request
/// shows without visiting that tab. Shows nothing when nothing waits. Each
/// card opens the order.
class ActionNeededList extends StatefulWidget {
  final String userId;
  final UserRole role;

  /// Called after an opened order is closed again (the user may have acted).
  final VoidCallback? onRefresh;

  /// Most cards shown; the Orders tab has the rest.
  static const maxShown = 5;

  const ActionNeededList({
    required this.userId,
    required this.role,
    super.key,
    this.onRefresh,
  });

  @override
  State<ActionNeededList> createState() => _ActionNeededListState();
}

class _ActionNeededListState extends State<ActionNeededList> {
  late Future<List<ActionNeededItem>> _items;

  @override
  void initState() {
    super.initState();
    _items = _load();
  }

  @override
  void didUpdateWidget(covariant ActionNeededList oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.userId != widget.userId || oldWidget.role != widget.role) {
      _items = _load();
    }
  }

  Future<List<ActionNeededItem>> _load() async {
    if (widget.userId.isEmpty) return const [];
    await _pullOrders();
    if (!mounted) return const [];
    try {
      final orders = await context
          .read<OrderBloc>()
          .repository
          .getOrdersByUser(widget.userId, role: widget.role.name);
      var withDelivery = const <String>{};
      if (widget.role == UserRole.trucker && mounted) {
        withDelivery = await _ordersWithDelivery();
      }
      return actionNeededItems(
        orders: orders,
        userId: widget.userId,
        role: widget.role,
        ordersWithDelivery: withDelivery,
      );
    } on Object {
      // Offline on web, or no order side above this widget: no cards.
      return const [];
    }
  }

  /// Mobile only (web has no MarketplaceSync and reads the server anyway).
  Future<void> _pullOrders() async {
    try {
      await context
          .read<MarketplaceSync?>()
          ?.pullOrders(userId: widget.userId, role: widget.role.name);
    } on Object {
      // Offline, or no sync above this widget: the device's orders show.
    }
  }

  Future<Set<String>> _ordersWithDelivery() async {
    try {
      final deliveries = await context
          .read<DeliveryBloc>()
          .repository
          .getDeliveriesByUser(widget.userId, role: 'trucker');
      return {
        for (final d in deliveries)
          if (d.status != DeliveryStatus.cancelled && d.orderId != null)
            d.orderId!,
      };
    } on Object {
      // Unknown: better to show the card than to hide a needed step.
      return const {};
    }
  }

  Future<void> _open(ActionNeededItem item) async {
    await openOrderDetail(context, item.order.orderId);
    if (!mounted) return;
    setState(() => _items = _load());
    widget.onRefresh?.call();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return FutureBuilder<List<ActionNeededItem>>(
      future: _items,
      builder: (context, snapshot) {
        final items = snapshot.data ?? const <ActionNeededItem>[];
        if (items.isEmpty) return const SizedBox.shrink();
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              l10n.actionNeededTitle,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w700,
                color: AppTheme.textDark,
              ),
            ),
            const SizedBox(height: 8),
            for (final item in items.take(ActionNeededList.maxShown)) ...[
              _ActionNeededCard(
                item: item,
                l10n: l10n,
                onTap: () => _open(item),
              ),
              const SizedBox(height: 8),
            ],
          ],
        );
      },
    );
  }
}

class _ActionNeededCard extends StatelessWidget {
  final ActionNeededItem item;
  final AppLocalizations l10n;
  final VoidCallback onTap;

  const _ActionNeededCard({
    required this.item,
    required this.l10n,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    final order = item.order;
    final due = item.dueAt;
    final facts = '${CropType.labelFor(order.cropType, l10n)} · '
        '${l10n.quantityKg(order.quantity.toStringAsFixed(0))}';
    final details = due == null
        ? ClockLabel.text(facts)
        : clockFormatOf(context)
            .deadline(
              due,
              date: DateFormat.MMMd(),
              sentence: l10n.actionDueBy,
            )
            .prefixed('$facts · ');
    return Semantics(
      button: true,
      child: Material(
        color: const Color(0xFFFAEED8),
        borderRadius: BorderRadius.circular(20),
        child: InkWell(
          onTap: onTap,
          borderRadius: BorderRadius.circular(20),
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                  children: [
                    const Text(
                      'ACTION NEEDED',
                      style: TextStyle(
                        fontSize: 11,
                        fontWeight: FontWeight.w800,
                        letterSpacing: 0.8,
                        color: Color(0xFF8A5410),
                      ),
                    ),
                    if (due != null)
                      ClockText(
                        details,
                        style: const TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w700,
                          color: Color(0xFF6B400B),
                        ),
                      ),
                  ],
                ),
                const SizedBox(height: 8),
                Text(
                  actionNeededLabel(l10n, item.kind),
                  style: const TextStyle(
                    fontSize: 17,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.soilDark,
                  ),
                ),
                const SizedBox(height: 8),
                Row(
                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                  children: [
                    Expanded(
                      child: Text(
                        facts,
                        style: const TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w600,
                          color: Color(0xFF6B400B),
                        ),
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    const Icon(
                      Icons.arrow_forward_ios,
                      size: 14,
                      color: Color(0xFF6B400B),
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
}
