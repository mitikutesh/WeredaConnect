import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/sync/handoff_check_in_rules.dart';

/// ESC-34, the client part (FUP-285): the arrival check-ins of an accepted
/// order and its pickup judgement, as the client reads them
/// (`delivery-verification-api.md` 2.6.0 P.2, `order-lifecycle.md` 2.15.0).
/// Only an order the server stamped `pickupJudgement: pending` at acceptance
/// takes check-ins: the trucker's "I have arrived" (the buyer's on a
/// self-pickup order) and the farmer's "Crops are ready". The server judges
/// the pickup at the deadline by the time it received each check-in; these
/// helpers only decide what to show and which button to offer.

/// The check-in kinds.
abstract final class CheckInKind {
  static const arrivedPickup = 'arrived_pickup';
  static const cropsReady = 'crops_ready';

  /// ESC-36 (FUP-336): the trucker at the drop-off, with the arrival photo
  /// and a GPS fix (`order_dropoff_check_in.dart`).
  static const arrivedDropoff = dropoffCheckInKind;
}

/// The values of [Order.pickupJudgement].
abstract final class PickupJudgementOutcome {
  /// Accepted with the flag on: judged at the pickup deadline.
  static const pending = 'pending';

  /// The crops were picked up: the normal flow.
  static const scanned = 'scanned';

  /// Nobody checked in at the farm: the order waits for the buyer's choice.
  static const truckerNoShow = 'trucker_no_show';

  /// The farmer did not say the crops were ready: cancelled, full refund.
  static const farmerNoShow = 'farmer_no_show';

  /// The self-pickup buyer did not come: cancelled, refunded.
  static const buyerNoShow = 'buyer_no_show';

  /// Both checked in, nothing was scanned: support decides, money held.
  static const bothPresentNoScan = 'both_present_no_scan';

  /// The arrival could not be measured: support decides, money held.
  static const uncheckedCheckIn = 'unchecked_check_in';
}

/// The check-in window opens this long before `pickupDate` (spec 4.8).
const Duration checkInOpensBeforePickup = Duration(hours: 24);

/// The server's box for a check-in's fix (its `LocationPingRules`): a fix
/// outside it, or a rougher one, would be refused `CHECKIN_VALIDATION`.
const double checkInMinLatitude = 3;
const double checkInMaxLatitude = 15;
const double checkInMinLongitude = 32.5;
const double checkInMaxLongitude = 48.5;
const double checkInMaxAccuracyM = 5000;

/// Whether the server stamped [order] for the pickup judgement (any value).
/// Such an order is judged from the check-ins: the buyer's cancel after the
/// pickup deadline is not offered (the server refuses it).
bool isPickupJudgementStamped(Order order) =>
    order.pickupJudgement?.isNotEmpty ?? false;

/// Whether [order] waits for its pickup judgement.
bool isPickupJudgementPending(Order order) =>
    order.pickupJudgement == PickupJudgementOutcome.pending;

/// The server's self-pickup rule: `fulfillment: self_pickup`, or a row
/// without `fulfillment` that names no trip and no trucker.
bool isSelfPickupOrder(Order order) {
  final fulfillment = order.fulfillment ?? '';
  return fulfillment == 'self_pickup' ||
      (fulfillment.isEmpty &&
          (order.tripId?.isEmpty ?? true) &&
          (order.truckerId?.isEmpty ?? true));
}

/// The check-in kind [userId] gives on [order], or null when they give none:
/// the farmer says the crops are ready; the trucker (the buyer on a
/// self-pickup order) says they have arrived.
String? checkInKindFor(Order order, String userId) {
  if (userId.isEmpty) return null;
  if (userId == order.farmerId) return CheckInKind.cropsReady;
  final arriving = isSelfPickupOrder(order) ? order.buyerId : order.truckerId;
  return userId == arriving ? CheckInKind.arrivedPickup : null;
}

