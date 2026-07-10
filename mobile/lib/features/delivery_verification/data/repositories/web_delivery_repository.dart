import 'package:uuid/uuid.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';

/// Web-only delivery repository.
///
/// All operations use HTTP calls via ApiClient. No Isar dependency.
class WebDeliveryRepository extends BaseDeliveryRepository {
  static const _uuid = Uuid();
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
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'delivery',
            'action': 'create',
            'entityId': deliveryId,
            'timestamp': now,
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
  Future<Delivery> createSelfPickupDelivery({
    required String orderId,
    required String listingId,
    required String farmerId,
    required String farmerName,
    required String buyerId,
    required String buyerName,
  }) async {
    final deliveryId =
        'dlv_${DateTime.now().millisecondsSinceEpoch}_${buyerId.hashCode.abs()}';
    final now = DateTime.now().toUtc().toIso8601String();

    await _api.post<dynamic>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'delivery',
            'action': 'create',
            'entityId': deliveryId,
            'timestamp': now,
            'idempotencyKey': 'web_create_$deliveryId',
            'data': {
              'orderId': orderId,
              'listingId': listingId,
              'farmerId': farmerId,
              'farmerName': farmerName,
              'buyerId': buyerId,
              'buyerName': buyerName,
              'deliveryType': 'self_pickup',
              'status': 'pending',
            },
          },
        ],
      },
    );

    return Delivery()
      ..deliveryId = deliveryId
      ..tripId = deliveryId
      ..listingId = listingId
      ..farmerId = farmerId
      ..farmerName = farmerName
      ..truckerId = ''
      ..truckerName = ''
      ..buyerId = buyerId
      ..buyerName = buyerName
      ..qrToken = ''
      ..deliveryType = 'self_pickup'
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

  /// Fetch the delivery's current server version for optimistic-concurrency.
  /// The server delivery update enforces `#v = :expectedVersion` against the
  /// version it currently holds, so every mutation MUST send it (pre-increment)
  /// or the CAS conflicts and the transition silently reverts. No local store on
  /// web means we must read it back before mutating.
  Future<int> _currentVersion(String deliveryId) async {
    final current = await getDeliveryById(deliveryId, userId: '');
    return current?.version ?? 1;
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
      'version': await _currentVersion(deliveryId),
    };
    if (gpsPickup != null) {
      // Server expects a {latitude, longitude} object (not a "lat,lng" string),
      // matching the mobile payload — otherwise GPS is mis-stored and the
      // proximity/fraud checks silently skip.
      data['gpsPickup'] = {
        'latitude': gpsPickup.latitude,
        'longitude': gpsPickup.longitude,
      };
    }
    if (photoRef != null) data['pickupPhotoRef'] = photoRef;
    if (scannedQrNonce != null) data['scannedQrNonce'] = scannedQrNonce;

    await _api.post<dynamic>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'delivery',
            'action': 'update',
            'entityId': deliveryId,
            'timestamp': now,
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
      'version': await _currentVersion(deliveryId),
    };
    if (gpsDropoff != null) {
      // Server expects a {latitude, longitude} object (see confirmPickup).
      data['gpsDropoff'] = {
        'latitude': gpsDropoff.latitude,
        'longitude': gpsDropoff.longitude,
      };
    }
    if (photoRef != null) data['dropoffPhotoRef'] = photoRef;
    if (scannedQrNonce != null) data['scannedQrNonce'] = scannedQrNonce;

    await _api.post<dynamic>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'delivery',
            'action': 'update',
            'entityId': deliveryId,
            'timestamp': now,
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
    final version = await _currentVersion(deliveryId);
    await _api.post<dynamic>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'delivery',
            'action': 'update',
            'entityId': deliveryId,
            'timestamp': now,
            'idempotencyKey': 'web_transit_${deliveryId}_$now',
            'data': {
              'status': 'inTransit',
              'truckerId': truckerId,
              'version': version,
            },
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
    final version = await _currentVersion(deliveryId);
    await _api.post<dynamic>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'delivery',
            'action': 'update',
            'entityId': deliveryId,
            'timestamp': now,
            'idempotencyKey': 'web_dispute_${deliveryId}_$now',
            'data': {
              'status': 'disputed',
              'disputeReason': reason,
              'version': version,
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
    final version = await _currentVersion(deliveryId);
    await _api.post<dynamic>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'delivery',
            'action': 'update',
            'entityId': deliveryId,
            'timestamp': now,
            'idempotencyKey': 'web_cancel_${deliveryId}_$now',
            'data': {'status': 'cancelled', 'version': version},
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
      // Server wraps the single-GET response as {"delivery": {...}}.
      if (data['delivery'] is Map<String, dynamic>) {
        return _parseDeliveryMap(data['delivery'] as Map<String, dynamic>);
      }
      if (data.containsKey('deliveryId')) return _parseDeliveryMap(data);
      if (data['item'] is Map<String, dynamic>) {
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
    // Buyer (and default): use the participant-scoped "mine" query so ALL
    // statuses are returned. The server derives the buyer from the JWT via
    // ParticipantIndex. Farmer/trucker participant queries are a server
    // follow-up (ParticipantIndex is buyer-keyed), so they keep the existing
    // filter path.
    final params = <String, dynamic>{};
    var mine = false;
    if (role == 'farmer') {
      params['farmerId'] = userId;
    } else if (role == 'trucker') {
      params['truckerId'] = userId;
    } else {
      params['mine'] = 'true';
      mine = true;
    }
    if (statusFilter != null) params['status'] = statusFilter;
    final data = await _api.get<Map<String, dynamic>>(
      '/deliveries',
      queryParameters: params,
    );
    final all = _parseDeliveries(data);
    // The mine=true query ignores the status GSI, so apply the UI status filter
    // client-side to keep behaviour consistent with a status-scoped query.
    if (mine && statusFilter != null) {
      return all.where((d) => d.status.name == statusFilter).toList();
    }
    return all;
  }

  List<Delivery> _parseDeliveries(Map<String, dynamic> data) {
    final items = data['deliveries'] as List<dynamic>? ??
        data['items'] as List<dynamic>? ??
        [];
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
      ..deliveryType = m['deliveryType'] as String?
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
