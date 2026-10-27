import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';

/// Shared order-status semantics (specs/contracts/order-lifecycle.md),
/// consumed by the dashboard overview tiles and the profile
/// "In Escrow (ETB)" stat so the web and Isar paths cannot drift.

/// Statuses where the buyer's money is held in escrow — funded through
/// delivered, plus disputed and (ESC-15) a funded order whose transport fell
/// through. Excludes disbursing and later: the money is already leaving escrow.
const Set<OrderStatus> escrowHeldOrderStatuses = {
  OrderStatus.funded,
  OrderStatus.awaitingTransport,
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
  OrderStatus.awaitingTransport,
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

/// ESC-48: statuses at which the parties of an order may reach each other
/// (order-lifecycle.md 2.0.0): from the farmer's accept until the money goes
/// back. In `funded` and `awaitingTransport` the farmer's answer decides
/// (ESC-15, [isOrderContactOpen]). Only the fallback for an order the server
/// has not judged yet ([Order.contactOpen] null); the server's flag also
/// closes contact after an admin override refund, which the status alone does
/// not show, and for a trucker who has not committed.
const Set<OrderStatus> contactOpenOrderStatuses = {
  OrderStatus.funded,
  OrderStatus.awaitingTransport,
  OrderStatus.accepted,
  OrderStatus.pickupConfirmed,
  OrderStatus.inTransit,
  OrderStatus.delivered,
  OrderStatus.disbursing,
  OrderStatus.settled,
  OrderStatus.disputed,
};

/// ESC-48: statuses before contact opens, when the order screens say when it
/// will open (`funded`: at the farmer's accept; earlier: after payment).
const Set<OrderStatus> contactNotYetOpenOrderStatuses = {
  OrderStatus.created,
  OrderStatus.pendingPayment,
  OrderStatus.paymentSubmitted,
  OrderStatus.paymentFailed,
  OrderStatus.funded,
};

/// ESC-48: whether the parties of [order] may call, copy and message each
/// other. The server's [Order.contactOpen] decides; a status outside the open
/// set always closes (a local cancel or reject not synced yet), and so does a
/// funded order the farmer has not accepted (ESC-15).
bool isOrderContactOpen(Order order) {
  if (!contactOpenOrderStatuses.contains(order.status)) return false;
  if ((order.status == OrderStatus.funded ||
          order.status == OrderStatus.awaitingTransport) &&
      farmerResponseOf(order) != CommitmentResponse.accepted) {
    return false;
  }
  return order.contactOpen ?? true;
}

/// ESC-48: the phone of the party [userId] of [order], or null when the order
/// is closed for contact or the server sent no number.
String? orderPartyPhone(Order order, String userId) {
  if (!isOrderContactOpen(order)) return null;
  final phone = userId == order.buyerId
      ? order.buyerPhone
      : userId == order.farmerId
          ? order.farmerPhone
          : userId == order.truckerId
              ? order.truckerPhone
              : null;
  final trimmed = phone?.trim() ?? '';
  return trimmed.isEmpty ? null : trimmed;
}

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
