import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';

/// ESC-15: the three-party commitment of a paid order, as the client reads it
/// (order-lifecycle.md 2.0.0, Commitments). The buyer's money is held first
/// (`funded`), then the FARMER is asked; only once the farmer accepted is the
/// TRUCKER asked. The order is `accepted` when both agreed (self-pickup: the
/// farmer alone). A failed transport parks it in `awaitingTransport`, where
/// the buyer cancels for a full refund. The server decides; these helpers only
/// pick what to show and which buttons to offer, mirroring the server's
/// `OrderCommitment.FarmerResponseOf` / `TruckerResponseOf`.
abstract final class CommitmentResponse {
  static const pending = 'pending';
  static const accepted = 'accepted';
  static const declined = 'declined';
  static const expired = 'expired';
  static const cancelled = 'cancelled';
  static const notAsked = 'not_asked';
  static const failedCapacity = 'failed_capacity';
  static const none = 'none';
}

/// The command values a farmer or trucker sends (sync-protocol.md 1.29.0).
abstract final class CommitmentCommand {
  static const accept = 'accept';
  static const decline = 'decline';
}

/// The decline reason codes the server accepts, in the order the sheet lists them.
const List<String> commitmentDeclineReasons = [
  'date',
  'capacity',
  'price',
  'route',
  'crop_not_ready',
  'other',
];

/// Whether the server runs the commitment rules for [order]. It stamps
/// `fulfillment` at funding only with `COMMITMENTS_ENABLED` on, and only then
/// writes `awaiting_transport` or a trucker answer; an order without any of
/// them follows the rules from before ESC-15 (the farmer's accept is final,
/// the buyer may cancel a funded order, no trucker command).
bool orderUsesCommitments(Order order) =>
    order.fulfillment != null ||
    order.status == OrderStatus.awaitingTransport ||
    (order.truckerResponse?.isNotEmpty ?? false);

/// A trucker order. The server binds `tripId` and `truckerId` together; an
/// older row may carry only the trucker.
bool orderHasTrip(Order order) =>
    (order.tripId?.isNotEmpty ?? false) ||
    (order.truckerId?.isNotEmpty ?? false);

/// The farmer's answer; a row the server never stamped counts as pending while
/// the farmer may still decide and as accepted from `accepted` on.
String farmerResponseOf(Order order) {
  final stored = order.farmerResponse;
  if (stored != null && stored.isNotEmpty) return stored;
  return _farmerDecides(order.status)
      ? CommitmentResponse.pending
      : CommitmentResponse.accepted;
}

/// The trucker's answer; an unstamped row derives it from the trip and status.
String truckerResponseOf(Order order) {
  final stored = order.truckerResponse;
  if (stored != null && stored.isNotEmpty) return stored;
  if (!orderHasTrip(order)) return CommitmentResponse.none;
  return _farmerDecides(order.status)
      ? CommitmentResponse.notAsked
      : CommitmentResponse.accepted;
}

bool _farmerDecides(OrderStatus status) =>
    status == OrderStatus.funded || status == OrderStatus.awaitingTransport;

/// Whether [userId] is this order's farmer and may accept or decline it now.
bool canFarmerRespond(Order order, String userId) =>
    order.status == OrderStatus.funded &&
    userId == order.farmerId &&
    farmerResponseOf(order) == CommitmentResponse.pending;

/// Whether [userId] is this order's trucker and may accept or decline the
/// transport request now: only after the farmer accepted (the sequential rule).
bool canTruckerRespond(Order order, String userId) =>
    order.status == OrderStatus.funded &&
    userId.isNotEmpty &&
    userId == order.truckerId &&
    farmerResponseOf(order) == CommitmentResponse.accepted &&
    truckerResponseOf(order) == CommitmentResponse.pending;

/// Whether [userId] is the committed trucker of an order not picked up yet and
/// may drop out (moves it to `awaitingTransport`; needs a fresh sign-in).
bool canTruckerDropOut(Order order, String userId) =>
    order.status == OrderStatus.accepted &&
    orderUsesCommitments(order) &&
    userId.isNotEmpty &&
    userId == order.truckerId &&
    truckerResponseOf(order) == CommitmentResponse.accepted;

