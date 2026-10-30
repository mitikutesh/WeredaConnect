import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
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

/// Home: the order waiting for the signed-in user, above "At a glance"
/// (the Farmer and Trucker home boards show one cream Action needed card).
/// Reads the user's orders through the app's order repository (Isar on
/// mobile, `GET /orders` on web), and for a trucker the deliveries, so it
/// works offline on mobile; on mobile it first pulls the user's orders
/// (best-effort and throttled, as the Orders tab does) so a new request
/// shows without visiting that tab. Shows nothing when nothing waits. The
/// card opens its order; when more orders wait, a "N more" link under it
/// opens the full list through [onShowAll].
class ActionNeededList extends StatefulWidget {
  final String userId;
  final UserRole role;

  /// Called after an opened order is closed again (the user may have acted).
  final VoidCallback? onRefresh;

  /// Opens the list with every waiting order (the Orders tab). Without it
  /// the "N more" link is left out.
  final VoidCallback? onShowAll;

  const ActionNeededList({
    required this.userId,
    required this.role,
    super.key,
    this.onRefresh,
    this.onShowAll,
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
        final more = items.length - 1;
        final onShowAll = widget.onShowAll;
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            _ActionNeededCard(
              item: items.first,
              role: widget.role,
              l10n: l10n,
              onTap: () => _open(items.first),
            ),
            if (more > 0 && onShowAll != null)
              Align(
                alignment: AlignmentDirectional.centerEnd,
                child: TextButton.icon(
                  key: const Key('action_needed_more'),
                  onPressed: onShowAll,
                  style: TextButton.styleFrom(
                    foregroundColor: AppTheme.money,
                    padding: const EdgeInsets.symmetric(horizontal: 8),
                    minimumSize: const Size(0, 36),
                    tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                    textStyle: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                  iconAlignment: IconAlignment.end,
                  icon: const Icon(Icons.chevron_right, size: 18),
                  label: Text(l10n.homeActionMore(more)),
                ),
              ),
          ],
        );
      },
    );
  }
}

/// The Action needed card's fact line. A farmer or a buyer reads the other
/// party, the crop and the quantity ("Abebe Trading · Wheat · 1,200 kg"); a
/// trucker reads the crop, the quantity and the trip's day once the order
/// carries it ("Wheat · 1,500 kg · Thu, Oct 30 trip"), as on the boards.
@visibleForTesting
String actionNeededFacts(Order order, UserRole role, AppLocalizations l10n) {
  final crop = CropType.labelFor(order.cropType, l10n);
  final quantity = l10n.quantityKg(wholeNumber(order.quantity));
  if (role == UserRole.trucker) {
    final pickup = order.pickupDate;
    return [
      crop,
      quantity,
      if (pickup != null)
        l10n.orderTripOn(DateFormat.MMMEd().format(dayInEthiopia(pickup))),
    ].join(' · ');
  }
  final party = partyDisplayName(
    role == UserRole.buyer ? order.farmerName : order.buyerName,
    l10n,
  );
  return [if (party.isNotEmpty) party, crop, quantity].join(' · ');
}

class _ActionNeededCard extends StatelessWidget {
  final ActionNeededItem item;
  final UserRole role;
  final AppLocalizations l10n;
  final VoidCallback onTap;

  const _ActionNeededCard({
    required this.item,
    required this.role,
    required this.l10n,
    required this.onTap,
  });

  String _facts() => actionNeededFacts(item.order, role, l10n);

  /// "Due today 16:00", or with the day when the deadline is another day.
  ClockLabel _due(BuildContext context, DateTime due) {
    final clock = clockFormatOf(context);
    final today = dayInEthiopia(DateTime.now().toUtc());
    if (DateUtils.isSameDay(dayInEthiopia(due), today)) {
      return clock.deadline(due, sentence: l10n.homeDueToday);
    }
    return clock.deadline(
      due,
      date: DateFormat.MMMd(),
      sentence: l10n.actionDueBy,
    );
  }

  @override
  Widget build(BuildContext context) {
    final due = item.dueAt;
    return Semantics(
      button: true,
      child: Material(
        color: AppTheme.actionCream,
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
                  children: [
                    Expanded(
                      child: Text(
                        l10n.actionNeededTitle.toUpperCase(),
                        style: const TextStyle(
                          fontSize: 11,
                          fontWeight: FontWeight.w800,
                          letterSpacing: 0.9,
                          color: AppTheme.money,
                        ),
                      ),
                    ),
                    if (due != null)
                      ClockText(
                        _due(context, due),
                        style: const TextStyle(
                          fontSize: 12,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.actionInk,
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
                    letterSpacing: -0.2,
                    color: AppTheme.soilDark,
                  ),
                ),
                const SizedBox(height: 8),
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        _facts(),
                        style: const TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w600,
                          color: AppTheme.actionInk,
                        ),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    const Icon(
                      Icons.chevron_right,
                      size: 20,
                      color: AppTheme.actionInk,
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
