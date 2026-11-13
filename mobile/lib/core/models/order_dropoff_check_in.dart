import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';

/// ESC-36, the client part (FUP-336): the trucker's drop-off check-in and the
/// drop-off judgement, as the client reads them (`delivery-verification-api.md`
/// 2.10.0 P.4 "As built", `order-lifecycle.md` 2.18.0, `sync-protocol.md`
/// 1.59.0). Only an order the server stamped `dropoffJudgement: pending` when
/// it left `accepted` after the pickup takes the check-in: the trucker's
/// "Arrived at drop-off", with the arrival photo attached first and a GPS fix.
/// The server judges by the time it received the check-in: T8 without one,
/// T9 48 hours after one the buyer neither confirmed nor disputed. These
/// helpers only decide what to show and whether to offer the button.

/// The values of [Order.dropoffJudgement].
abstract final class DropoffJudgementOutcome {
  /// Picked up with the flag on: judged at T8, or at T9 once the trucker
  /// checked in.
  static const pending = 'pending';

  /// A party confirmed or disputed the delivery first: nothing to judge.
  static const closed = 'closed';

  /// T9: the system confirmed the delivery for a buyer who said nothing; the
  /// normal dispute window runs.
  static const autoConfirmed = 'auto_confirmed';

  /// T9: the buyer said nothing and the delivery could not be confirmed for
  /// them: support decides, money held.
  static const silentBuyer = 'silent_buyer';

  /// T8: no drop-off check-in by the deadline: support decides, money held.
  static const deliveryOverdue = 'delivery_overdue';

  /// T8 after the trucker tried to check in more than 5 km from the drop-off:
  /// support decides, money held.
  static const dropoffOutOfRange = 'dropoff_out_of_range';

  /// The money could be neither confirmed nor frozen: the admin queue.
  static const queued = 'queued';
}

/// T9 comes this long after the server received the trucker's drop-off
/// check-in (the SSM default `silent-buyer-hours`; the server never goes
/// below 24 hours). Only the fallback of [silentBuyerConfirmsAt] when the
/// order carries no server T9 (FUP-370). The client only shows the time; the
/// server decides.
const Duration silentBuyerWindow = Duration(hours: 48);

/// The order statuses of goods on the road (the server's
/// `OnTheRoadOrderStatuses`): the only ones that take the check-in.
const Set<OrderStatus> dropoffCheckInOrderStatuses = {
  OrderStatus.pickupConfirmed,
  OrderStatus.inTransit,
};

/// Whether the server stamped [order] for the drop-off judgement (any value).
bool isDropoffJudgementStamped(Order order) =>
    order.dropoffJudgement?.isNotEmpty ?? false;

/// Whether [order] waits for its drop-off judgement.
bool isDropoffJudgementPending(Order order) =>
    order.dropoffJudgement == DropoffJudgementOutcome.pending;

/// Whether [userId] gives the drop-off check-in on [order]: its trucker, on a
/// trucked order (a self-pickup has none).
bool isDropoffCheckInParty(Order order, String userId) =>
    userId.isNotEmpty && userId == order.truckerId && !isSelfPickupOrder(order);

/// The server's drop-off check-in on [order], or null.
OrderCheckIn? dropoffCheckInOf(Order order) =>
    checkInOf(order, CheckInKind.arrivedDropoff);

/// The drop-off check-in when it counts: received by T8 and not late (the
/// server's `CountedCheckIn`). Only that one starts the buyer's 48 hours.
OrderCheckIn? countedDropoffCheckIn(Order order) {
  final checkIn = dropoffCheckInOf(order);
  final received = checkIn?.receivedAt;
  final deadline = order.dropoffDeadlineAt;
  if (checkIn == null ||
      checkIn.isLate ||
      received == null ||
      deadline == null ||
      received.isAfter(deadline)) {
    return null;
  }
  return checkIn;
}

/// When the delivery may be confirmed for a buyer who says nothing (T9),
/// while the order is still on the road and waits for its judgement: the
/// server's own T9 ([Order.dropoffConfirmAt], FUP-370, which follows a
/// changed `silent-buyer-hours`), else the counted check-in's receipt plus
/// [silentBuyerWindow] (a server before it). Null otherwise: a delivery a
/// party confirmed or disputed keeps `pending` until the cron follows it at
/// its deadline, and nothing is confirmed for anyone then.
DateTime? silentBuyerConfirmsAt(Order order) {
  if (!isDropoffJudgementPending(order) ||
      !dropoffCheckInOrderStatuses.contains(order.status)) {
    return null;
  }
  return order.dropoffConfirmAt ??
      countedDropoffCheckIn(order)?.receivedAt?.add(silentBuyerWindow);
}

/// [CheckInChance] of [userId]'s drop-off check-in on [order] at [nowUtc]
/// (the device clock; the server decides with its own). There is no
/// [CheckInChance.notYet]: the window opens with the pickup.
CheckInChance dropoffCheckInChanceFor(
  Order order,
  String userId,
  DateTime nowUtc,
) {
  if (!isDropoffCheckInParty(order, userId) ||
      !dropoffCheckInOrderStatuses.contains(order.status) ||
      !isDropoffJudgementPending(order)) {
    return CheckInChance.none;
  }
  if (dropoffCheckInOf(order) != null ||
      order.queuedCheckIns.containsKey(CheckInKind.arrivedDropoff)) {
    return CheckInChance.given;
  }
  final deadline = order.dropoffDeadlineAt;
  if (deadline == null || nowUtc.isAfter(deadline)) return CheckInChance.closed;
  return CheckInChance.open;
}

/// The repositories' guard before a drop-off check-in is queued or sent.
/// Returns false when [userId] already gave it (the server keeps the first
/// one, so nothing is sent again); throws [CheckInNotAllowedException], with
/// the drop-off text the server would give for the same reason, when it is
/// not theirs to give, the order takes none, the deadline passed on the
/// device clock, or there is no usable fix.
bool checkDropoffCheckInAllowed(
  Order order,
  String userId,
  HandoffCheckInFix? fix,
  DateTime nowUtc,
) {
  if (!isDropoffCheckInParty(order, userId)) {
    throw const CheckInNotAllowedException(
      'Only the order\'s trucker checks in at the drop-off.',
      AppErrorCode.dropoffCheckInNotParty,
    );
  }
  switch (dropoffCheckInChanceFor(order, userId, nowUtc)) {
    case CheckInChance.given:
      return false;
    case CheckInChance.open:
      break;
    case CheckInChance.closed:
      throw const CheckInNotAllowedException(
        'The delivery deadline has passed.',
        AppErrorCode.dropoffCheckInClosed,
      );
    case CheckInChance.notYet:
    case CheckInChance.none:
      throw const CheckInNotAllowedException(
        'Drop-off check-ins are not open for this order.',
        AppErrorCode.dropoffCheckInNotOpen,
      );
  }
  if (!isUsableCheckInFix(fix)) {
    throw const CheckInNotAllowedException(
      'A drop-off check-in needs a GPS fix.',
      AppErrorCode.dropoffCheckInNeedsLocation,
    );
  }
  return true;
}