/// The deadline of the answer [userId] owes on this order, if any: the
/// farmer's or the trucker's request, or the buyer's choice after a failed
/// transport.
DateTime? commitmentDeadlineFor(Order order, String userId) {
  if (canFarmerRespond(order, userId)) return order.farmerDeadlineAt;
  if (canTruckerRespond(order, userId)) return order.truckerDeadlineAt;
  if (order.status == OrderStatus.awaitingTransport &&
      userId == order.buyerId) {
    return order.buyerChoiceDeadlineAt;
  }
  return null;
}

/// The status a farmer's accept leaves [order] in, as the server decides it:
/// a self-pickup order is agreed; a trip order stays `funded` while the
/// trucker is asked, or waits for the buyer when its trip was already full or
/// cancelled.
OrderStatus farmerAcceptStatus(Order order) {
  if (!orderHasTrip(order) || !orderUsesCommitments(order)) {
    return OrderStatus.accepted;
  }
  final trucker = truckerResponseOf(order);
  return trucker == CommitmentResponse.failedCapacity ||
          trucker == CommitmentResponse.cancelled
      ? OrderStatus.awaitingTransport
      : OrderStatus.funded;
}

/// Statuses the buyer may cancel from: before the money is held, and after
/// the transport fell through (ESC-15). A `funded` order may not be
/// cancelled while the farmer and the trucker decide (spec D1).
const Set<OrderStatus> buyerCancellableOrderStatuses = {
  OrderStatus.created,
  OrderStatus.pendingPayment,
  OrderStatus.paymentFailed,
  OrderStatus.awaitingTransport,
};

/// Whether [userId] may cancel [order] now (the client's mirror of the
/// server's cancel rules, order-lifecycle.md 2.0.0):
/// - the buyer: [buyerCancellableOrderStatuses], an accepted order after its
///   pickup deadline (ESC-06), and a funded order only on a server without
///   commitments ([orderUsesCommitments]);
/// - the farmer: after the transport fell through (no penalty), or an
///   accepted order before pickup on a server with commitments (counts
///   against the farmer; the server asks for a fresh sign-in).
bool canCancelOrder(Order order, String userId, DateTime nowUtc) {
  if (userId.isEmpty) return false;
  if (userId == order.buyerId) {
    return buyerCancellableOrderStatuses.contains(order.status) ||
        (order.status == OrderStatus.funded && !orderUsesCommitments(order)) ||
        isTransportFailureCancelOpen(order, nowUtc);
  }
  if (userId == order.farmerId) {
    return order.status == OrderStatus.awaitingTransport ||
        (order.status == OrderStatus.accepted && orderUsesCommitments(order));
  }
  return false;
}

/// Throws [OrderCancelNotAllowedException] unless [canCancelOrder].
void checkOrderCancelAllowed(Order order, String userId, DateTime nowUtc) {
  if (canCancelOrder(order, userId, nowUtc)) return;
  throw OrderCancelNotAllowedException(
    order.status == OrderStatus.funded
        ? 'The farmer and the trucker are deciding; the order cannot be '
            'cancelled now.'
        : 'The order cannot be cancelled in status ${order.status.name}.',
  );
}

/// Reads the commitment fields of a server order map (`GET /orders`, `/sync`
/// rows) into [order]. Every field is optional: an older server sends none.
void applyOrderCommitmentFields(Order order, Map<String, dynamic> m) {
  order
    ..fulfillment = _string(m['fulfillment'])
    ..farmerResponse = _string(m['farmerResponse'])
    ..truckerResponse = _string(m['truckerResponse'])
    ..farmerDeadlineAt = _utc(m['farmerDeadlineAt'])
    ..truckerDeadlineAt = _utc(m['truckerDeadlineAt'])
    ..buyerChoiceDeadlineAt = _utc(m['buyerChoiceDeadlineAt'])
    ..transportFailure = _string(m['transportFailure'])
    ..declineReason = _string(m['declineReason'])
    ..cancelledBy = _string(m['cancelledBy']);
}

String? _string(Object? value) =>
    value is String && value.isNotEmpty ? value : null;

DateTime? _utc(Object? value) =>
    value is String ? DateTime.tryParse(value)?.toUtc() : null;
