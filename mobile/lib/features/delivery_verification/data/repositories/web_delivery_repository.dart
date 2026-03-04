import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';

/// Web-only delivery repository.
///
/// All operations use HTTP calls via ApiClient. No Isar dependency.
class WebDeliveryRepository extends BaseDeliveryRepository {
  final ApiClient _api;

  WebDeliveryRepository({required ApiClient api}) : _api = api;

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
    final deliveryId =
        'dlv_${DateTime.now().millisecondsSinceEpoch}_${farmerId.hashCode.abs()}';
    final now = DateTime.now().toUtc().toIso8601String();

    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'delivery',
            'operationType': 'create',
            'entityId': deliveryId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_create_$deliveryId',
            'data': {
              'tripId': tripId,
              'listingId': listingId,
              'farmerId': farmerId,
              'farmerName': farmerName,
              'truckerId': truckerId,
              'truckerName': truckerName,
              'buyerId': buyerId,
              'buyerName': buyerName,
              'qrToken': qrToken,
              'status': 'pending',
            },
          },
        ],
      },
    );

    return Delivery()
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
      ..createdAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc()
      ..version = 1;
  }

  @override
  Future<void> persistQrNonce({
    required String deliveryId,
    required String qrNonce,
    required String userId,
  }) async {
    // On web, QR nonces are not needed (no QR scanning on web)
  }

  @override
  Future<void> confirmPickup({
    required String deliveryId,
    required String truckerId,
    LatLng? gpsPickup,
    String? photoRef,
    String? scannedQrNonce,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    final data = <String, dynamic>{
      'status': 'pickedUp',
      'truckerId': truckerId,
    };
    if (gpsPickup != null) {
      data['gpsPickup'] = '${gpsPickup.latitude},${gpsPickup.longitude}';
    }
    if (photoRef != null) data['pickupPhotoRef'] = photoRef;

    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'delivery',
            'operationType': 'update',
            'entityId': deliveryId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_pickup_${deliveryId}_$now',
            'data': data,
          },
        ],
      },
    );
  }

  @override
  Future<void> confirmDelivery({
    required String deliveryId,
    required String buyerId,
    LatLng? gpsDropoff,
    String? photoRef,
    String? scannedQrNonce,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    final data = <String, dynamic>{
      'status': 'delivered',
      'buyerId': buyerId,
    };
    if (gpsDropoff != null) {
      data['gpsDropoff'] = '${gpsDropoff.latitude},${gpsDropoff.longitude}';
    }
    if (photoRef != null) data['dropoffPhotoRef'] = photoRef;

    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'delivery',
            'operationType': 'update',
            'entityId': deliveryId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_delivery_${deliveryId}_$now',
            'data': data,
          },
        ],
      },
    );
  }

  @override
  Future<void> markInTransit({
    required String deliveryId,
    required String truckerId,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'delivery',
            'operationType': 'update',
            'entityId': deliveryId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_transit_${deliveryId}_$now',
            'data': {'status': 'inTransit', 'truckerId': truckerId},
          },
        ],
      },
    );
  }

  @override
  Future<void> raiseDispute({
    required String deliveryId,
    required String userId,
    required String reason,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'delivery',
            'operationType': 'update',
            'entityId': deliveryId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_dispute_${deliveryId}_$now',
            'data': {
              'status': 'disputed',
              'disputeReason': reason,
            },
          },
        ],
      },
    );
  }

  @override
  Future<void> cancelDelivery({
    required String deliveryId,
    required String userId,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
      '/sync',
      data: {
        'operations': [
          {
            'entityType': 'delivery',
            'operationType': 'update',
            'entityId': deliveryId,
            'clientTimestamp': now,
            'idempotencyKey': 'web_cancel_${deliveryId}_$now',
            'data': {'status': 'cancelled'},
          },
        ],
      },
    );
  }

  @override
  Future<List<Delivery>> getDeliveriesByTrip(
    String tripId, {
    required String userId,
  }) async {
    final data = await _api.get<Map<String, dynamic>>(
      '/deliveries',
      queryParameters: {'tripId': tripId},
    );
    return _parseDeliveries(data);
  }

  @override
  Future<List<Delivery>> getDeliveriesByStatus(
    DeliveryStatus status, {
    required String userId,
  }) async {
    final data = await _api.get<Map<String, dynamic>>(
      '/deliveries',
      queryParameters: {'status': status.name},
    );
    return _parseDeliveries(data);
  }

  @override
  Future<Delivery?> getDeliveryById(
    String deliveryId, {
    required String userId,
  }) async {
    try {
      final data =
          await _api.get<Map<String, dynamic>>('/deliveries/$deliveryId');
      if (data.containsKey('deliveryId')) return _parseDeliveryMap(data);
      if (data.containsKey('item')) {
        return _parseDeliveryMap(data['item'] as Map<String, dynamic>);
      }
      return null;
    } on Exception {
      return null;
    }
  }

  @override
  Future<List<Delivery>> getDeliveriesByUser(
    String userId, {
    String? role,
    String? statusFilter,
  }) async {
    final params = <String, dynamic>{};
    if (role == 'farmer') {
      params['farmerId'] = userId;
    } else if (role == 'trucker') {
      params['truckerId'] = userId;
    } else if (role == 'buyer') {
      params['buyerId'] = userId;
    } else {
      params['buyerId'] = userId;
    }
    if (statusFilter != null) params['status'] = statusFilter;
    final data = await _api.get<Map<String, dynamic>>(
      '/deliveries',
      queryParameters: params,
    );
    return _parseDeliveries(data);
  }

  List<Delivery> _parseDeliveries(Map<String, dynamic> data) {
    final items = data['items'] as List<dynamic>? ?? [];
    return items
        .map((item) => _parseDeliveryMap(item as Map<String, dynamic>))
        .toList();
  }

  Delivery _parseDeliveryMap(Map<String, dynamic> m) {
    return Delivery()
      ..deliveryId = m['deliveryId'] as String? ?? ''
      ..tripId = m['tripId'] as String? ?? ''
      ..listingId = m['listingId'] as String? ?? ''
      ..farmerId = m['farmerId'] as String? ?? ''
      ..farmerName = m['farmerName'] as String? ?? ''
      ..truckerId = m['truckerId'] as String? ?? ''
      ..truckerName = m['truckerName'] as String? ?? ''
      ..buyerId = m['buyerId'] as String? ?? ''
      ..buyerName = m['buyerName'] as String? ?? ''
      ..qrToken = m['qrToken'] as String? ?? ''
      ..status = _parseDeliveryStatus(m['status'] as String?)
      ..pickedUpAt = DateTime.tryParse(m['pickedUpAt'] as String? ?? '')
      ..confirmedAt = DateTime.tryParse(m['confirmedAt'] as String? ?? '')
      ..disputeReason = m['disputeReason'] as String?
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt =
          DateTime.tryParse(m['updatedAt'] as String? ?? '') ?? DateTime.now()
      ..version = (m['version'] as num?)?.toInt() ?? 1;
  }

  DeliveryStatus _parseDeliveryStatus(String? s) {
    return DeliveryStatus.values.firstWhere(
      (e) => e.name == s,
      orElse: () => DeliveryStatus.pending,
    );
  }
}
