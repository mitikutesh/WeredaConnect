import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_validation_exception.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';

/// Offline-first repository for delivery CRUD operations.
///
/// All writes go to local Isar first, then enqueue a sync operation.
/// Reads are always from Isar (fast, works offline).
class DeliveryRepository extends BaseDeliveryRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;
  static const _uuid = Uuid();

  DeliveryRepository({
    required Isar isar,
    required SyncQueue syncQueue,
  })  : _isar = isar,
        _syncQueue = syncQueue;

  /// Validate delivery operation before enqueuing to sync queue.
  /// Mirrors server-side checks to reject invalid operations early.
  /// Throws DeliveryValidationException with localized message on failure.
  void _validateDeliveryOperation(
    Delivery delivery,
    String action,
    String userId, {
    String? disputeReason,
  }) {
    if (!DeliveryConstants.uuidRegex.hasMatch(delivery.deliveryId)) {
      throw DeliveryValidationException(
        'deliveryId',
        'Invalid delivery ID format',
      );
    }

    switch (action) {
      case 'confirmPickup':
        if (delivery.status != DeliveryStatus.pending) {
          throw DeliveryValidationException(
            'status',
            'Delivery must be pending for pickup',
          );
        }
        if (delivery.truckerId != userId) {
          throw DeliveryValidationException(
            'truckerId',
            'Only the trucker can confirm pickup',
          );
        }
      case 'confirmDelivery':
        // The trucker's separate "mark in transit" step is optional, so the
        // buyer may confirm the handoff directly from pickedUp. This mirrors
        // the server transition map (pickedUp -> delivered) and the detail
        // screen's button gating. Requiring inTransit here silently blocked
        // the confirmation, leaving the delivery stuck at pickedUp so escrow
        // never released to the farmer.
        //
        // Self-pickup has NO trucker/transit leg: the buyer collects at the
        // farm and confirms straight from `pending` (server allows the
        // self_pickup pending -> delivered hop). Without this, the buyer's scan
        // threw here and the self-pickup escrow never released.
        final isSelfPickup =
            delivery.deliveryType == DeliveryConstants.selfPickupDeliveryType;
        final validForConfirm = isSelfPickup
            ? delivery.status == DeliveryStatus.pending
            : (delivery.status == DeliveryStatus.pickedUp ||
                delivery.status == DeliveryStatus.inTransit);
        if (!validForConfirm) {
          throw DeliveryValidationException(
            'status',
            isSelfPickup
                ? 'Self-pickup must be pending to confirm collection'
                : 'Delivery must be picked up or in transit for confirmation',
          );
        }
        if (delivery.buyerId != userId) {
          throw DeliveryValidationException(
            'buyerId',
            'Only the buyer can confirm delivery',
          );
        }
      case 'markInTransit':
        if (delivery.status != DeliveryStatus.pickedUp) {
          throw DeliveryValidationException(
            'status',
            'Delivery must be picked up to mark in transit',
          );
        }
        if (delivery.truckerId != userId) {
          throw DeliveryValidationException(
            'truckerId',
            'Only the trucker can mark in transit',
          );
        }
      case 'raiseDispute':
        if (delivery.status != DeliveryStatus.delivered) {
          throw DeliveryValidationException(
            'status',
            'Can only dispute delivered items',
          );
        }
        if (delivery.confirmedAt != null &&
            DateTime.now().toUtc().difference(delivery.confirmedAt!) >
                DeliveryConstants.disputeWindow) {
          throw DeliveryValidationException(
            'confirmedAt',
            'Dispute window (48 hours) has expired',
          );
        }
        if (disputeReason == null ||
            disputeReason.isEmpty ||
            disputeReason.length > DeliveryConstants.maxDisputeReasonLength) {
          throw DeliveryValidationException(
            'disputeReason',
            'Reason must be 1-500 characters',
          );
        }
        if (delivery.farmerId != userId &&
            delivery.truckerId != userId &&
            delivery.buyerId != userId) {
          throw DeliveryValidationException(
            'userId',
            'Only participants can raise disputes',
          );
        }
      case 'cancel':
        if (delivery.status != DeliveryStatus.pending &&
            delivery.status != DeliveryStatus.pickedUp) {
          throw DeliveryValidationException(
            'status',
            'Cannot cancel after delivery',
          );
        }
        if (delivery.farmerId != userId &&
            delivery.truckerId != userId &&
            delivery.buyerId != userId) {
          throw DeliveryValidationException(
            'userId',
            'Only participants can cancel',
          );
        }
    }
  }

  /// GPS coordinate range validation (if provided).
  void _validateGpsCoordinates(double? lat, double? lon) {
    if (lat == null || lon == null) return;
    // F12-CR-03: Reject NaN and Infinity before range checks
    if (lat.isNaN || lat.isInfinite || lon.isNaN || lon.isInfinite) {
      throw DeliveryValidationException(
        'gps',
        'GPS coordinates must be finite numbers',
      );
    }
    if (lat < -90 || lat > 90) {
      throw DeliveryValidationException(
        'latitude',
        'Latitude must be between -90 and 90',
      );
    }
    if (lon < -180 || lon > 180) {
      throw DeliveryValidationException(
        'longitude',
        'Longitude must be between -180 and 180',
      );
    }
  }

  /// Update the status of a crop listing and enqueue a sync operation.
  ///
  /// Called as a side-effect of delivery lifecycle transitions so the
  /// listing status stays in sync with its delivery state.
  Future<void> _updateListingStatus(
    String listingId,
    ListingStatus newStatus,
    String userId,
  ) async {
    final listing = await _isar.cropListings
        .filter()
        .listingIdEqualTo(listingId)
        .findFirst();

    if (listing == null) return;

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      listing.status = newStatus;
      listing.updatedAt = now;
      listing.version += 1;
      await _isar.cropListings.put(listing);
    });

    await _syncQueue.enqueue(
      userId: userId,
      entityType: 'cropListing',
      entityId: listingId,
      action: 'update',
      data: jsonEncode({
        'listingId': listingId,
        'status': newStatus.name,
        'updatedAt': now.toIso8601String(),
        'version': listing.version,
      }),
      operationId: _uuid.v4(),
    );
  }

  /// Create a new delivery.
  @override
  Future<Delivery> createDelivery({
    required String tripId,
    required String orderId,
    required String listingId,
    required String farmerId,
    required String farmerName,
    required String truckerId,
    required String truckerName,
    required String buyerId,
    required String buyerName,
    required String qrToken,
  }) async {
    final deliveryId = _uuid.v4();
    final now = DateTime.now().toUtc();

    final delivery = Delivery()
      ..deliveryId = deliveryId
      ..tripId = tripId
      ..orderId = orderId
      ..listingId = listingId
      ..farmerId = farmerId
      ..farmerName = farmerName
      ..truckerId = truckerId
      ..truckerName = truckerName
      ..buyerId = buyerId
      ..buyerName = buyerName
      ..qrToken = qrToken
      ..status = DeliveryStatus.pending
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;

    await _isar.writeTxn(() async {
      await _isar.deliverys.put(delivery);
    });

    // Send only fields the server accepts (server whitelist). deliveryId is the
    // operation entityId; createdAt/updatedAt are server-managed timestamps.
    final data = jsonEncode({
      'tripId': tripId,
      'orderId': orderId,
      'listingId': listingId,
      'farmerId': farmerId,
      'farmerName': farmerName,
      'truckerId': truckerId,
      'truckerName': truckerName,
      'buyerId': buyerId,
      'buyerName': buyerName,
      'qrToken': qrToken,
      'status': DeliveryStatus.pending.name,
      'version': 1,
    });

    // The local Isar write above is authoritative (offline-first). Enqueue
    // failures (queue full / duplicate idempotency key) must NOT surface as a
    // save failure — the delivery is persisted; the sync layer reconciles later.
    try {
      await _syncQueue.enqueue(
        userId: truckerId,
        entityType: 'delivery',
        entityId: deliveryId,
        action: 'create',
        data: data,
        operationId: _uuid.v4(),
      );
    } on Object catch (e) {
      debugPrint(
        '[DeliveryRepository] delivery saved locally but sync enqueue '
        'failed: $e',
      );
    }

    await _updateListingStatus(listingId, ListingStatus.reserved, truckerId);

    return delivery;
  }

  /// Buyer creates a self-pickup delivery (no trucker). The farmer generates the
  /// QR later; the buyer confirms the handoff at the farm by scanning it.
  @override
  Future<Delivery> createSelfPickupDelivery({
    required String orderId,
    required String listingId,
    required String farmerId,
    required String farmerName,
    required String buyerId,
    required String buyerName,
  }) async {
    final deliveryId = _uuid.v4();
    final now = DateTime.now().toUtc();

    final delivery = Delivery()
      ..deliveryId = deliveryId
      // No trip: the sort key is synthesized from the deliveryId (server does the
      // same). truckerId/truckerName/qrToken are empty — there is no trucker and
      // the farmer generates the QR after the delivery exists.
      ..tripId = deliveryId
      ..orderId = orderId
      ..listingId = listingId
      ..farmerId = farmerId
      ..farmerName = farmerName
      ..truckerId = ''
      ..truckerName = ''
      ..buyerId = buyerId
      ..buyerName = buyerName
      ..qrToken = ''
      ..deliveryType = DeliveryConstants.selfPickupDeliveryType
      ..status = DeliveryStatus.pending
      ..createdAt = now
      ..updatedAt = now
      ..version = 1;

    await _isar.writeTxn(() async {
      await _isar.deliverys.put(delivery);
    });

    final data = jsonEncode({
      'orderId': orderId,
      'listingId': listingId,
      'farmerId': farmerId,
      'farmerName': farmerName,
      'buyerId': buyerId,
      'buyerName': buyerName,
      'deliveryType': DeliveryConstants.selfPickupDeliveryType,
      'status': DeliveryStatus.pending.name,
      'version': 1,
    });

    try {
      await _syncQueue.enqueue(
        userId: buyerId,
        entityType: 'delivery',
        entityId: deliveryId,
        action: 'create',
        data: data,
        operationId: _uuid.v4(),
      );
    } on Object catch (e) {
      debugPrint(
        '[DeliveryRepository] self-pickup delivery saved locally but sync '
        'enqueue failed: $e',
      );
    }

    return delivery;
  }

  /// Persist QR nonce for server-side cross-validation.
  ///
  /// Called by the QR generator after creating a token. The nonce is
  /// saved to the local delivery record and enqueued for sync.
  @override
  Future<void> persistQrNonce({
    required String deliveryId,
    required String qrNonce,
    required String userId,
  }) async {
    final delivery = await _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();

    if (delivery == null) return;

    // Only delivery participants can persist QR nonce
    if (delivery.farmerId != userId &&
        delivery.truckerId != userId &&
        delivery.buyerId != userId) {
      return;
    }

    await _isar.writeTxn(() async {
      delivery.qrNonce = qrNonce;
      delivery.updatedAt = DateTime.now().toUtc();
      await _isar.deliverys.put(delivery);
    });

    await _syncQueue.enqueue(
      userId: userId,
      entityType: 'delivery',
      entityId: deliveryId,
      action: 'update',
      data: jsonEncode({
        'qrNonce': qrNonce,
        'version': delivery.version,
      }),
      operationId: _uuid.v4(),
    );
  }

  /// Confirm pickup by trucker. Status → pickedUp.
  @override
  Future<void> confirmPickup({
    required String deliveryId,
    required String truckerId,
    LatLng? gpsPickup,
    String? photoRef,
    String? scannedQrNonce,
  }) async {
    final delivery = await _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();

    if (delivery == null) return;

    _validateDeliveryOperation(delivery, 'confirmPickup', truckerId);
    _validateGpsCoordinates(gpsPickup?.latitude, gpsPickup?.longitude);

    final now = DateTime.now().toUtc();
    // Send the PRE-increment version to the server: its optimistic-concurrency
    // check (#v = :expectedVersion) compares against the version it currently holds.
    // Sending the post-increment value makes every transition conflict and revert.
    final baseVersion = delivery.version;

    await _isar.writeTxn(() async {
      delivery.status = DeliveryStatus.pickedUp;
      delivery.gpsPickup = gpsPickup;
      delivery.pickupPhotoRef = photoRef;
      delivery.pickedUpAt = now;
      delivery.updatedAt = now;
      delivery.version += 1;
      await _isar.deliverys.put(delivery);
    });

    // Server whitelist: only these fields are accepted on a delivery update.
    // pickedUpAt/updatedAt are stamped server-side; deliveryId is the entityId.
    final syncData = <String, dynamic>{
      'status': DeliveryStatus.pickedUp.name,
      'version': baseVersion,
    };

    if (gpsPickup != null) {
      syncData['gpsPickup'] = {
        'latitude': gpsPickup.latitude,
        'longitude': gpsPickup.longitude,
      };
    }

    if (photoRef != null) {
      syncData['pickupPhotoRef'] = photoRef;
    }

    if (scannedQrNonce != null) {
      syncData['scannedQrNonce'] = scannedQrNonce;
    }

    await _syncQueue.enqueue(
      userId: truckerId,
      entityType: 'delivery',
      entityId: deliveryId,
      action: 'update',
      data: jsonEncode(syncData),
      operationId: _uuid.v4(),
    );
  }

  /// Confirm delivery by buyer. Status → delivered.
  @override
  Future<void> confirmDelivery({
    required String deliveryId,
    required String buyerId,
    LatLng? gpsDropoff,
    String? photoRef,
    String? scannedQrNonce,
  }) async {
    final delivery = await _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();

    if (delivery == null) return;

    _validateDeliveryOperation(delivery, 'confirmDelivery', buyerId);
    _validateGpsCoordinates(gpsDropoff?.latitude, gpsDropoff?.longitude);

    final now = DateTime.now().toUtc();
    // Pre-increment version for the server CAS (see confirmPickup).
    final baseVersion = delivery.version;

    await _isar.writeTxn(() async {
      delivery.status = DeliveryStatus.delivered;
      delivery.gpsDropoff = gpsDropoff;
      delivery.dropoffPhotoRef = photoRef;
      delivery.confirmedAt = now;
      delivery.updatedAt = now;
      delivery.version += 1;
      await _isar.deliverys.put(delivery);
    });

    // Server whitelist: only these fields are accepted on a delivery update.
    // confirmedAt/updatedAt are stamped server-side; deliveryId is the entityId.
    final syncData = <String, dynamic>{
      'status': DeliveryStatus.delivered.name,
      'version': baseVersion,
    };

    if (gpsDropoff != null) {
      syncData['gpsDropoff'] = {
        'latitude': gpsDropoff.latitude,
        'longitude': gpsDropoff.longitude,
      };
    }

    if (photoRef != null) {
      syncData['dropoffPhotoRef'] = photoRef;
    }

    if (scannedQrNonce != null) {
      syncData['scannedQrNonce'] = scannedQrNonce;
    }

    await _syncQueue.enqueue(
      userId: buyerId,
      entityType: 'delivery',
      entityId: deliveryId,
      action: 'update',
      data: jsonEncode(syncData),
      operationId: _uuid.v4(),
    );

    await _updateListingStatus(delivery.listingId, ListingStatus.sold, buyerId);
  }

  /// Mark delivery as in transit by trucker. Status → inTransit.
  @override
  Future<void> markInTransit({
    required String deliveryId,
    required String truckerId,
  }) async {
    final delivery = await _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();

    if (delivery == null) return;

    _validateDeliveryOperation(delivery, 'markInTransit', truckerId);

    final now = DateTime.now().toUtc();
    // Pre-increment version for the server CAS (see confirmPickup).
    final baseVersion = delivery.version;

    await _isar.writeTxn(() async {
      delivery.status = DeliveryStatus.inTransit;
      delivery.updatedAt = now;
      delivery.version += 1;
      await _isar.deliverys.put(delivery);
    });

    await _syncQueue.enqueue(
      userId: truckerId,
      entityType: 'delivery',
      entityId: deliveryId,
      action: 'update',
      data: jsonEncode({
        'status': DeliveryStatus.inTransit.name,
        'version': baseVersion,
      }),
      operationId: _uuid.v4(),
    );
  }

  /// Raise a dispute on a delivery. Status → disputed.
  ///
  /// [reason] must be 1-500 characters.
  /// Throws [DeliveryValidationException] if validation fails.
  @override
  Future<void> raiseDispute({
    required String deliveryId,
    required String userId,
    required String reason,
  }) async {
    final delivery = await _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();

    if (delivery == null) return;

    _validateDeliveryOperation(
      delivery,
      'raiseDispute',
      userId,
      disputeReason: reason,
    );

    final now = DateTime.now().toUtc();
    // Pre-increment version for the server CAS (see confirmPickup).
    final baseVersion = delivery.version;

    await _isar.writeTxn(() async {
      delivery.status = DeliveryStatus.disputed;
      delivery.disputeReason = reason;
      delivery.updatedAt = now;
      delivery.version += 1;
      await _isar.deliverys.put(delivery);
    });

    await _syncQueue.enqueue(
      userId: userId,
      entityType: 'delivery',
      entityId: deliveryId,
      action: 'update',
      data: jsonEncode({
        'deliveryId': deliveryId,
        'status': DeliveryStatus.disputed.name,
        'disputeReason': reason,
        'updatedAt': now.toIso8601String(),
        'version': baseVersion,
      }),
      operationId: _uuid.v4(),
    );
  }

  /// Cancel a delivery. Status → cancelled (soft delete).
  @override
  Future<void> cancelDelivery({
    required String deliveryId,
    required String userId,
  }) async {
    final delivery = await _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();

    if (delivery == null) return;

    _validateDeliveryOperation(delivery, 'cancel', userId);

    final now = DateTime.now().toUtc();
    // Pre-increment version for the server CAS (see confirmPickup).
    final baseVersion = delivery.version;

    await _isar.writeTxn(() async {
      delivery.status = DeliveryStatus.cancelled;
      delivery.updatedAt = now;
      delivery.version += 1;
      await _isar.deliverys.put(delivery);
    });

    await _syncQueue.enqueue(
      userId: userId,
      entityType: 'delivery',
      entityId: deliveryId,
      action: 'delete',
      data: jsonEncode({
        'deliveryId': deliveryId,
        'status': DeliveryStatus.cancelled.name,
        'updatedAt': now.toIso8601String(),
        'version': baseVersion,
      }),
      operationId: _uuid.v4(),
    );

    await _updateListingStatus(
      delivery.listingId,
      ListingStatus.active,
      userId,
    );
  }

  /// Get all deliveries for a specific trip, scoped to a user.
  @override
  Future<List<Delivery>> getDeliveriesByTrip(
    String tripId, {
    required String userId,
  }) async {
    return _isar.deliverys
        .filter()
        .tripIdEqualTo(tripId)
        .group(
          (q) => q
              .farmerIdEqualTo(userId)
              .or()
              .truckerIdEqualTo(userId)
              .or()
              .buyerIdEqualTo(userId),
        )
        .sortByCreatedAtDesc()
        .findAll();
  }

  /// Get deliveries filtered by status, scoped to a user.
  @override
  Future<List<Delivery>> getDeliveriesByStatus(
    DeliveryStatus status, {
    required String userId,
  }) async {
    return _isar.deliverys
        .filter()
        .statusEqualTo(status)
        .group(
          (q) => q
              .farmerIdEqualTo(userId)
              .or()
              .truckerIdEqualTo(userId)
              .or()
              .buyerIdEqualTo(userId),
        )
        .sortByCreatedAtDesc()
        .findAll();
  }

  /// Get a single delivery by its ID, verifying user is a participant.
  ///
  /// Returns null if the delivery doesn't exist or user is not a participant.
  @override
  Future<Delivery?> getDeliveryById(
    String deliveryId, {
    required String userId,
  }) async {
    final delivery = await _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();
    if (delivery == null) return null;
    if (delivery.farmerId != userId &&
        delivery.truckerId != userId &&
        delivery.buyerId != userId) {
      return null;
    }
    return delivery;
  }

  /// Local lookup: every delivery the user can see is already in Isar, so
  /// [orderCreatedAt] is not needed.
  @override
  Future<Delivery?> findActiveDeliveryForOrder(
    String orderId, {
    required String userId,
    required String role,
    DateTime? orderCreatedAt,
  }) async {
    final matches = await _isar.deliverys
        .filter()
        .orderIdEqualTo(orderId)
        .not()
        .statusEqualTo(DeliveryStatus.cancelled)
        .sortByCreatedAtDesc()
        .findAll();
    for (final d in matches) {
      if (d.farmerId == userId ||
          d.truckerId == userId ||
          d.buyerId == userId) {
        return d;
      }
    }
    return null;
  }

  /// Get all deliveries where the user is a participant.
  /// Queries Isar by farmerId, truckerId, or buyerId.
  /// Optional role filter: 'farmer', 'trucker', 'buyer'.
  /// Optional statusFilter: matches DeliveryStatus enum name.
  @override
  Future<List<Delivery>> getDeliveriesByUser(
    String userId, {
    String? role,
    String? statusFilter,
  }) async {
    List<Delivery> results;

    if (role == 'farmer') {
      results = await _isar.deliverys
          .filter()
          .farmerIdEqualTo(userId)
          .sortByCreatedAtDesc()
          .findAll();
    } else if (role == 'trucker') {
      results = await _isar.deliverys
          .filter()
          .truckerIdEqualTo(userId)
          .sortByCreatedAtDesc()
          .findAll();
    } else if (role == 'buyer') {
      results = await _isar.deliverys
          .filter()
          .buyerIdEqualTo(userId)
          .sortByCreatedAtDesc()
          .findAll();
    } else {
      // All roles: single query using group/or (avoids 3 queries + in-memory dedup)
      var query = _isar.deliverys.filter().group(
            (q) => q
                .farmerIdEqualTo(userId)
                .or()
                .truckerIdEqualTo(userId)
                .or()
                .buyerIdEqualTo(userId),
          );

      // Push status filter into Isar query when provided
      if (statusFilter != null) {
        final targetStatus = DeliveryStatus.values.firstWhere(
          (s) => s.name == statusFilter,
          orElse: () => DeliveryStatus.pending,
        );
        query = query.statusEqualTo(targetStatus);
      }

      results = await query.sortByCreatedAtDesc().findAll();
      return results;
    }

    // Apply status filter if provided (for role-specific queries)
    if (statusFilter != null) {
      final targetStatus = DeliveryStatus.values.firstWhere(
        (s) => s.name == statusFilter,
        orElse: () => DeliveryStatus.pending,
      );
      results = results.where((d) => d.status == targetStatus).toList();
    }

    return results;
  }
}
