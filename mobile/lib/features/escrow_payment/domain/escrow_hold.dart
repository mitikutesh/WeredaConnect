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
  OrderStatus.awaitingTransport,
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

/// The escrow of [orderId], or null when there is none or the read fails.
/// Reads the server when it can (mobile falls back to its cached copy
/// offline). The order detail reads it once for the hold and for the payee's
/// payout date (FUP-113).
Future<Payment?> readOrderEscrow(
  BaseEscrowRepository repository,
  String orderId,
) async {
  try {
    return await repository.refreshEscrow(orderEscrowId(orderId));
  } on Object {
    return null;
  }
}

/// Whether the escrow of [orderId] is `held`, read as [readOrderEscrow]
/// reads it. A failure reads as not held: the server still refuses what a
/// held escrow does not allow.
Future<bool> isOrderEscrowHeld(
  BaseEscrowRepository repository,
  String orderId,
) async =>
    (await readOrderEscrow(repository, orderId))?.status == PaymentStatus.held;

/// Rows per page of the held read (the server's most).
const heldHistoryPageLimit = 50;

/// The most pages the held read asks for: the party's 200 newest escrows.
/// A party with fewer than 50 still makes one call (FUP-79); at exactly 50
/// the server still hands a token, so a second call answers empty.
const heldHistoryMaxPages = 4;

/// The most cached held rows the phone reads again by id after the held
/// pages, per load (FUP-79): the list waits for them (the farmer's Accept is
/// enabled once the held read ends), so the rest wait for the next load.
const heldRereadMax = 5;

/// One page of `GET /escrow/history`: its rows and the token of the next page
/// (null on the last one).
typedef EscrowHistoryPage = ({List<Payment> rows, String? nextToken});

/// Reads `GET /escrow/history?status=held` page after page through [page]
/// (FUP-79). The server applies `limit` to the index before the status
/// filter, so one page holds only the held rows among the next 50 escrows,
/// and a held escrow further back comes on a later page. Stops on the last
/// page or after [heldHistoryMaxPages]; an error propagates.
Future<List<Payment>> readHeldPages(
  Future<EscrowHistoryPage> Function(String? nextToken) page,
) async {
  final rows = <Payment>[];
  String? nextToken;
  for (var read = 0; read < heldHistoryMaxPages; read++) {
    final answer = await page(nextToken);
    rows.addAll(answer.rows);
    nextToken = answer.nextToken;
    if (nextToken == null) break;
  }
  return rows;
}

/// The orders whose escrow is `held`, read with
/// [BaseEscrowRepository.refreshHeldEscrows] (the party's 200 newest escrows,
/// see [readHeldPages]). [role] is the caller's `UserRole.name`. Mobile falls
/// back to its cache offline; a failure reads as none held.
Future<Set<String>> heldOrderIds(
  BaseEscrowRepository repository, {
  required String userId,
  required String role,
}) async {
  try {
    final payments = await repository.refreshHeldEscrows(
      userId: userId,
      role: role,
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
