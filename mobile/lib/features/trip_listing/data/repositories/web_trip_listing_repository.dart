import 'package:uuid/uuid.dart';
import 'package:wereda_connect/core/models/trip.dart';
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
  }) async {
    final tripId =
        'trp_${DateTime.now().millisecondsSinceEpoch}_${truckerId.hashCode.abs()}';
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
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
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
            'data': {'status': 'cancelled', 'truckerId': truckerId},
          },
        ],
      },
    );
  }

  @override
  Future<Trip> startTrip({
    required String tripId,
    required String truckerId,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
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
            'data': {'status': 'inTransit', 'truckerId': truckerId},
          },
        ],
      },
    );

    // Fetch the updated trip from server
    final data = await _api.get<Map<String, dynamic>>('/trips/$tripId');
    return _parseSingleTrip(data);
  }

  @override
  Future<Trip> markDelivered({
    required String tripId,
    required String truckerId,
  }) async {
    final now = DateTime.now().toUtc().toIso8601String();
    await _api.post<dynamic>(
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
            'data': {'status': 'delivered', 'truckerId': truckerId},
          },
        ],
      },
    );

    final data = await _api.get<Map<String, dynamic>>('/trips/$tripId');
    return _parseSingleTrip(data);
  }

  @override
  Future<List<({String from, String to})>> getRecentRoutes(
    String truckerId, {
    int limit = 5,
  }) async {
    final data = await _api.get<Map<String, dynamic>>(
      '/trips',
      queryParameters: {'truckerId': truckerId, 'limit': '$limit'},
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
    final data = await _api.get<Map<String, dynamic>>(
      '/trips',
      queryParameters: {
        'truckerId': truckerId,
        'status': 'open',
        'limit': '1',
      },
    );
    final trips = _parseTrips(data);
    return trips.isEmpty ? null : trips.first;
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
    // Single trip response may have the trip at the top level
    if (m.containsKey('tripId')) return _parseTripMap(m);
    // Or nested under 'item'
    return _parseTripMap(m['item'] as Map<String, dynamic>);
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