/// The server's check-in of [kind] on [order], or null.
OrderCheckIn? checkInOf(Order order, String kind) {
  for (final checkIn in order.checkIns) {
    if (checkIn.kind == kind) return checkIn;
  }
  return null;
}

/// When check-ins open: `pickupDate` minus 24 hours. A self-pickup order has
/// no `pickupDate` and is open from its acceptance (null).
DateTime? checkInOpensAt(Order order) =>
    order.pickupDate?.subtract(checkInOpensBeforePickup);

/// Where the viewer's own check-in stands on [order] now.
enum CheckInChance {
  /// Not this viewer's to give, or the order takes none.
  none,

  /// Given already: the server has it, or it waits on this phone.
  given,

  /// The window has not opened yet ([checkInOpensAt]).
  notYet,

  /// The button is offered.
  open,

  /// The pickup deadline passed.
  closed,
}

/// [CheckInChance] of [userId] on [order] at [nowUtc] (the device clock; the
/// server decides with its own).
CheckInChance checkInChanceFor(Order order, String userId, DateTime nowUtc) {
  final kind = checkInKindFor(order, userId);
  if (kind == null ||
      order.status != OrderStatus.accepted ||
      !isPickupJudgementPending(order)) {
    return CheckInChance.none;
  }
  if (checkInOf(order, kind) != null ||
      order.queuedCheckIns.containsKey(kind)) {
    return CheckInChance.given;
  }
  final deadline = order.pickupDeadlineAt;
  if (deadline == null || nowUtc.isAfter(deadline)) return CheckInChance.closed;
  final opens = checkInOpensAt(order);
  if (opens != null && nowUtc.isBefore(opens)) return CheckInChance.notYet;
  return CheckInChance.open;
}

/// The repositories' guard before a check-in is queued or sent. Returns
/// false when [userId] already gave [kind] (the server keeps the first one,
/// so nothing is sent again); throws [CheckInNotAllowedException], with the
/// check-in text the server would give for the same reason, when it is not
/// theirs to give, the order takes none, the window is shut on the device
/// clock, or an arrival has no usable fix.
bool checkCheckInAllowed(
  Order order,
  String userId,
  String kind,
  HandoffCheckInFix? fix,
  DateTime nowUtc,
) {
  if (checkInKindFor(order, userId) != kind) {
    throw const CheckInNotAllowedException(
      'Only the farmer says the crops are ready, and only the trucker (or '
      'the buyer collecting the order) says they have arrived.',
      AppErrorCode.checkInNotParty,
    );
  }
  switch (checkInChanceFor(order, userId, nowUtc)) {
    case CheckInChance.given:
      return false;
    case CheckInChance.open:
      break;
    case CheckInChance.notYet:
      throw const CheckInNotAllowedException(
        'Check-ins open 24 hours before the pickup day.',
        AppErrorCode.checkInTooEarly,
      );
    case CheckInChance.closed:
      throw const CheckInNotAllowedException(
        'The pickup deadline has passed.',
        AppErrorCode.checkInClosed,
      );
    case CheckInChance.none:
      throw const CheckInNotAllowedException(
        'Check-ins are not open for this order.',
        AppErrorCode.checkInNotOpen,
      );
  }
  if (kind == CheckInKind.arrivedPickup && !isUsableCheckInFix(fix)) {
    throw const CheckInNotAllowedException(
      'An arrival needs a GPS fix at the farm.',
      AppErrorCode.checkInNeedsLocation,
    );
  }
  return true;
}

/// Whether [order] shows a check-in made on this phone that the server's
/// copy does not hold yet: waiting to be sent, or sent and not read back.
/// The order screen reads the order again after each sync while one does.
bool hasCheckInAwaitingServer(Order order) =>
    order.queuedCheckIns.keys.any((kind) => checkInOf(order, kind) == null);

