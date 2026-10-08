import 'package:uuid/uuid.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
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
    // UUID like the mobile repositories: the server rejects other id
    // shapes (for example a delivery's tripId must be a UUID).
    final deliveryId = _uuid.v4();
    final now = DateTime.now().toUtc().toIso8601String();

    final response = await _api.post<Map<String, dynamic>>(
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
              'orderId': orderId,
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
    // A rejected op must not leave a phantom local-only delivery.
    ensureSyncSucceeded(response);

    return Delivery()
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
    // UUID like the mobile repositories: the server rejects other id
    // shapes (for example a delivery's tripId must be a UUID).
    final deliveryId = _uuid.v4();
    final now = DateTime.now().toUtc().toIso8601String();

    final response = await _api.post<Map<String, dynamic>>(
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
    ensureSyncSucceeded(response);

    return Delivery()
      ..deliveryId = deliveryId
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
      ..deliveryType = 'self_pickup'
      ..status = DeliveryStatus.pending
      ..createdAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc()
      ..version = 1;
  }

  /// Registers the farmer's QR nonce with the server: a `/sync` delivery
  /// update carrying only `qrNonce` (the server moves it to the QR secret
  /// store). Web has no sync queue, so without this a QR shown on web could
  /// never be confirmed: the scan would stay deferred until the nonce
  /// arrives. Throws when the server refuses it, so the QR screen keeps the
  /// code hidden instead of showing one that cannot work.
  @override
  Future<void> persistQrNonce({
    required String deliveryId,
    required String qrNonce,
    required String userId,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    final response = await _api.post<Map<String, dynamic>>(
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
            'idempotencyKey': 'web_qr_nonce_${deliveryId}_$now',
            'data': {
              'qrNonce': qrNonce,
              'version': await _currentVersion(deliveryId),
            },
          },
        ],
      },
    );
    ensureSyncSucceeded(response);
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
    // Server upload keys only (the server refuses any other shape and would
    // fail the whole pickup); same rule as the mobile repository.
    if (photoRef != null &&
        DeliveryConstants.isServerPhotoRef(photoRef, deliveryId)) {
      data['pickupPhotoRef'] = photoRef;
    }
    if (scannedQrNonce != null) data['scannedQrNonce'] = scannedQrNonce;

    final response = await _api.post<Map<String, dynamic>>(
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
    ensureSyncSucceeded(response);
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
    if (photoRef != null &&
        DeliveryConstants.isServerPhotoRef(photoRef, deliveryId)) {
      data['dropoffPhotoRef'] = photoRef;
    }
    if (scannedQrNonce != null) data['scannedQrNonce'] = scannedQrNonce;

    final response = await _api.post<Map<String, dynamic>>(
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
    ensureSyncSucceeded(response);
  }

  @override
  Future<void> markInTransit({
    required String deliveryId,
    required String truckerId,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    final version = await _currentVersion(deliveryId);
    final response = await _api.post<Map<String, dynamic>>(
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
    ensureSyncSucceeded(response);
  }

  @override
  Future<void> raiseDispute({
    required String deliveryId,
    required String userId,
    required String reason,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    final version = await _currentVersion(deliveryId);
    final response = await _api.post<Map<String, dynamic>>(
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
    ensureSyncSucceeded(response);
  }

  @override
  Future<void> cancelDelivery({
    required String deliveryId,
    required String userId,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    final version = await _currentVersion(deliveryId);
    final response = await _api.post<Map<String, dynamic>>(
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
    ensureSyncSucceeded(response);
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
    final params = _participantParams(userId, role);
    final mine = params.containsKey('mine');
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

  /// Query keys that scope `GET /deliveries` to the user's own deliveries:
  /// the farmer and trucker GSIs, else the buyer's participant ("mine") query.
  Map<String, dynamic> _participantParams(String userId, String? role) {
    if (role == 'farmer') return {'farmerId': userId};
    if (role == 'trucker') return {'truckerId': userId};
    return {'mine': 'true'};
  }

  /// Most pages [findActiveDeliveryForOrder] reads (50 deliveries each).
  static const _maxOrderLookupPages = 10;

  /// Slack for clock skew between the order's and the deliveries' timestamps
  /// before the lookup gives up on older pages.
  static const _orderClockSkew = Duration(days: 1);

  /// Pages through the user's deliveries (newest first) until the order's
  /// live delivery turns up. `GET /deliveries` has no order filter and returns
  /// 20 by default, so reading one page missed it for busy users. A delivery
  /// cannot predate its order, so paging stops at the first page that
  /// reaches deliveries older than [orderCreatedAt]: on 3G the usual case
  /// (no delivery yet) costs one request, not ten.
  @override
  Future<Delivery?> findActiveDeliveryForOrder(
    String orderId, {
    required String userId,
    required String role,
    DateTime? orderCreatedAt,
  }) async {
    final cutoff = orderCreatedAt?.toUtc().subtract(_orderClockSkew);
    final params = _participantParams(userId, role)..['limit'] = 50;
    for (var page = 0; page < _maxOrderLookupPages; page++) {
      final data = await _api.get<Map<String, dynamic>>(
        '/deliveries',
        queryParameters: params,
      );
      final rows = _parseDeliveries(data);
      for (final d in rows) {
        if (d.orderId == orderId && d.status != DeliveryStatus.cancelled) {
          return d;
        }
      }
      if (cutoff != null &&
          rows.isNotEmpty &&
          rows.last.createdAt.toUtc().isBefore(cutoff)) {
        return null;
      }
      final next = data['nextToken'] as String?;
      if (next == null || next.isEmpty) return null;
      params['nextToken'] = next;
    }
    return null;
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
      ..orderId = m['orderId'] as String?
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
