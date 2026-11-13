import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/order_delivery_location.dart';

/// Abstract base for order repositories.
///
/// Extracted from OrderRepository so both mobile (Isar) and
/// web (HTTP-only) implementations can share the same BLoC interface.
abstract class BaseOrderRepository {
  Future<List<Order>> getOrdersByFarmer(
    String farmerId, {
    String? statusFilter,
  });

  Future<List<Order>> getOrdersByBuyer(
    String buyerId, {
    String? statusFilter,
  });

  Future<List<Order>> getOrdersByUser(
    String userId, {
    String? role,
    String? statusFilter,
  });

  /// FUP-327, FUP-371: [orders] with [userId]'s check-ins made on this
  /// phone that the server has not refused ([Order.queuedCheckIns]), as
  /// [getOrderById] gives them, read in one go and without the network
  /// (Home's "Action needed" cards). Web sends a check-in at once and keeps
  /// no queue: the orders as they are.
  Future<List<Order>> ordersWithQueuedCheckIns(
    List<Order> orders,
    String userId,
  ) async =>
      orders;

  Future<Order?> getOrderById(String orderId);

  /// [dropoffNote] is the buyer's optional drop-off landmark (LT-12): sent
  /// trimmed, left out when empty. [deliveryPoint] is the order's own
  /// drop-off point (LT-14), the place the buyer picked: sent when inside
  /// Ethiopia's box, else left out (the trip's destination is the drop-off).
  Future<Order> createOrder({
    required String listingId,
    required String buyerId,
    required String farmerId,
    required String buyerName,
    required String farmerName,
    required String cropType,
    required double quantity,
    required double pricePerUnit,
    String? tripId,
    String? truckerId,
    String? truckerName,
    double? transportCost,
    double? commissionRate,
    String? dropoffNote,
    DeliveryPoint? deliveryPoint,
  });

  /// LT-14: the buyer changes the drop-off point of a trucked order before
  /// pickup (`canEditDeliveryLocation`) to [point], a picked place's point.
  /// Throws `OrderActionNotAllowedException` for anyone else, after pickup,
  /// or for a point outside Ethiopia's box; the same point again is a no-op.
  Future<void> updateDeliveryLocation(
    String orderId,
    String buyerId,
    DeliveryPoint point,
  );

  /// LT-12: the buyer adds, changes or (with null or blank) clears the
  /// drop-off landmark of a trucked order before pickup
  /// (`canEditDropoffNote`). Throws `OrderActionNotAllowedException` for
  /// anyone else or after pickup; the same note again is a no-op.
  Future<void> updateDropoffNote(
    String orderId,
    String buyerId,
    String? dropoffNote,
  );

  /// The buyer's `payment_submitted`. [cbeTransactionId] is the demo
  /// transfer reference; live Telebirr payments send none (ESC-44), they are
  /// matched by the server's checkout. A retry after `payment_failed` is the
  /// same single change (order-lifecycle 1.13.0).
  Future<void> submitPayment(
    String orderId,
    String? cbeTransactionId, {
    String paymentMethod = 'cbe_transfer',
  });

  Future<void> cancelOrder(
    String orderId,
    String userId, {
    String? reason,
  });

  /// The farmer's yes to a funded order (`farmerResponse: accept`). A
  /// self-pickup order is agreed at once; a trip order stays `funded` while
  /// its trucker is asked (ESC-15, sequential).
  Future<void> acceptOrder(String orderId, String farmerId);

  /// The farmer's no (`farmerResponse: decline`): the buyer is refunded.
  /// [declineReason] is one of `farmerDeclineReasons`.
  Future<void> rejectOrder(
    String orderId,
    String farmerId, {
    String? reason,
    String? declineReason,
  });

  /// ESC-15: the trucker's answer to a transport request
  /// (`truckerResponse: accept | decline`), only once the farmer accepted. A
  /// decline on an `accepted` order before pickup is the trucker's drop-out
  /// (the server asks for a fresh sign-in). [declineReason] is one of
  /// `commitmentDeclineReasons`.
  Future<void> respondAsTrucker(
    String orderId,
    String truckerId, {
    required bool accept,
    String? declineReason,
  });

  /// ESC-34 (FUP-285): [userId]'s arrival check-in of [kind] on an accepted
  /// order stamped for the pickup judgement: the trucker's (on a self-pickup
  /// order the buyer's) `arrived_pickup` with [fix], which it needs, or the
  /// farmer's `crops_ready`, whose [fix] is optional and left out when the
  /// server would not take it. Queued on the phone, sent at once on web,
  /// where a refusal throws `SyncRejectedException` with its `CHECKIN_*`
  /// code. Throws `CheckInNotAllowedException` when the check-in is not this
  /// user's to give, not open, or an arrival has no usable fix
  /// ([checkCheckInAllowed]). Returns true when it was queued or sent, false
  /// when this user had already given it (nothing is sent again).
  Future<bool> checkIn(
    String orderId,
    String userId, {
    required String kind,
    HandoffCheckInFix? fix,
  });

  /// ESC-36 (FUP-336): the trucker's drop-off check-in (`arrived_dropoff`) on
  /// an order on the road stamped for the drop-off judgement, with [fix],
  /// which it needs. The server refuses it until the trucker's arrival photo
  /// of [deliveryId] is attached, so a new [arrivalPhotoRef] (this phone's
  /// file, or on web the key the browser just uploaded) goes first: queued
  /// ahead of the check-in on the phone (the check-in waits for it), attached
  /// before it is sent on web. Null when the delivery has its photo already.
  /// A refusal on web throws `SyncRejectedException` with its `CHECKIN_*`
  /// code. Throws `CheckInNotAllowedException` when the check-in is not this
  /// user's to give, not open, or has no usable fix
  /// (`checkDropoffCheckInAllowed`). Returns true when it was queued or sent,
  /// false when this user had already given it (nothing is sent again).
  Future<bool> checkInAtDropoff(
    String orderId,
    String userId, {
    required String deliveryId,
    required HandoffCheckInFix? fix,
    String? arrivalPhotoRef,
  });
}