/// One GPS fix taken for a check-in.
class HandoffCheckInFix {
  const HandoffCheckInFix({
    required this.latitude,
    required this.longitude,
    this.accuracyM,
  });

  final double latitude;
  final double longitude;

  /// Horizontal accuracy in metres, when the platform gave one.
  final double? accuracyM;
}

/// Whether the server takes [fix]: inside its box and no rougher than
/// [checkInMaxAccuracyM]. The arrival needs one; the farmer's is optional
/// and an unusable one is simply not sent, so "Crops are ready" never fails
/// on a bad fix.
bool isUsableCheckInFix(HandoffCheckInFix? fix) {
  if (fix == null) return false;
  final accuracy = fix.accuracyM;
  return fix.latitude.isFinite &&
      fix.longitude.isFinite &&
      fix.latitude >= checkInMinLatitude &&
      fix.latitude <= checkInMaxLatitude &&
      fix.longitude >= checkInMinLongitude &&
      fix.longitude <= checkInMaxLongitude &&
      (accuracy == null ||
          (accuracy.isFinite &&
              accuracy >= 0 &&
              accuracy <= checkInMaxAccuracyM));
}

/// The `data` of a `handoff_check_in` operation: exactly the fields the
/// server takes (it refuses any other), the fix only when usable.
Map<String, Object> handoffCheckInData({
  required String orderId,
  required String kind,
  required DateTime capturedAt,
  HandoffCheckInFix? fix,
}) =>
    {
      'orderId': orderId,
      'kind': kind,
      'capturedAt': capturedAt.toUtc().toIso8601String(),
      if (fix != null && isUsableCheckInFix(fix)) ...{
        'latitude': fix.latitude,
        'longitude': fix.longitude,
        if (fix.accuracyM != null) 'accuracyM': fix.accuracyM!,
      },
    };

/// Reads the pickup judgement, the drop-off judgement (ESC-36,
/// order-lifecycle 2.18.0, FUP-336) and the check-ins of a server order map
/// (`GET /orders`, `GET /orders/{id}`, `/sync` rows) into [order]. An order
/// accepted or picked up without the flags carries none, which clears the
/// device's copy.
void applyOrderCheckInFields(Order order, Map<String, dynamic> m) {
  order
    ..pickupJudgement = _text(m['pickupJudgement'])
    ..pickupJudgedAt = _utc(m['pickupJudgedAt'])
    ..checkIns = parseOrderCheckIns(m['checkIns'])
    ..dropoffJudgement = _text(m['dropoffJudgement'])
    ..dropoffJudgedAt = _utc(m['dropoffJudgedAt'])
    ..dropoffDeadlineAt = _utc(m['dropoffDeadlineAt'])
    ..disputeReason = _text(m['disputeReason']);
}

String? _text(Object? value) =>
    value is String && value.isNotEmpty ? value : null;

/// The check-ins of a server `checkIns` map (`{kind: {receivedAt,
/// capturedAt?, distanceChecked, late}}`), in kind order; an entry without a
/// readable `receivedAt` is left out.
List<OrderCheckIn> parseOrderCheckIns(Object? raw) {
  if (raw is! Map) return [];
  final checkIns = <OrderCheckIn>[];
  for (final entry in raw.entries) {
    final kind = entry.key;
    final value = entry.value;
    if (kind is! String || kind.isEmpty || value is! Map) continue;
    final receivedAt = _utc(value['receivedAt']);
    if (receivedAt == null) continue;
    checkIns.add(
      OrderCheckIn()
        ..kind = kind
        ..receivedAt = receivedAt
        ..capturedAt = _utc(value['capturedAt'])
        ..distanceChecked = value['distanceChecked'] == true
        ..isLate = value['late'] == true,
    );
  }
  checkIns.sort((a, b) => a.kind.compareTo(b.kind));
  return checkIns;
}

DateTime? _utc(Object? value) =>
    value is String ? DateTime.tryParse(value)?.toUtc() : null;
