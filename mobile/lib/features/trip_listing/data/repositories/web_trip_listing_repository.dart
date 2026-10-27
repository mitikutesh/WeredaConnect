import 'package:uuid/uuid.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';

/// Web-only trip listing repository.
///
/// All operations use HTTP calls via ApiClient. No Isar dependency.
class WebTripListingRepository extends BaseTripListingRepository {
  static const _uuid = Uuid();
  final ApiClient _api;

  WebTripListingRepository({required ApiClient api}) : _api = api;

  @override
  Future<Trip> createTrip({
    required String truckerId,
    required String truckerName,
    required String truckerPhone,
    required String fromWoreda,
    required String toWoreda,
    required DateTime departureDate,
    required double capacity,
    required double pricePerQuintal,
    String? vehiclePlate,
    String? vehicleDescription,
    String? fromPlaceId,
    String? toPlaceId,
  }) async {
    // UUID like the mobile repositories: the server rejects other id
    // shapes (for example a delivery's tripId must be a UUID).
    final tripId = _uuid.v4();
    final now = DateTime.now().toUtc().toIso8601String();

    final payload = <String, dynamic>{
      'truckerId': truckerId,
      'truckerName': truckerName,
      'truckerPhone': truckerPhone,
      'fromWoreda': fromWoreda,
      'toWoreda': toWoreda,
      'departureDate': departureDate.toUtc().toIso8601String(),
      'capacity': capacity,
      'pricePerQuintal': pricePerQuintal,
      'status': 'open',
      if (vehiclePlate != null) 'vehiclePlate': vehiclePlate,
      if (vehicleDescription != null) 'vehicleDescription': vehicleDescription,
    };

    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'trip',
            'action': 'create',
            'entityId': tripId,
            'timestamp': now,
            'idempotencyKey': 'web_create_$tripId',
            'data': payload,
          },
        ],
      },
    );
    // Surface server-side rejections (e.g. TRIP_UNAUTHORIZED) instead of
    // silently reporting success for a trip that was never persisted.
    ensureSyncSucceeded(response);

    final trip = Trip()
      ..tripId = tripId
      ..truckerId = truckerId
      ..truckerName = truckerName
      ..truckerPhone = truckerPhone
      ..vehiclePlate = vehiclePlate
      ..vehicleDescription = vehicleDescription
      ..fromWoreda = fromWoreda
      ..toWoreda = toWoreda
      ..fromPlaceId = fromPlaceId
      ..toPlaceId = toPlaceId
      ..departureDate = departureDate
      ..capacity = capacity
      ..pricePerQuintal = pricePerQuintal
      ..status = TripStatus.open
      ..matchedListings = []
      ..createdAt = DateTime.now().toUtc()
      ..updatedAt = DateTime.now().toUtc()
      ..version = 1;
    return trip;
  }

  @override
  Future<List<Trip>> getTripsByTrucker(String truckerId) async {
    final data = await _api.get<Map<String, dynamic>>(
      '/trips',
      queryParameters: {'truckerId': truckerId},
    );
    return _parseTrips(data);
  }

  @override
  Future<List<Trip>> getOpenTrips({
    String? fromWoreda,
    String? toWoreda,
  }) async {
    final params = <String, dynamic>{'status': 'open'};
    if (fromWoreda != null) params['fromWoreda'] = fromWoreda;
    if (toWoreda != null) params['toWoreda'] = toWoreda;
    final data = await _api.get<Map<String, dynamic>>(
      '/trips',
      queryParameters: params,
    );
    return _parseTrips(data);
  }

  @override
  Future<void> cancelTrip({
    required String tripId,
    required String truckerId,
  }) async {
    // The server applies a status transition only when the payload's `version`
    // matches the version it currently holds (optimistic concurrency,
    // sync-protocol.md). Web keeps no local copy, so read the current version
    // first — omitting it defaults server-side to 0 and always conflicts,
    // silently leaving the trip open.
    final version = await _currentTripVersion(tripId);
    final now = DateTime.now().toUtc().toIso8601String();
    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'trip',
            'action': 'update',
            'entityId': tripId,
            'timestamp': now,
            'idempotencyKey': 'web_cancel_${tripId}_$now',
            'data': {
              'status': 'cancelled',
              'truckerId': truckerId,
              'version': version,
            },
          },
        ],
      },
    );
    // /sync answers HTTP 200 per operation: a refused or raced cancel must not
    // look like success.
    ensureSyncSucceeded(response);
  }

  @override
  Future<Trip> startTrip({
    required String tripId,
    required String truckerId,
  }) async {
    final version = await _currentTripVersion(tripId);
    final now = DateTime.now().toUtc().toIso8601String();
    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'trip',
            'action': 'update',
            'entityId': tripId,
            'timestamp': now,
            'idempotencyKey': 'web_start_${tripId}_$now',
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

    // Fetch the updated trip from server
    final data = await _api.get<Map<String, dynamic>>('/trips/$tripId');
    return _parseSingleTrip(data);
  }

  @override
  Future<Trip> markDelivered({
    required String tripId,
    required String truckerId,
  }) async {
    final version = await _currentTripVersion(tripId);
    final now = DateTime.now().toUtc().toIso8601String();
    final response = await _api.post<Map<String, dynamic>>(
      '/sync',
      data: {
        'syncBatchId': _uuid.v4(),
        'clientId': 'web',
        'operations': [
          {
            'operationId': _uuid.v4(),
            'entityType': 'trip',
            'action': 'update',
            'entityId': tripId,
            'timestamp': now,
            'idempotencyKey': 'web_deliver_${tripId}_$now',
            'data': {
              'status': 'delivered',
              'truckerId': truckerId,
              'version': version,
            },
          },
        ],
      },
    );
    ensureSyncSucceeded(response);

    final data = await _api.get<Map<String, dynamic>>('/trips/$tripId');
    return _parseSingleTrip(data);
  }

  /// Read a trip's current server version so status-transition syncs pass the
  /// server's optimistic-concurrency check. Falls back to version 1 (the value
  /// forced on create) if the field is missing.
  Future<int> _currentTripVersion(String tripId) async {
    final data = await _api.get<Map<String, dynamic>>('/trips/$tripId');
    return _parseSingleTrip(data).version;
  }

  @override
  Future<List<({String from, String to})>> getRecentRoutes(
    String truckerId, {
    int limit = 5,
  }) async {
    // Same request as getMostRelevantTrip and the Home dashboard so the
    // concurrent reads coalesce into one call (ApiClient, SIM-21); the
    // route limit is applied client-side.
    final data = await _api.get<Map<String, dynamic>>(
      '/trips',
      queryParameters: {'truckerId': truckerId, 'limit': '50'},
    );
    final trips = _parseTrips(data);
    final seen = <String>{};
    final routes = <({String from, String to})>[];
    for (final trip in trips) {
      final key = '${trip.fromWoreda}->${trip.toWoreda}';
      if (seen.add(key)) {
        routes.add((from: trip.fromWoreda, to: trip.toWoreda));
      }
      if (routes.length >= limit) break;
    }
    return routes;
  }

  @override
  Future<Trip?> getMostRelevantTrip(String truckerId) async {
    // Do NOT filter to status=open: the trucker's hero card must also reflect
    // matched/inTransit/delivered trips. Fetch the trucker's trips and apply the
    // same priority the mobile repo uses (inTransit > matched > open > most recent).
    final data = await _api.get<Map<String, dynamic>>(
      '/trips',
      queryParameters: {'truckerId': truckerId, 'limit': '50'},
    );
    final trips = _parseTrips(data);
    if (trips.isEmpty) return null;
    // Exactly the mobile priority: inTransit > matched > open > delivered.
    // 'cancelled' is deliberately excluded: the mobile repo never surfaces a
    // cancelled trip in the hero card. The server stops listing cancelled
    // trips with SIM-31, but an older deployment still returns them (they carry
    // no deletedAt), and a bare "most recent" fallback would then let a
    // cancelled trip win over a delivered one.
    for (final status in [
      TripStatus.inTransit,
      TripStatus.matched,
      TripStatus.open,
      TripStatus.delivered,
    ]) {
      final matches = trips.where((t) => t.status == status).toList()
        ..sort((a, b) => b.updatedAt.compareTo(a.updatedAt));
      if (matches.isNotEmpty) return matches.first;
    }
    return null;
  }

  @override
  Future<Trip?> getTripById(String tripId) async {
    try {
      final data = await _api.get<Map<String, dynamic>>('/trips/$tripId');
      return _parseSingleTrip(data);
    } on Exception {
      return null;
    }
  }

  List<Trip> _parseTrips(Map<String, dynamic> data) {
    final items = data['trips'] as List<dynamic>? ??
        data['items'] as List<dynamic>? ??
        [];
    return items
        .map((item) => _parseTripMap(item as Map<String, dynamic>))
        .toList();
  }

  Trip _parseSingleTrip(Map<String, dynamic> m) {
    // Server wraps the single-GET response as {"trip": {...}} (GetTripResponse,
    // camelCase). Handling only the bare/`item` shapes threw a TypeError on the
    // real response, breaking every web trip status transition (start/deliver/cancel
    // all read the current version through here first).
    if (m['trip'] is Map<String, dynamic>) {
      return _parseTripMap(m['trip'] as Map<String, dynamic>);
    }
    // Or the trip at the top level.
    if (m.containsKey('tripId')) return _parseTripMap(m);
    // Or nested under 'item'.
    if (m['item'] is Map<String, dynamic>) {
      return _parseTripMap(m['item'] as Map<String, dynamic>);
    }
    // Unknown shape: return a safe empty trip (version defaults to 1) rather
    // than throwing.
    return _parseTripMap(const <String, dynamic>{});
  }

  Trip _parseTripMap(Map<String, dynamic> m) {
    return Trip()
      ..tripId = m['tripId'] as String? ?? ''
      ..truckerId = m['truckerId'] as String? ?? ''
      ..truckerName = m['truckerName'] as String? ?? ''
      ..truckerPhone = m['truckerPhone'] as String? ?? ''
      ..vehiclePlate = m['vehiclePlate'] as String?
      ..vehicleDescription = m['vehicleDescription'] as String?
      ..fromWoreda = m['fromWoreda'] as String? ?? ''
      ..toWoreda = m['toWoreda'] as String? ?? ''
      ..departureDate =
          DateTime.tryParse(m['departureDate'] as String? ?? '') ??
              DateTime.now()
      ..capacity = (m['capacity'] as num?)?.toDouble() ?? 0
      ..pricePerQuintal = (m['pricePerQuintal'] as num?)?.toDouble() ?? 0
      ..bookedQuintals = quintalsFromWire(m['bookedQuintals'])
      ..remainingQuintals = quintalsFromWire(m['remainingQuintals'])
      ..status = _parseTripStatus(m['status'] as String?)
      ..matchedListings = (m['matchedListings'] as List<dynamic>?)
              ?.map((e) => e as String)
              .toList() ??
          []
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt =
          DateTime.tryParse(m['updatedAt'] as String? ?? '') ?? DateTime.now()
      ..version = (m['version'] as num?)?.toInt() ?? 1;
  }

  TripStatus _parseTripStatus(String? s) {
    return TripStatus.values.firstWhere(
      (e) => e.name == s,
      orElse: () => TripStatus.open,
    );
  }
}
