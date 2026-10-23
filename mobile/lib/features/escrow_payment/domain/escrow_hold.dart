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
