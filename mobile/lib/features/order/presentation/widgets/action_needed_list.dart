import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_dropoff_check_in.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/core/utils/date_locale.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/home_cards.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

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

  /// FUP-327: "I have arrived" at the farm, the trucker's (the buyer's on
  /// a self-pickup order), while the check-in window is open.
  checkInAtFarm,

  /// FUP-327: the farmer's "Crops are ready", while the window is open.
  cropsReady,

  /// FUP-371: the trucker's "Arrived at drop-off", until T8.
  checkInAtDropoff,

  /// FUP-371: the buyer confirms after the trucker's counted drop-off
  /// check-in, by T9 (or the delivery may be confirmed for them).
  confirmDelivery,
}

/// One "Action needed" card: the order, what to do, and by when.
class ActionNeededItem {
  final Order order;
  final ActionNeededKind kind;
  final DateTime? dueAt;

  const ActionNeededItem(this.order, this.kind, [this.dueAt]);
}

/// The cards [userId] (in [role]) has among [orders] at [now] (UTC; the
/// device clock when null), most urgent first: a stamped deadline sorts
/// before none, then the newest order. [ordersWithDelivery] holds the
/// orders that already have a live delivery, so a trucker is not asked to
/// set one up again. A check-in card (FUP-327, FUP-371) goes by the same
/// rules as the check-in cards of the order detail, the phone's queued
/// check-ins ([Order.queuedCheckIns]) included; an order without the
/// server's stamps has none.
List<ActionNeededItem> actionNeededItems({
  required List<Order> orders,
  required String userId,
  required UserRole role,
  Set<String> ordersWithDelivery = const {},
  DateTime? now,
}) {
  if (userId.isEmpty) return const [];
  final at = (now ?? DateTime.now()).toUtc();
  final items = <ActionNeededItem>[];
  for (final order in orders) {
    final item = switch (role) {
      UserRole.farmer => _farmerItem(order, userId, at),
      UserRole.trucker => _truckerItem(order, userId, ordersWithDelivery, at),
      UserRole.buyer => _buyerItem(order, userId, at),
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

ActionNeededItem? _farmerItem(Order order, String userId, DateTime now) {
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
  return _farmCheckInItem(order, userId, now);
}

ActionNeededItem? _truckerItem(
  Order order,
  String userId,
  Set<String> ordersWithDelivery,
  DateTime now,
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
  if (dropoffCheckInChanceFor(order, userId, now) == CheckInChance.open) {
    return ActionNeededItem(
      order,
      ActionNeededKind.checkInAtDropoff,
      order.dropoffDeadlineAt,
    );
  }
  return _farmCheckInItem(order, userId, now);
}

ActionNeededItem? _buyerItem(Order order, String userId, DateTime now) {
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
    OrderStatus.accepted => _farmCheckInItem(order, userId, now),
    OrderStatus.pickupConfirmed ||
    OrderStatus.inTransit =>
      _confirmDeliveryItem(order, now),
    _ => null,
  };
}

/// FUP-327: the viewer's own check-in at the farm while its window is open
/// ([checkInChanceFor]): the farmer's "Crops are ready", the trucker's (the
/// self-pickup buyer's) "I have arrived", due at the pickup deadline.
ActionNeededItem? _farmCheckInItem(Order order, String userId, DateTime now) {
  if (checkInChanceFor(order, userId, now) != CheckInChance.open) return null;
  return ActionNeededItem(
    order,
    checkInKindFor(order, userId) == CheckInKind.cropsReady
        ? ActionNeededKind.cropsReady
        : ActionNeededKind.checkInAtFarm,
    order.pickupDeadlineAt,
  );
}

/// FUP-371: the buyer's confirm after the trucker's counted drop-off
/// check-in, due at T9 ([silentBuyerConfirmsAt]); none once T9 passed (the
/// outcome follows, as the order detail says).
ActionNeededItem? _confirmDeliveryItem(Order order, DateTime now) {
  final confirmsAt = silentBuyerConfirmsAt(order);
  if (confirmsAt == null || now.isAfter(confirmsAt)) return null;
  return ActionNeededItem(order, ActionNeededKind.confirmDelivery, confirmsAt);
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
      ActionNeededKind.checkInAtFarm => l10n.actionCheckInAtFarm,
      ActionNeededKind.cropsReady => l10n.actionCropsReady,
      ActionNeededKind.checkInAtDropoff => l10n.actionCheckInAtDropoff,
      ActionNeededKind.confirmDelivery => l10n.actionConfirmDelivery,
    };

/// Home: the order waiting for the signed-in user, above "At a glance"
/// (the Farmer and Trucker home boards show one cream Action needed card).
/// Reads the user's orders through the app's order repository (Isar on
/// mobile, `GET /orders` on web), the user's check-ins waiting on the phone
/// (FUP-327, FUP-371) and for a trucker the deliveries, so it works
/// offline on mobile; on mobile it first pulls the user's orders
/// (best-effort and throttled, as the Orders tab does) so a new request
/// shows without visiting that tab. Shows nothing when nothing waits. The
/// card opens its order; when more orders wait, a "N more" link under it
/// opens the full list through [onShowAll]. A read that fails (offline on
/// web) says so with Retry (E-6), never an empty "nothing waits".
class ActionNeededList extends StatefulWidget {
  final String userId;
  final UserRole role;

  /// Called after an opened order is closed again (the user may have acted).
  final VoidCallback? onRefresh;

  /// Opens the list with every waiting order (the Orders tab). Without it
  /// the "N more" link is left out.
  final VoidCallback? onShowAll;

  /// The time now (UTC); the device clock when null. Tests move it.
  final DateTime Function()? clock;

  /// How many waiting orders get a card, most urgent first. Home shows
  /// one; the buyer's Orders tab, which stands in for a Home, shows more.
  final int maxItems;

  const ActionNeededList({
    required this.userId,
    required this.role,
    super.key,
    this.onRefresh,
    this.onShowAll,
    this.clock,
    this.maxItems = 1,
  });

  @override
  State<ActionNeededList> createState() => _ActionNeededListState();
}

/// What Home read: the user's orders (with this phone's check-in marks)
/// and, for a trucker, the orders that have a live delivery.
typedef _ActionNeededRead = ({List<Order> orders, Set<String> withDelivery});

const _ActionNeededRead _nothingRead = (orders: [], withDelivery: {});

class _ActionNeededListState extends State<ActionNeededList> {
  late Future<_ActionNeededRead> _read;

  /// Counts the reads; only the newest one sets [_edge].
  int _reads = 0;

  /// FUP-327, FUP-371: fires when a check-in window of the orders read
  /// opens or closes (T8, T9), so a check-in card comes and goes while Home
  /// stays open, as on the order detail. Nothing is read again then.
  Timer? _edge;

  DateTime _now() => (widget.clock?.call() ?? DateTime.now()).toUtc();

  @override
  void initState() {
    super.initState();
    _read = _load();
  }

  @override
  void didUpdateWidget(covariant ActionNeededList oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.userId != widget.userId || oldWidget.role != widget.role) {
      _read = _load();
    }
  }

  @override
  void dispose() {
    _edge?.cancel();
    super.dispose();
  }

  Future<_ActionNeededRead> _load() async {
    final read = ++_reads;
    _edge?.cancel();
    if (widget.userId.isEmpty) return _nothingRead;
    await _pullOrders();
    if (!mounted) return _nothingRead;
    final BaseOrderRepository repository;
    try {
      repository = context.read<OrderBloc>().repository;
    } on Object {
      // No order side above this widget: no cards.
      return _nothingRead;
    }
    // A failed read (offline on web) is an error, not "nothing waits".
    var orders = await repository.getOrdersByUser(
      widget.userId,
      role: widget.role.name,
    );
    orders = await _withQueuedCheckIns(repository, orders);
    var withDelivery = const <String>{};
    if (widget.role == UserRole.trucker && mounted) {
      withDelivery = await _ordersWithDelivery();
    }
    if (mounted && read == _reads) _scheduleEdge(orders);
    return (orders: orders, withDelivery: withDelivery);
  }

  /// FUP-327, FUP-371: the user's check-ins waiting on this phone, so one
  /// given offline takes its card away at once (the order detail's marks).
  /// Read only when an order waits for a check-in judgement, from the
  /// phone's queue (no network); unread, the orders show as they are.
  Future<List<Order>> _withQueuedCheckIns(
    BaseOrderRepository repository,
    List<Order> orders,
  ) async {
    if (!orders.any(
      (o) => isPickupJudgementPending(o) || isDropoffJudgementPending(o),
    )) {
      return orders;
    }
    try {
      return await repository.ordersWithQueuedCheckIns(orders, widget.userId);
    } on Object catch (e) {
      debugPrint('[ActionNeeded] queued check-ins not read: $e');
      return orders;
    }
  }

  /// A rebuild at the next check-in window edge of [orders] still ahead.
  void _scheduleEdge(List<Order> orders) {
    _edge?.cancel();
    final now = _now();
    DateTime? next;
    for (final order in orders) {
      for (final at in [
        nextPickupCheckInEdge(order, now),
        nextDropoffCheckInEdge(order, now),
      ]) {
        if (at != null && (next == null || at.isBefore(next))) next = at;
      }
    }
    _edge = checkInEdgeTimer(next, now, () {
      if (!mounted) return;
      setState(() {});
      _scheduleEdge(orders);
    });
  }

  void _retry() {
    setState(() {
      _read = _load();
    });
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
    // A block body: an arrow would hand setState the Future it assigns.
    setState(() {
      _read = _load();
    });
    widget.onRefresh?.call();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return FutureBuilder<_ActionNeededRead>(
      future: _read,
      builder: (context, snapshot) {
        if (snapshot.hasError) {
          return LoadErrorChip(
            key: const Key('action_needed_load_failed'),
            message: l10n.homeActionNeededLoadFailed,
            retryLabel: l10n.retry,
            onRetry: _retry,
          );
        }
        final read = snapshot.data ?? _nothingRead;
        final items = actionNeededItems(
          orders: read.orders,
          userId: widget.userId,
          role: widget.role,
          ordersWithDelivery: read.withDelivery,
          now: _now(),
        );
        if (items.isEmpty) return const SizedBox.shrink();
        final shown = items.take(widget.maxItems).toList();
        final more = items.length - shown.length;
        final onShowAll = widget.onShowAll;
        return Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            for (final item in shown) ...[
              if (item != shown.first) const SizedBox(height: 12),
              _ActionNeededCard(
                item: item,
                role: widget.role,
                l10n: l10n,
                onTap: () => _open(item),
              ),
            ],
            if (more > 0 && onShowAll != null)
              Align(
                alignment: AlignmentDirectional.centerEnd,
                child: TextButton.icon(
                  key: const Key('action_needed_more'),
                  onPressed: onShowAll,
                  style: TextButton.styleFrom(
                    foregroundColor: context.roleColors.primary,
                    padding: const EdgeInsets.symmetric(horizontal: 8),
                    minimumSize: const Size(0, 36),
                    // A 36 tall link in a 48 by 48 hit area.
                    tapTargetSize: MaterialTapTargetSize.padded,
                    textStyle: const TextStyle(
                      fontFamily: AppTheme.fontFamily,
                      fontFamilyFallback: AppTheme.ethiopicFontFallback,
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
  final quantity = kgQuantityText(order.quantity, l10n);
  if (role == UserRole.trucker) {
    final pickup = order.pickupDate;
    return [
      crop,
      quantity,
      if (pickup != null) l10n.orderTripOn(boardDayInEthiopia(pickup)),
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
      date: formatMonthDay,
      sentence: l10n.actionDueBy,
    );
  }

  @override
  Widget build(BuildContext context) {
    final due = item.dueAt;
    // Design canvas page M. The trucker's Home leads with the trip card, so
    // their action card is flat on the orange tint; for the farmer (and the
    // buyer) it is the card the page leads with, raised on paper.
    final flat = role == UserRole.trucker;
    final body = Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            Flexible(child: HomeActionPill(l10n.actionNeededTitle)),
            const SizedBox(width: 12),
            if (due != null)
              Expanded(
                child: ClockText(
                  _due(context, due),
                  textAlign: TextAlign.end,
                  style: TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w700,
                    color: flat ? AppTheme.orangeInk : AppTheme.inkSoft,
                  ),
                ),
              ),
          ],
        ),
        const SizedBox(height: 8),
        Text(
          actionNeededLabel(l10n, item.kind),
          style: const TextStyle(
            fontSize: 19,
            fontWeight: FontWeight.w800,
            letterSpacing: -0.4,
            height: 1.2,
            color: AppTheme.ink,
          ),
        ),
        const SizedBox(height: 8),
        Row(
          children: [
            if (flat) ...[
              const Icon(
                Icons.inventory_2_outlined,
                size: 20,
                color: AppTheme.inkSoft,
              ),
              const SizedBox(width: 6),
            ],
            Expanded(
              child: Text(
                _facts(),
                style: TextStyle(
                  fontSize: 14,
                  color: flat ? AppTheme.inkSoft : AppTheme.slate,
                ),
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
              ),
            ),
            const SizedBox(width: 12),
            HomeOpenMark(label: l10n.homeOpen, pill: !flat),
          ],
        ),
      ],
    );
    return Semantics(
      button: true,
      child: flat
          ? FwCard(
              key: const Key('action_needed_card'),
              color: AppTheme.orangeTint,
              borderColor: null,
              padding: const EdgeInsets.fromLTRB(16, 14, 16, 16),
              onTap: onTap,
              child: body,
            )
          : FwCard(
              key: const Key('action_needed_card'),
              raised: true,
              padding: const EdgeInsets.fromLTRB(16, 16, 16, 18),
              onTap: onTap,
              child: body,
            ),
    );
  }
}
