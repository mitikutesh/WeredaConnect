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

/// Enum-name forms for O(1) matching of statuses parsed off the wire
/// (see order_status_wire.dart).
final Set<String> escrowHeldOrderStatusNames =
    escrowHeldOrderStatuses.map((s) => s.name).toSet();
final Set<String> activeOrderStatusNames =
    activeOrderStatuses.map((s) => s.name).toSet();

/// ETB locked in escrow on one order: the stored full total when present,
/// else the crop subtotal (quantity × price) for legacy orders created
/// before the cost breakdown existed.
double orderEscrowValue({required double subtotal, double? orderTotal}) {
  final total = orderTotal ?? 0;
  return total > 0 ? total : subtotal;
}

/// Defensive double for wire JSON values (number or numeric string).
double wireDouble(dynamic value) => switch (value) {
      final num n => n.toDouble(),
      final String s => double.tryParse(s) ?? 0,
      _ => 0,
    };
