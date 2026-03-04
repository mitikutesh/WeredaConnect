import 'dart:convert';

import 'package:isar/isar.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_validation_exception.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_distance_service.dart';
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
        if (delivery.status != DeliveryStatus.inTransit) {
          throw DeliveryValidationException(
            'status',
            'Delivery must be in transit for confirmation',
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

  /// Check GPS proximity and return true if distance exceeds threshold.
  ///
  /// This is a WARNING-ONLY check. It never blocks the operation because
  /// GPS accuracy is unreliable in rural Ethiopian areas with poor
  /// satellite coverage. The flag is included in sync data so the server
  /// can flag the transaction for review.
  ///
  /// Returns `true` if a warning should be added (distance > 5 km), or
  /// `false` if coordinates are missing or within the threshold.
  bool _shouldWarnGpsDistance(LatLng? capturedGps, LatLng? referenceGps) {
    if (capturedGps == null || referenceGps == null) return false;
    final distanceMeters = GpsDistanceService.haversineDistance(
      capturedGps.latitude,
      capturedGps.longitude,
      referenceGps.latitude,
      referenceGps.longitude,
    );
    return distanceMeters > DeliveryConstants.gpsProximityThresholdKm * 1000;
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

    await _updateListingStatus(listingId, ListingStatus.reserved, truckerId);

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
        'deliveryId': deliveryId,
        'qrNonce': qrNonce,
        'updatedAt': delivery.updatedAt.toIso8601String(),
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

    // GPS proximity warning: compare pickup GPS against listing location.
    // Non-blocking -- only flags the sync payload for server review.
    LatLng? listingLocation;
    if (gpsPickup != null) {
      final listing = await _isar.cropListings
          .filter()
          .listingIdEqualTo(delivery.listingId)
          .findFirst();
      listingLocation = listing?.location;
    }
    final gpsDistanceWarning =
        _shouldWarnGpsDistance(gpsPickup, listingLocation);

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

    if (scannedQrNonce != null) {
      syncData['scannedQrNonce'] = scannedQrNonce;
    }

    if (gpsDistanceWarning) {
      syncData['gpsDistanceWarning'] = true;
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

    // GPS proximity warning: compare dropoff GPS against listing location.
    // NOTE: The listing location is the farm (origin), not the buyer's
    // destination. When a destination GPS field is added to Trip or
    // Delivery, update this to use that instead. For now the server
    // performs the authoritative check; this flags obvious outliers.
    LatLng? destinationRef;
    if (gpsDropoff != null) {
      final listing = await _isar.cropListings
          .filter()
          .listingIdEqualTo(delivery.listingId)
          .findFirst();
      destinationRef = listing?.location;
    }
    final gpsDistanceWarning =
        _shouldWarnGpsDistance(gpsDropoff, destinationRef);

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

    if (scannedQrNonce != null) {
      syncData['scannedQrNonce'] = scannedQrNonce;
    }

    if (gpsDistanceWarning) {
      syncData['gpsDistanceWarning'] = true;
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
        'deliveryId': deliveryId,
        'status': DeliveryStatus.inTransit.name,
        'updatedAt': now.toIso8601String(),
        'version': delivery.version,
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
