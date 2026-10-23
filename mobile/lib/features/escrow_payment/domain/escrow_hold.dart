import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';

// An order whose escrow is `held` (frozen for review, escrow-protocol.md
// 1.7.0) reads "Under Review" for its buyer, farmer and trucker
// (order-lifecycle.md 1.9.0). The hold lives on the escrow, not on the order,
// so the order screens ask the escrow side. Both reads are party-scoped on the
// server, so no role sees more than before.

/// The id of an order's own escrow (`escrow-order-{orderId}`,
/// escrow-protocol.md, Order-Funded Escrow).
String orderEscrowId(String orderId) => 'escrow-order-$orderId';

/// Order statuses at which the order's escrow can be held: the money is in
/// escrow and waits to settle, or the order was refused, cancelled or
/// disputed and its money waits for the refund or the verdict. At any other
/// status a `held` escrow row is a stale copy (the order moved on with its
/// money), so the order screens show "Under Review" only at these.
const orderHoldableStatuses = {
  OrderStatus.funded,
  OrderStatus.accepted,
  OrderStatus.pickupConfirmed,
  OrderStatus.inTransit,
  OrderStatus.delivered,
  OrderStatus.disputed,
  OrderStatus.disputeResolved,
  OrderStatus.cancelled,
  OrderStatus.rejected,
};

/// Whether the buyer has already paid the escrow of a delivery, so "Pay for
/// delivery" is not offered again: the money is held, paid out, disputed or
/// frozen for review. A status this build does not know counts too, since
/// no action is offered on it. Not `initiated`: a stale abandoned initiation
/// would hide the button forever, and the server's ESCROW_009 guard blocks a
/// genuine double payment.
bool escrowAlreadyPaid(PaymentStatus status) => switch (status) {
      PaymentStatus.escrowed ||
      PaymentStatus.released ||
      PaymentStatus.completed ||
      PaymentStatus.disputed ||
      PaymentStatus.held ||
      PaymentStatus.unknown =>
        true,
      PaymentStatus.initiated ||
      PaymentStatus.cancelled ||
      PaymentStatus.refunded =>
        false,
    };

/// Whether the escrow of [orderId] is `held`. Reads the server when it can
/// (mobile falls back to its cached copy offline). A failure reads as not
/// held: the server still refuses what a held escrow does not allow.
Future<bool> isOrderEscrowHeld(
  BaseEscrowRepository repository,
  String orderId,
) async {
  try {
    final escrow = await repository.refreshEscrow(orderEscrowId(orderId));
    return escrow?.status == PaymentStatus.held;
  } on Object {
    return false;
  }
}

/// The orders whose escrow is `held`, among the newest escrows the server
/// lists for this party: one `GET /escrow/history?status=held` page of up to
/// 50 (the server filters within its page). [role] is the caller's
/// `UserRole.name`. Mobile falls back to its cache offline; a failure reads as
/// none held.
Future<Set<String>> heldOrderIds(
  BaseEscrowRepository repository, {
  required String userId,
  required String role,
}) async {
  try {
    final payments = await repository.refreshHistory(
      userId: userId,
      statusFilter: PaymentStatus.held.name,
      roleFilter: role,
      limit: 50,
    );
    return {
      for (final payment in payments)
        if (payment.status == PaymentStatus.held && payment.orderId != null)
          payment.orderId!,
    };
  } on Object {
    return const {};
  }
}
