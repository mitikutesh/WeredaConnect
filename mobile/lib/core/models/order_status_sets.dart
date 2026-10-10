import 'package:wereda_connect/core/models/order.dart';

/// Shared order-status semantics (specs/contracts/order-lifecycle.md),
/// consumed by the dashboard overview tiles and the profile
/// "In Escrow (ETB)" stat so the web and Isar paths cannot drift.

/// Statuses where the buyer's money is held in escrow — funded through
/// delivered, plus disputed. Excludes disbursing and later: the money is
/// already leaving escrow.
const Set<OrderStatus> escrowHeldOrderStatuses = {
  OrderStatus.funded,
  OrderStatus.accepted,
  OrderStatus.pickupConfirmed,
  OrderStatus.inTransit,
  OrderStatus.delivered,
  OrderStatus.disputed,
};

/// Buyer-facing "active" orders: in flight from creation through acceptance.
const Set<OrderStatus> activeOrderStatuses = {
  OrderStatus.created,
  OrderStatus.pendingPayment,
  OrderStatus.paymentSubmitted,
  OrderStatus.funded,
  OrderStatus.accepted,
};

/// The goods arrived: delivered, and the money stages after it. ESC-09: after
/// delivered the order follows its money to disbursing (which can last days
/// while a manual payout waits) and settled. The buyer may rate the farmer and
/// trucker at any of them (rating-api.md 1.1.0).
const Set<OrderStatus> deliveredOrderStatuses = {
  OrderStatus.delivered,
  OrderStatus.disbursing,
  OrderStatus.settled,
};

/// Enum-name forms for O(1) matching of statuses parsed off the wire
/// (see order_status_wire.dart).
final Set<String> escrowHeldOrderStatusNames =
    escrowHeldOrderStatuses.map((s) => s.name).toSet();
final Set<String> activeOrderStatusNames =
    activeOrderStatuses.map((s) => s.name).toSet();

/// ESC-06: an accepted order nobody picked up by its server-stamped
/// [Order.pickupDeadlineAt] counts as a transport failure, and its buyer may
/// cancel it for a full refund (order-lifecycle.md 1.15.0). This uses the
/// device clock to show the action; the server decides with its own clock.
bool isTransportFailureCancelOpen(Order order, DateTime nowUtc) {
  final deadline = order.pickupDeadlineAt;
  return order.status == OrderStatus.accepted &&
      deadline != null &&
      !nowUtc.isBefore(deadline);
}

/// ETB locked in escrow on one order: the stored full total when present,
/// else the crop subtotal (quantity × price) for legacy orders created
/// before the cost breakdown existed.
double orderEscrowValue({required double subtotal, double? orderTotal}) {
  final total = orderTotal ?? 0;
  return total > 0 ? total : subtotal;
}

/// The farmer's own leg of an escrow-held order: the stored crop cost when
/// present, else the crop subtotal (quantity × price). Never the transport fee
/// or the platform commission, which belong to the trucker and the platform.
double farmerEscrowShare({required double subtotal, double? cropCost}) {
  final crop = cropCost ?? 0;
  return crop > 0 ? crop : subtotal;
}

/// Defensive double for wire JSON values (number or numeric string).
double wireDouble(dynamic value) => switch (value) {
      final num n => n.toDouble(),
      final String s => double.tryParse(s) ?? 0,
      _ => 0,
    };
