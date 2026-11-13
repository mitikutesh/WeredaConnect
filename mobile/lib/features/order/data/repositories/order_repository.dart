import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_delivery_location.dart';
import 'package:wereda_connect/core/models/order_dropoff_check_in.dart';
import 'package:wereda_connect/core/models/order_dropoff_note.dart';
import 'package:wereda_connect/core/models/order_pricing.dart';
import 'package:wereda_connect/core/models/order_status_wire.dart';
import 'package:wereda_connect/core/models/sync_operation.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/delivery_photo_rules.dart';
import 'package:wereda_connect/core/sync/handoff_check_in_rules.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';

/// Offline-first repository for order CRUD operations.
///
/// All writes go to local Isar first, then enqueue a sync operation.
/// Reads are always from Isar (fast, works offline).
///
/// The update actions refuse instead of returning quietly: a missing order
/// throws [NotFoundException], the wrong party or status throws
/// [OrderActionNotAllowedException], and a failed enqueue puts the local
/// order back and rethrows. Repeating an action the order already shows
/// (accept on `accepted`, ...) is a no-op success.
class OrderRepository extends BaseOrderRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;

  /// The signed-in user, whose own queued check-ins [getOrderById] shows
  /// (FUP-285). The app passes it; without it every user's show.
  final Future<String?> Function()? _currentUserId;

  /// Deletes a photo file this phone took (FUP-373: an arrival photo taken
  /// while another one already waits to upload); tests pass a fake.
  final Future<void> Function(String path)? _deletePhotoFile;
  static const _uuid = Uuid();

  OrderRepository({
    required Isar isar,
    required SyncQueue syncQueue,
    Future<String?> Function()? currentUserId,
    Future<void> Function(String path)? deletePhotoFile,
  })  : _isar = isar,
        _syncQueue = syncQueue,
        _currentUserId = currentUserId,
        _deletePhotoFile = deletePhotoFile;

  /// Get orders for a farmer, optionally filtered by status.
  @override
  Future<List<Order>> getOrdersByFarmer(
    String farmerId, {
    String? statusFilter,
  }) async {
    var query = _isar.orders.filter().farmerIdEqualTo(farmerId);

    if (statusFilter != null) {
      final status = OrderStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => OrderStatus.unknown,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Get orders placed by a buyer, optionally filtered by status.
  @override
  Future<List<Order>> getOrdersByBuyer(
    String buyerId, {
    String? statusFilter,
  }) async {
    var query = _isar.orders.filter().buyerIdEqualTo(buyerId);

    if (statusFilter != null) {
      final status = OrderStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => OrderStatus.unknown,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Get all orders for a user (as farmer or buyer).
  @override
  Future<List<Order>> getOrdersByUser(
    String userId, {
    String? role,
    String? statusFilter,
  }) async {
    if (role == 'farmer') {
      return getOrdersByFarmer(userId, statusFilter: statusFilter);
    } else if (role == 'buyer') {
      return getOrdersByBuyer(userId, statusFilter: statusFilter);
    } else if (role == 'trucker') {
      // Truckers see orders where they are the assigned transporter. Without this
      // branch a trucker fell through to the farmer/buyer-only query and saw none of
      // their assigned orders (the web repo already handles this — keep them in sync).
      return _getOrdersByTrucker(userId, statusFilter: statusFilter);
    }

    // All roles: single query using group/or
    var query = _isar.orders.filter().group(
          (q) => q.farmerIdEqualTo(userId).or().buyerIdEqualTo(userId),
        );

    if (statusFilter != null) {
      final status = OrderStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => OrderStatus.unknown,
      );
      query = query.statusEqualTo(status);
    }

    return query.sortByCreatedAtDesc().findAll();
  }

  /// Get orders where the user is the assigned trucker, optionally by status.
  /// Reads through the `truckerId` index (ESC-22), not a scan of every order.
  Future<List<Order>> _getOrdersByTrucker(
    String truckerId, {
    String? statusFilter,
  }) async {
    final query = _isar.orders.where().truckerIdEqualTo(truckerId);

    if (statusFilter == null) {
      return query.sortByCreatedAtDesc().findAll();
    }

    final status = OrderStatus.values.firstWhere(
      (s) => s.name == statusFilter,
      orElse: () => OrderStatus.unknown,
    );
    return query.filter().statusEqualTo(status).sortByCreatedAtDesc().findAll();
  }

  /// Get a single order by its orderId, with the signed-in user's check-ins
  /// made on this phone that the server has not refused
  /// ([Order.queuedCheckIns], FUP-285).
  @override
  Future<Order?> getOrderById(String orderId) async {
    final order =
        await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();
    if (order == null) return null;
    final currentUserId = _currentUserId;
    if (currentUserId == null) return _withQueuedCheckIns(order);
    final userId = await currentUserId();
    // Signed out: nobody's marks.
    if (userId == null || userId.isEmpty) return order;
    return _withQueuedCheckIns(order, userId: userId);
  }

  /// [order] with its [Order.queuedCheckIns]: per kind the first check-in of
  /// the queue the server has not refused (the server keeps the first one
  /// too), sent once any of them was applied. A refused one is left out, so
  /// the local mark goes back as soon as the queue rejects it. With
  /// [userId], only that user's: on a shared phone another user's check-in
  /// neither shows nor holds back this user's button.
  Future<Order> _withQueuedCheckIns(Order order, {String? userId}) async {
    final ops = await _syncQueue.unrejectedOperationsFor(
      handoffCheckInEntityType,
      entityIdPrefix: handoffCheckInEntityId(order.orderId, ''),
      userId: userId,
    );
    if (ops.isEmpty) return order;
    return order..queuedCheckIns = _queuedCheckInsOf(ops);
  }

  /// FUP-327, FUP-371: the marks of [_withQueuedCheckIns] for many orders,
  /// from one read of the queue, only [userId]'s (none when signed out).
  @override
  Future<List<Order>> ordersWithQueuedCheckIns(
    List<Order> orders,
    String userId,
  ) async {
    if (orders.isEmpty || userId.isEmpty) return orders;
    final ops = await _syncQueue.unrejectedOperationsFor(
      handoffCheckInEntityType,
      entityIdPrefix: '',
      userId: userId,
    );
    if (ops.isEmpty) return orders;
    final byOrder = <String, List<SyncOperation>>{};
    for (final op in ops) {
      final orderId = parseHandoffCheckInEntityId(op.entityId)?.orderId;
      if (orderId != null) (byOrder[orderId] ??= []).add(op);
    }
    for (final order in orders) {
      final own = byOrder[order.orderId];
      if (own != null) order.queuedCheckIns = _queuedCheckInsOf(own);
    }
    return orders;
  }

  /// The marks of the check-in operations [ops] (oldest first): per kind
  /// the first one, sent once any of them was applied.
  static Map<String, QueuedCheckIn> _queuedCheckInsOf(
    List<SyncOperation> ops,
  ) {
    final queued = <String, QueuedCheckIn>{};
    for (final op in ops) {
      final kind = parseHandoffCheckInEntityId(op.entityId)?.kind;
      if (kind == null) continue;
      final sent = op.status == SyncStatus.synced;
      final first = queued[kind];
      queued[kind] = QueuedCheckIn(
        madeAt: first?.madeAt ?? op.createdAt.toUtc(),
        sent: (first?.sent ?? false) || sent,
      );
    }
    return queued;
  }

  /// Create a new order (buyer action).
  @override
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
  }) async {
    final orderId = _uuid.v4();
    final now = DateTime.now().toUtc();
    final note = normalizeDropoffNote(dropoffNote);
    final point = _boxed(deliveryPoint);
    final totalPrice = quantity * pricePerUnit;
    final effectiveCommissionRate = commissionRate ?? 0.05;
    // SIM-30: the server's money, each leg to the santim, so the local copy
    // shows what the server stores until the sync brings its row back.
    final pricing = OrderPricing(
      cropCost: totalPrice,
      transportCost: transportCost ?? 0,
      commissionRate: effectiveCommissionRate,
    );
    final cropCost = pricing.cropCostEtb;
    final commissionAmount = pricing.commissionAmount;
    final orderTotal = pricing.orderTotal;

    final order = Order()
      ..orderId = orderId
      ..listingId = listingId
      ..buyerId = buyerId
      ..farmerId = farmerId
      ..buyerName = buyerName
      ..farmerName = farmerName
      ..cropType = cropType
      ..quantity = quantity
      ..pricePerUnit = pricePerUnit
      ..totalPrice = totalPrice
      ..status = OrderStatus.created
      ..createdAt = now
      ..version = 1
      ..tripId = tripId
      ..truckerId = truckerId
      ..truckerName = truckerName
      ..cropCost = cropCost
      ..transportCost = transportCost == null ? null : pricing.transportCostEtb
      ..commissionRate = effectiveCommissionRate
      ..commissionAmount = commissionAmount
      ..orderTotal = orderTotal
      ..paymentMethod = 'cbe_transfer'
      ..dropoffNote = note
      ..deliveryLatitude = point?.latitude
      ..deliveryLongitude = point?.longitude
      ..updatedAt = now;

    await _isar.writeTxn(() async {
      await _isar.orders.put(order);
    });

    // The local Isar write above is authoritative (offline-first). Enqueue
    // failures (queue full / duplicate idempotency key) must NOT surface as a
    // save failure — the order is persisted; the sync layer reconciles later.
    try {
      await _syncQueue.enqueue(
        userId: buyerId,
        entityType: 'order',
        entityId: orderId,
        action: 'create',
        data: jsonEncode({
          'orderId': orderId,
          'listingId': listingId,
          'buyerId': buyerId,
          'farmerId': farmerId,
          'buyerName': buyerName,
          'farmerName': farmerName,
          'cropType': cropType,
          'quantity': quantity,
          'pricePerUnit': pricePerUnit,
          'totalPrice': totalPrice,
          'cropCost': cropCost,
          'transportCost': transportCost,
          'commissionRate': effectiveCommissionRate,
          'commissionAmount': commissionAmount,
          'orderTotal': orderTotal,
          'paymentMethod': 'cbe_transfer',
          if (tripId != null) 'tripId': tripId,
          if (truckerId != null) 'truckerId': truckerId,
          if (truckerName != null) 'truckerName': truckerName,
          if (note != null) 'dropoffNote': note,
          if (point != null) ...{
            'deliveryLatitude': point.latitude,
            'deliveryLongitude': point.longitude,
          },
          'status': orderStatusToWire(OrderStatus.created.name),
          'createdAt': now.toIso8601String(),
          'updatedAt': now.toIso8601String(),
          'version': 1,
        }),
        operationId: _uuid.v4(),
      );
    } on Object catch (e) {
      debugPrint(
        '[OrderRepository] order saved locally but sync enqueue failed: $e',
      );
    }

    return order;
  }

  /// Submit payment proof (buyer action).
  @override
  Future<void> submitPayment(
    String orderId,
    String? cbeTransactionId, {
    String paymentMethod = 'cbe_transfer',
  }) async {
    final order = await _findOrder(orderId);
    // Already submitted (and still waiting to sync): nothing to send again.
    if (order.status == OrderStatus.paymentSubmitted) return;

    final now = DateTime.now().toUtc();
    // Send the version the client HAD before this change: the server checks
    // `#v = :expectedVersion`, so sending the post-increment value would always
    // conflict and get reverted by server-wins (same off-by-one fixed for trips).
    final baseVersion = order.version;
    final statusBefore = order.status;

    await _writeThenEnqueue(
      order,
      (o) {
        o.cbeTransactionId = cbeTransactionId;
        o.paymentMethod = paymentMethod;
        o.status = OrderStatus.paymentSubmitted;
        o.updatedAt = now;
        o.version = baseVersion + 1;
      },
      () => _syncQueue.enqueue(
        userId: order.buyerId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          if (cbeTransactionId != null) 'cbeTransactionId': cbeTransactionId,
          'paymentMethod': paymentMethod,
          'status': orderStatusToWire(OrderStatus.paymentSubmitted.name),
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
          // Client-only (not sent): where a refused payment goes back to.
          statusBeforePaymentKey: statusBefore.name,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  /// The client-only key of a queued payment that records the order's
  /// status before it ([revertRejectedPaymentSubmission]). The sync executor
  /// never sends `_` keys.
  static const statusBeforePaymentKey = '_statusBefore';

  /// Undo an optimistic payment submission the server's KYC gate rejected
  /// (`ORDER_KYC_REQUIRED`), so the order does not stay stuck in
  /// `paymentSubmitted` and the buyer can pay again after verifying.
  ///
  /// The server never applied the update, so the order goes back to the
  /// status it had before [submitPayment] (with the version from then): the
  /// one the refused operation's [sentData] recorded (`payment_failed` for a
  /// retry, ESC-44), else, for operations queued before that was recorded,
  /// `accepted` once the farmer responded, else `created` (payment can be
  /// submitted straight after placing the order). No-op when the order moved
  /// on in the meantime (e.g. a later pull).
  Future<void> revertRejectedPaymentSubmission(
    String orderId, {
    String? sentData,
  }) async {
    final recorded = _statusBeforePayment(sentData);
    await _isar.writeTxn(() async {
      final order =
          await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();
      if (order == null || order.status != OrderStatus.paymentSubmitted) {
        return;
      }
      order
        ..status = recorded ??
            (order.respondedAt != null
                ? OrderStatus.accepted
                : OrderStatus.created)
        ..cbeTransactionId = null
        ..paymentMethod = null
        ..version = order.version > 1 ? order.version - 1 : order.version
        ..updatedAt = DateTime.now().toUtc();
      await _isar.orders.put(order);
    });
  }

  static OrderStatus? _statusBeforePayment(String? sentData) {
    if (sentData == null) return null;
    try {
      final data = jsonDecode(sentData);
      final name = data is Map ? data[statusBeforePaymentKey] : null;
      for (final status in OrderStatus.values) {
        if (status.name == name && status != OrderStatus.paymentSubmitted) {
          return status;
        }
      }
    } on FormatException {
      // Fall back to the older rule.
    }
    return null;
  }

  /// Cancel an order (buyer or farmer action).
  @override
  Future<void> cancelOrder(
    String orderId,
    String userId, {
    String? reason,
  }) async {
    final order = await _findOrder(orderId);
    if (order.buyerId != userId && order.farmerId != userId) {
      throw const OrderActionNotAllowedException(
        'Only the buyer or the farmer can cancel this order.',
      );
    }
    if (order.status == OrderStatus.cancelled) return;

    final now = DateTime.now().toUtc();
    checkOrderCancelAllowed(order, userId, now);

    final baseVersion = order.version;

    await _writeThenEnqueue(
      order,
      (o) {
        o.status = OrderStatus.cancelled;
        o.cancelledAt = now;
        o.cancelReason = reason;
        o.updatedAt = now;
        o.version = baseVersion + 1;
        _closeContact(o);
      },
      () => _syncQueue.enqueue(
        userId: userId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          'status': orderStatusToWire(OrderStatus.cancelled.name),
          'cancelledAt': now.toIso8601String(),
          if (reason != null) 'cancelReason': reason,
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  /// Accept an order (farmer action). ESC-15: sent as the farmer command; the
  /// local row shows the sequential result until the next pull (a server with
  /// commitments off accepts a trip order at once).
  @override
  Future<void> acceptOrder(String orderId, String farmerId) async {
    final order = await _findOrder(orderId);
    _checkFarmerResponse(order, farmerId);
    if (order.status == OrderStatus.accepted ||
        order.farmerResponse == CommitmentResponse.accepted) {
      return;
    }
    _checkFunded(order);

    final now = DateTime.now().toUtc();
    final baseVersion = order.version;
    final nextStatus = farmerAcceptStatus(order);

    await _writeThenEnqueue(
      order,
      (o) {
        o.status = nextStatus;
        o.farmerResponse = CommitmentResponse.accepted;
        if (nextStatus == OrderStatus.funded) {
          o.truckerResponse = CommitmentResponse.pending;
        }
        o.respondedAt = now;
        o.updatedAt = now;
        o.version = baseVersion + 1;
        // ESC-48: contact opens at the accept; the server's flag and the
        // parties' phones arrive with the next pull, the status decides until then.
        o.contactOpen = null;
      },
      () => _syncQueue.enqueue(
        userId: farmerId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          'farmerResponse': CommitmentCommand.accept,
          'respondedAt': now.toIso8601String(),
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  /// Reject an order (farmer action).
  @override
  Future<void> rejectOrder(
    String orderId,
    String farmerId, {
    String? reason,
    String? declineReason,
  }) async {
    final order = await _findOrder(orderId);
    _checkFarmerResponse(order, farmerId);
    if (order.status == OrderStatus.rejected) return;
    _checkFunded(order);

    final now = DateTime.now().toUtc();
    final baseVersion = order.version;

    await _writeThenEnqueue(
      order,
      (o) {
        o.status = OrderStatus.rejected;
        o.farmerResponse = CommitmentResponse.declined;
        o.cancelledBy = 'farmer';
        o.respondedAt = now;
        o.rejectionReason = reason;
        o.declineReason = declineReason;
        o.updatedAt = now;
        o.version = baseVersion + 1;
        _closeContact(o);
      },
      () => _syncQueue.enqueue(
        userId: farmerId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          'farmerResponse': CommitmentCommand.decline,
          'respondedAt': now.toIso8601String(),
          if (reason != null) 'rejectionReason': reason,
          if (declineReason != null) 'declineReason': declineReason,
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  /// ESC-15: the trucker's answer to a transport request, or the drop-out
  /// from an accepted order before pickup (the server asks for a fresh
  /// sign-in, which the sync queue waits for).
  @override
  Future<void> respondAsTrucker(
    String orderId,
    String truckerId, {
    required bool accept,
    String? declineReason,
  }) async {
    final order = await _findOrder(orderId);
    if (truckerId.isEmpty || order.truckerId != truckerId) {
      throw const OrderActionNotAllowedException(
        'Only the trucker of this order can answer its transport request.',
      );
    }
    final answered = truckerResponseOf(order);
    if (accept && answered == CommitmentResponse.accepted) return;
    if (!accept &&
        order.status == OrderStatus.awaitingTransport &&
        (answered == CommitmentResponse.declined ||
            answered == CommitmentResponse.cancelled)) {
      return;
    }
    final dropOut = !accept && canTruckerDropOut(order, truckerId);
    if (!dropOut && !canTruckerRespond(order, truckerId)) {
      throw const OrderActionNotAllowedException(
        'This transport request is not open: the farmer has not accepted '
        'yet, or it was already answered.',
      );
    }

    final now = DateTime.now().toUtc();
    final baseVersion = order.version;

    await _writeThenEnqueue(
      order,
      (o) {
        if (accept) {
          o.status = OrderStatus.accepted;
          o.truckerResponse = CommitmentResponse.accepted;
          o.contactOpen = null;
        } else {
          o.status = OrderStatus.awaitingTransport;
          o.truckerResponse = dropOut
              ? CommitmentResponse.cancelled
              : CommitmentResponse.declined;
          o.transportFailure =
              dropOut ? 'trucker_cancelled' : 'trucker_declined';
          o.declineReason = declineReason;
          // The trucker leaves the order's contact at once.
          _closeContact(o);
        }
        o.updatedAt = now;
        o.version = baseVersion + 1;
      },
      () => _syncQueue.enqueue(
        userId: truckerId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          'truckerResponse':
              accept ? CommitmentCommand.accept : CommitmentCommand.decline,
          if (!accept && declineReason != null) 'declineReason': declineReason,
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  /// LT-12: the buyer's drop-off landmark, saved on the phone and queued
  /// (offline-first). The server checks the same rules again and refuses a
  /// change that lost the race with the pickup (`ORDER_DROPOFF_NOTE_LOCKED`);
  /// the queue then puts the order back to the server's view.
  @override
  Future<void> updateDropoffNote(
    String orderId,
    String buyerId,
    String? dropoffNote,
  ) async {
    final order = await _findOrder(orderId);
    if (!canEditDropoffNote(order, buyerId)) {
      throw const OrderActionNotAllowedException(
        'Only the buyer of a trucked order can change its drop-off note, '
        'and only before pickup.',
      );
    }
    final note = normalizeDropoffNote(dropoffNote);
    if (note == normalizeDropoffNote(order.dropoffNote)) return;

    final now = DateTime.now().toUtc();
    final baseVersion = order.version;

    await _writeThenEnqueue(
      order,
      (o) {
        o.dropoffNote = note;
        o.updatedAt = now;
        o.version = baseVersion + 1;
      },
      () => _syncQueue.enqueue(
        userId: buyerId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          // An empty note clears it on the server.
          'dropoffNote': note ?? '',
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  /// LT-14: the order's own drop-off point, saved on the phone and queued
  /// (offline-first), by the drop-off note's rules. The server checks them
  /// again and refuses a change that lost the race with the pickup
  /// (`ORDER_DELIVERY_LOCATION_LOCKED`); the queue then puts the order back
  /// to the server's view.
  @override
  Future<void> updateDeliveryLocation(
    String orderId,
    String buyerId,
    DeliveryPoint point,
  ) async {
    final order = await _findOrder(orderId);
    if (!canEditDeliveryLocation(order, buyerId)) {
      throw const OrderActionNotAllowedException(
        'Only the buyer of a trucked order can change its drop-off place, '
        'and only before pickup.',
      );
    }
    final boxed = _boxed(point);
    if (boxed == null) {
      throw const OrderActionNotAllowedException(
        'The drop-off place must be in Ethiopia.',
      );
    }
    if (boxed == orderDeliveryPoint(order)) return;

    final now = DateTime.now().toUtc();
    final baseVersion = order.version;

    await _writeThenEnqueue(
      order,
      (o) {
        o
          ..deliveryLatitude = boxed.latitude
          ..deliveryLongitude = boxed.longitude
          ..updatedAt = now
          ..version = baseVersion + 1;
      },
      () => _syncQueue.enqueue(
        userId: buyerId,
        entityType: 'order',
        entityId: orderId,
        action: 'update',
        data: jsonEncode({
          'orderId': orderId,
          'deliveryLatitude': boxed.latitude,
          'deliveryLongitude': boxed.longitude,
          'updatedAt': now.toIso8601String(),
          'version': baseVersion,
        }),
        operationId: _uuid.v4(),
      ),
    );
  }

  /// ESC-34 (FUP-285): the arrival check-in, queued (offline-first). Nothing
  /// is written to the order: the queued operation is the local mark
  /// ([getOrderById]), and the server's copy, with the time it received it,
  /// comes with the next read of the order. Every `CHECKIN_*` refusal is
  /// permanent; the sync queue then rejects the operation, which takes the
  /// mark back, and says why. Only [userId]'s own queued check-ins count as
  /// given.
  @override
  Future<bool> checkIn(
    String orderId,
    String userId, {
    required String kind,
    HandoffCheckInFix? fix,
  }) async {
    final order = await _withQueuedCheckIns(
      await _findOrder(orderId),
      userId: userId,
    );
    final now = DateTime.now().toUtc();
    if (!checkCheckInAllowed(order, userId, kind, fix, now)) return false;
    await _syncQueue.enqueue(
      userId: userId,
      entityType: handoffCheckInEntityType,
      entityId: handoffCheckInEntityId(orderId, kind),
      action: 'create',
      data: jsonEncode(
        handoffCheckInData(
          orderId: orderId,
          kind: kind,
          capturedAt: now,
          fix: fix,
        ),
      ),
      operationId: _uuid.v4(),
    );
    return true;
  }

  /// ESC-36 (FUP-336): the trucker's drop-off check-in, queued behind the
  /// arrival photo (offline-first). A new [arrivalPhotoRef] (this phone's
  /// file of [deliveryId]) is kept on the local delivery, so the photo shows
  /// and the next attempt does not ask for another, and queued as
  /// `delivery_photo` `{deliveryId}#arrival` (the sync preflight uploads it),
  /// unless one already waits (then a new file is deleted, FUP-373: it would
  /// never be sent). The check-in names its delivery in a `_` key, never
  /// sent: the sync preflight holds it back while that photo waits.
  /// As for [checkIn], the queued operation is the local mark and every
  /// `CHECKIN_*` refusal takes it back.
  @override
  Future<bool> checkInAtDropoff(
    String orderId,
    String userId, {
    required String deliveryId,
    required HandoffCheckInFix? fix,
    String? arrivalPhotoRef,
  }) async {
    final order = await _withQueuedCheckIns(
      await _findOrder(orderId),
      userId: userId,
    );
    final now = DateTime.now().toUtc();
    if (!checkDropoffCheckInAllowed(order, userId, fix, now)) return false;
    final photo = arrivalPhotoRef;
    final photoId = deliveryPhotoEntityId(deliveryId, arrivalPhotoType);
    if (photo != null &&
        DeliveryConstants.isLocalPhotoPath(photo, deliveryId)) {
      if (!await _syncQueue.hasWaitingOperationsFor(
        deliveryPhotoEntityType,
        photoId,
      )) {
        await _keepArrivalPhoto(deliveryId, photo);
        await _syncQueue.enqueue(
          userId: userId,
          entityType: deliveryPhotoEntityType,
          entityId: photoId,
          action: 'create',
          data: jsonEncode({deliveryPhotoLocalPathKey: photo}),
          operationId: _uuid.v4(),
        );
      } else if (!await _isQueuedPhotoFile(photoId, photo)) {
        await _deleteUnsentPhoto(photo);
      }
    }
    await _syncQueue.enqueue(
      userId: userId,
      entityType: handoffCheckInEntityType,
      entityId: handoffCheckInEntityId(orderId, CheckInKind.arrivedDropoff),
      action: 'create',
      data: jsonEncode({
        ...handoffCheckInData(
          orderId: orderId,
          kind: CheckInKind.arrivedDropoff,
          capturedAt: now,
          fix: fix,
        ),
        handoffCheckInDeliveryIdKey: deliveryId,
      }),
      operationId: _uuid.v4(),
    );
    return true;
  }

  /// The local delivery shows the arrival photo this phone took until the
  /// server's key replaces it (FRD-12's attach clean-up). A server key that is
  /// there already stays.
  Future<void> _keepArrivalPhoto(String deliveryId, String path) async {
    await _isar.writeTxn(() async {
      final delivery = await _isar.deliverys
          .filter()
          .deliveryIdEqualTo(deliveryId)
          .findFirst();
      final current = delivery?.arrivalPhotoRef;
      if (delivery == null ||
          (current != null &&
              DeliveryConstants.isServerPhotoRef(current, deliveryId))) {
        return;
      }
      delivery.arrivalPhotoRef = path;
      await _isar.deliverys.put(delivery);
    });
  }

  /// Whether a `delivery_photo` [photoId] the server has not refused carries
  /// the file [path] (the kept photo passed again for the next attempt).
  Future<bool> _isQueuedPhotoFile(String photoId, String path) async {
    final ops = await _syncQueue.unrejectedOperationsFor(
      deliveryPhotoEntityType,
      entityIdPrefix: photoId,
    );
    for (final op in ops) {
      try {
        final data = jsonDecode(op.data);
        if (data is Map && data[deliveryPhotoLocalPathKey] == path) {
          return true;
        }
      } on FormatException {
        continue;
      }
    }
    return false;
  }

  /// FUP-373: an arrival photo taken while another one already waits to
  /// upload is never sent, so its file is not left on the phone. Best effort.
  Future<void> _deleteUnsentPhoto(String path) async {
    try {
      await (_deletePhotoFile ?? PhotoManager().deleteLocalPhoto)(path);
    } on Object catch (e) {
      debugPrint('[OrderRepository] unsent arrival photo not deleted: $e');
    }
  }

  /// [point] when the server takes it (inside Ethiopia's box), else null.
  static DeliveryPoint? _boxed(DeliveryPoint? point) =>
      point != null && isInDeliveryLocationBox(point.latitude, point.longitude)
          ? point
          : null;

  /// ESC-48: a cancelled or rejected order closes contact at once, offline
  /// too: the parties' phones leave the device with the change.
  static void _closeContact(Order order) {
    order
      ..contactOpen = false
      ..buyerPhone = null
      ..farmerPhone = null
      ..truckerPhone = null;
  }

  Future<Order> _findOrder(String orderId) async {
    final order =
        await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();
    if (order == null) throw NotFoundException('Order $orderId not found.');
    return order;
  }

  /// Only the order's farmer accepts or rejects it.
  static void _checkFarmerResponse(Order order, String farmerId) {
    if (order.farmerId != farmerId) {
      throw const OrderActionNotAllowedException(
        'Only the farmer of this order can respond to it.',
      );
    }
  }

  /// The farmer responds to a paid order only.
  static void _checkFunded(Order order) {
    if (order.status != OrderStatus.funded) {
      throw OrderActionNotAllowedException(
        'The order is ${order.status.name}; only a funded order can be '
        'accepted or rejected.',
      );
    }
  }

  /// Apply [change] to the local order, then queue its sync operation. When
  /// queueing fails (queue full, duplicate key) the local order is put back
  /// as it was and the error is rethrown, so the caller never reports a
  /// change that will not reach the server.
  Future<void> _writeThenEnqueue(
    Order order,
    void Function(Order order) change,
    Future<void> Function() enqueue,
  ) async {
    final before = (
      status: order.status,
      farmerResponse: order.farmerResponse,
      truckerResponse: order.truckerResponse,
      transportFailure: order.transportFailure,
      declineReason: order.declineReason,
      cancelledBy: order.cancelledBy,
      respondedAt: order.respondedAt,
      rejectionReason: order.rejectionReason,
      cancelledAt: order.cancelledAt,
      cancelReason: order.cancelReason,
      cbeTransactionId: order.cbeTransactionId,
      paymentMethod: order.paymentMethod,
      updatedAt: order.updatedAt,
      version: order.version,
      contactOpen: order.contactOpen,
      buyerPhone: order.buyerPhone,
      farmerPhone: order.farmerPhone,
      truckerPhone: order.truckerPhone,
      dropoffNote: order.dropoffNote,
      deliveryLatitude: order.deliveryLatitude,
      deliveryLongitude: order.deliveryLongitude,
    );
    await _isar.writeTxn(() async {
      change(order);
      await _isar.orders.put(order);
    });
    try {
      await enqueue();
    } on Object {
      await _isar.writeTxn(() async {
        order
          ..status = before.status
          ..farmerResponse = before.farmerResponse
          ..truckerResponse = before.truckerResponse
          ..transportFailure = before.transportFailure
          ..declineReason = before.declineReason
          ..cancelledBy = before.cancelledBy
          ..respondedAt = before.respondedAt
          ..rejectionReason = before.rejectionReason
          ..cancelledAt = before.cancelledAt
          ..cancelReason = before.cancelReason
          ..cbeTransactionId = before.cbeTransactionId
          ..paymentMethod = before.paymentMethod
          ..updatedAt = before.updatedAt
          ..version = before.version
          ..contactOpen = before.contactOpen
          ..buyerPhone = before.buyerPhone
          ..farmerPhone = before.farmerPhone
          ..truckerPhone = before.truckerPhone
          ..dropoffNote = before.dropoffNote
          ..deliveryLatitude = before.deliveryLatitude
          ..deliveryLongitude = before.deliveryLongitude;
        await _isar.orders.put(order);
      });
      rethrow;
    }
  }
}
