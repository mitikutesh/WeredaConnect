import 'dart:convert';

import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

/// Offline-first repository for delivery CRUD operations.
///
/// All writes go to local Isar first, then enqueue a sync operation.
/// Reads are always from Isar (fast, works offline).
class DeliveryRepository {
  final Isar _isar;
  final SyncQueue _syncQueue;
  static const _uuid = Uuid();

  DeliveryRepository({
    required Isar isar,
    required SyncQueue syncQueue,
  })  : _isar = isar,
        _syncQueue = syncQueue;

  /// Create a new delivery.
  Future<Delivery> createDelivery({
    required String tripId,
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

    final data = jsonEncode({
      'deliveryId': deliveryId,
      'tripId': tripId,
      'listingId': listingId,
      'farmerId': farmerId,
      'farmerName': farmerName,
      'truckerId': truckerId,
      'truckerName': truckerName,
      'buyerId': buyerId,
      'buyerName': buyerName,
      'qrToken': qrToken,
      'status': DeliveryStatus.pending.name,
      'createdAt': now.toIso8601String(),
      'version': 1,
    });

    await _syncQueue.enqueue(
      userId: truckerId,
      entityType: 'delivery',
      entityId: deliveryId,
      action: 'create',
      data: data,
      operationId: _uuid.v4(),
    );

    return delivery;
  }

  /// Confirm pickup by trucker. Status → pickedUp.
  Future<void> confirmPickup({
    required String deliveryId,
    required String truckerId,
    LatLng? gpsPickup,
    String? photoRef,
  }) async {
    final delivery = await _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();

    if (delivery == null) return;
    if (delivery.status != DeliveryStatus.pending) return;

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      delivery.status = DeliveryStatus.pickedUp;
      delivery.gpsPickup = gpsPickup;
      delivery.pickupPhotoRef = photoRef;
      delivery.pickedUpAt = now;
      delivery.updatedAt = now;
      delivery.version += 1;
      await _isar.deliverys.put(delivery);
    });

    final syncData = <String, dynamic>{
      'deliveryId': deliveryId,
      'status': DeliveryStatus.pickedUp.name,
      'pickedUpAt': now.toIso8601String(),
      'updatedAt': now.toIso8601String(),
      'version': delivery.version,
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
  Future<void> confirmDelivery({
    required String deliveryId,
    required String buyerId,
    LatLng? gpsDropoff,
    String? photoRef,
  }) async {
    final delivery = await _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();

    if (delivery == null) return;
    if (delivery.status != DeliveryStatus.pickedUp &&
        delivery.status != DeliveryStatus.inTransit) {
      return;
    }

    final now = DateTime.now().toUtc();

    await _isar.writeTxn(() async {
      delivery.status = DeliveryStatus.delivered;
      delivery.gpsDropoff = gpsDropoff;
      delivery.dropoffPhotoRef = photoRef;
      delivery.confirmedAt = now;
      delivery.updatedAt = now;
      delivery.version += 1;
      await _isar.deliverys.put(delivery);
    });

    final syncData = <String, dynamic>{
      'deliveryId': deliveryId,
      'status': DeliveryStatus.delivered.name,
      'confirmedAt': now.toIso8601String(),
      'updatedAt': now.toIso8601String(),
      'version': delivery.version,
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

    await _syncQueue.enqueue(
      userId: buyerId,
      entityType: 'delivery',
      entityId: deliveryId,
      action: 'update',
      data: jsonEncode(syncData),
      operationId: _uuid.v4(),
    );
  }

  /// Raise a dispute on a delivery. Status → disputed.
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
    if (delivery.status != DeliveryStatus.delivered) return;

    final now = DateTime.now().toUtc();

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
        'version': delivery.version,
      }),
      operationId: _uuid.v4(),
    );
  }

  /// Cancel a delivery. Status → cancelled (soft delete).
  Future<void> cancelDelivery({
    required String deliveryId,
    required String userId,
  }) async {
    final delivery = await _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();

    if (delivery == null) return;
    if (delivery.status != DeliveryStatus.pending &&
        delivery.status != DeliveryStatus.pickedUp) {
      return;
    }

    final now = DateTime.now().toUtc();

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
        'version': delivery.version,
      }),
      operationId: _uuid.v4(),
    );
  }

  /// Get all deliveries for a specific trip.
  Future<List<Delivery>> getDeliveriesByTrip(String tripId) async {
    return _isar.deliverys
        .filter()
        .tripIdEqualTo(tripId)
        .sortByCreatedAtDesc()
        .findAll();
  }

  /// Get deliveries filtered by status.
  Future<List<Delivery>> getDeliveriesByStatus(DeliveryStatus status) async {
    return _isar.deliverys
        .filter()
        .statusEqualTo(status)
        .sortByCreatedAtDesc()
        .findAll();
  }

  /// Get a single delivery by its ID.
  Future<Delivery?> getDeliveryById(String deliveryId) async {
    return _isar.deliverys
        .filter()
        .deliveryIdEqualTo(deliveryId)
        .findFirst();
  }
}
