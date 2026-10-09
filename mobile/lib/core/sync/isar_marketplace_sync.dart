import 'dart:convert';

import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_status_wire.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';

/// Isar-backed [MarketplaceSync] (mobile only — imports Isar, so it is
/// constructed exclusively from `run_mobile.dart`, never on web).
///
/// Fetches the marketplace from the API (`GET /crops?status=available`,
/// `GET /trips?status=open`, following pagination) and caches it in Isar so
/// the existing offline-first browse + matching queries have data to read.
///
/// Quantity unit note: the server returns crop quantity under the key
/// `quantityKg`, but the value is the quintal figure the farmer entered — no
/// kg/quintal conversion happens anywhere in the stack (the key is a
/// historical misnomer). We therefore store it verbatim into
/// [CropListing.quantity], which the whole app treats as quintals, so
/// matching (`quantity >= quantityNeeded`, both quintals) stays consistent.
class IsarMarketplaceSync implements MarketplaceSync {
  final ApiClient _api;
  final Isar _isar;
  final Future<String?> Function()? _getCurrentUserId;

  /// Skip re-pulling a collection if it was refreshed within this window. A
  /// Buyer browsing then immediately running "Find matches" would otherwise
  /// trigger two back-to-back full network fetches + Isar rewrites, which is
  /// costly on the 2GB-RAM / 3G target devices (CLAUDE.md core principles).
  static const _minRefreshInterval = Duration(seconds: 30);
  DateTime? _lastCropPull;
  DateTime? _lastTripPull;
  DateTime? _lastDeliveryPull;
  DateTime? _lastOrderPull;

  IsarMarketplaceSync({
    required ApiClient api,
    required Isar isar,
    Future<String?> Function()? getCurrentUserId,
  })  : _api = api,
        _isar = isar,
        _getCurrentUserId = getCurrentUserId;

  @override
  Future<void> pullAll() async {
    await pullCrops();
    await pullTrips();
  }

  @override
  Future<void> pullCrops() async {
    if (_isFresh(_lastCropPull)) return;
    final crops = await _fetchCrops();
    final userId = await _currentUserId();

    await _isar.writeTxn(() async {
      await _deleteStaleCrops(userId);
      await _isar.cropListings.putAllByListingId(crops);
    });
    _lastCropPull = DateTime.now();
  }

  @override
  Future<void> pullTrips() async {
    if (_isFresh(_lastTripPull)) return;
    final trips = await _fetchTrips();
    final userId = await _currentUserId();

    await _isar.writeTxn(() async {
      await _deleteStaleTrips(userId);
      await _isar.trips.putAllByTripId(trips);
    });
    _lastTripPull = DateTime.now();
  }

  @override
  Future<void> pullDeliveries() async {
    if (_isFresh(_lastDeliveryPull)) return;
    final userId = await _currentUserId();
    if (userId == null || userId.isEmpty) return;
    final deliveries = await _fetchDeliveries(userId);
    // entityIds of local delivery writes not yet confirmed synced. A buyer who
    // just confirmed delivery offline must not have that overwritten by an older
    // server view before the queued upload lands.
    final pendingIds = await _entityIdsWithPendingUploads();

    await _isar.writeTxn(() async {
      for (final d in deliveries) {
        if (pendingIds.contains(d.deliveryId)) continue;
        final existing = await _isar.deliverys
            .filter()
            .deliveryIdEqualTo(d.deliveryId)
            .findFirst();
        if (existing != null) {
          // Never regress to an older server version than what we already hold.
          if (d.version < existing.version) continue;
          d.id = existing.id;
        }
        await _isar.deliverys.put(d);
      }
    });
    _lastDeliveryPull = DateTime.now();
  }

  @override
  Future<void> pullOrders({required String userId, String? role}) async {
    if (userId.isEmpty) return;
    if (_isFresh(_lastOrderPull)) return;
    final orders = await _fetchOrders(userId, role);
    // entityIds of local order writes not yet confirmed synced — a buyer who
    // just placed/paid offline must not have it overwritten by an older server
    // view before the queued upload lands.
    final pendingIds = await _entityIdsWithPendingUploads();

    await _isar.writeTxn(() async {
      for (final o in orders) {
        if (pendingIds.contains(o.orderId)) continue;
        final existing =
            await _isar.orders.filter().orderIdEqualTo(o.orderId).findFirst();
        if (existing != null) {
          // Never regress to an older server version than what we already hold.
          if (o.version < existing.version) continue;
          o.id = existing.id;
        }
        await _isar.orders.put(o);
      }
    });
    _lastOrderPull = DateTime.now();
  }

  @override
  Future<Order?> pullOrder(String orderId) async {
    if (await _hasOpenUploads('order', orderId)) return null;
    final server = await _getServerOrder(orderId);
    if (server == null || server.orderId != orderId) return null;
    await _isar.writeTxn(() async {
      // A change made while the read was in flight wins over the server copy.
      if (await _hasOpenUploads('order', orderId)) return;
      final existing =
          await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();
      if (existing != null) {
        if (server.version < existing.version) return;
        server.id = existing.id;
      }
      await _isar.orders.put(server);
    });
    return server;
  }

  /// `GET /orders/{id}`: the server's order, or null when the response holds
  /// none. Errors (offline, 404, 403) propagate.
  Future<Order?> _getServerOrder(String orderId) async {
    final data = await _api.get<Map<String, dynamic>>('/orders/$orderId');
    // GET /orders/{id} returns {"order": {...}}; accept `item` and a bare
    // object too, like the web order repository.
    final envelope = data['order'] ?? data['item'];
    final map = envelope is Map<String, dynamic>
        ? envelope
        : (data.containsKey('orderId') ? data : null);
    return map == null ? null : _parseOrder(map);
  }

  /// Undo the local change of an order operation the server refused for
  /// good (the sync executor's `PermanentRejectionHandler`,
  /// sync-protocol.md 1.10.0). Server wins:
  /// - a refused `create` was never stored, so the local order is removed;
  /// - a refused update is replaced by the server's order
  ///   (`GET /orders/{id}`), unless another operation for it is still on its
  ///   way, whose own outcome settles the row; the order is removed when the
  ///   server says it does not exist or this user is not a party (404, 403);
  /// - when that read fails (offline, 401, 5xx) the local version goes back
  ///   to the lowest one its refused operations were based on, so the next
  ///   [pullOrders], which rejected operations no longer block, overwrites
  ///   the row.
  ///
  /// Every path clears the order pull stamp so that pull is not throttled.
  /// A delivery cancel refused after pickup (ESC-08) and a refused or late
  /// delivery dispute (ESC-07) are undone the same way from
  /// `GET /deliveries/{id}`, never removing the delivery. Other entity types
  /// are left alone.
  Future<void> revertRejected(SyncOperation operation, String message) async {
    if (operation.entityType == 'delivery') {
      await _revertRejectedDelivery(operation);
      return;
    }
    if (operation.entityType != 'order') return;
    final orderId = operation.entityId;
    _lastOrderPull = null;

    if (operation.action == 'create') {
      await _deleteLocalOrder(orderId);
      return;
    }
    if (await _hasOpenUploads(operation.entityType, orderId)) return;

    Order? server;
    try {
      server = await _getServerOrder(orderId);
    } on NotFoundException {
      await _deleteLocalOrder(orderId);
      return;
    } on AuthException catch (e) {
      if (e.statusCode == 403) {
        await _deleteLocalOrder(orderId);
        return;
      }
    } on Object {
      // Offline or a server error: fall back to the next pull below.
    }

    final restored = server;
    if (restored == null || restored.orderId != orderId) {
      await _restoreBaseVersion(operation);
      return;
    }
    await _isar.writeTxn(() async {
      // A change made while the read was in flight wins over the restore.
      if (await _hasOpenUploads(operation.entityType, orderId)) return;
      final existing =
          await _isar.orders.filter().orderIdEqualTo(orderId).findFirst();
      // No version guard: the local version is the refused optimistic one.
      if (existing != null) restored.id = existing.id;
      await _isar.orders.put(restored);
    });
  }

  /// ESC-08: the server refused a delivery cancel because the delivery was
  /// picked up meanwhile; ESC-07: it refused a dispute (the payment could not
  /// be frozen, or the 48-hour window had passed), so the delivery is still
  /// `delivered` there. Server wins, as for an order update: the local row
  /// is replaced by `GET /deliveries/{id}` unless another upload for it is on
  /// its way; when that read fails the version goes back for the next
  /// [pullDeliveries].
  Future<void> _revertRejectedDelivery(SyncOperation operation) async {
    final deliveryId = operation.entityId;
    _lastDeliveryPull = null;
    if (await _hasOpenUploads(operation.entityType, deliveryId)) return;

    Delivery? server;
    try {
      final data =
          await _api.get<Map<String, dynamic>>('/deliveries/$deliveryId');
      // GET /deliveries/{id} returns {"delivery": {...}}.
      final envelope = data['delivery'];
      if (envelope is Map<String, dynamic>) server = _parseDelivery(envelope);
    } on Object {
      // Offline, a server error or no longer readable: the next pull decides.
    }

    final restored = server;
    if (restored == null || restored.deliveryId != deliveryId) {
      await _restoreBaseVersion(operation);
      return;
    }
    await _isar.writeTxn(() async {
      // A change made while the read was in flight wins over the restore.
      if (await _hasOpenUploads(operation.entityType, deliveryId)) return;
      final existing = await _isar.deliverys
          .filter()
          .deliveryIdEqualTo(deliveryId)
          .findFirst();
      // No version guard: the local version is the refused optimistic one.
      if (existing != null) restored.id = existing.id;
      await _isar.deliverys.put(restored);
    });
  }

  Future<void> _deleteLocalOrder(String orderId) async {
    await _isar.writeTxn(() async {
      await _isar.orders.filter().orderIdEqualTo(orderId).deleteAll();
    });
  }

  /// Put the local order (or delivery) back on the lowest version any of its
  /// refused operations was based on, so a pull is not skipped by the "never
  /// regress" guard. Each optimistic write bumped the version by one, so two
  /// refused changes (pay, then cancel) leave the row two ahead of the
  /// server, and their rejections may come in different batches. No-op when
  /// a newer change is queued for it.
  Future<void> _restoreBaseVersion(SyncOperation operation) async {
    final ops = await _isar.syncOperations
        .filter()
        .entityTypeEqualTo(operation.entityType)
        .entityIdEqualTo(operation.entityId)
        .findAll();
    int? base;
    for (final op in ops.where(SyncQueue.isRejected)) {
      final version = _sentVersion(op);
      if (version != null && (base == null || version < base)) base = version;
    }
    if (base == null) return;
    final baseVersion = base;
    await _isar.writeTxn(() async {
      if (await _hasOpenUploads(operation.entityType, operation.entityId)) {
        return;
      }
      if (operation.entityType == 'delivery') {
        final local = await _isar.deliverys
            .filter()
            .deliveryIdEqualTo(operation.entityId)
            .findFirst();
        if (local == null || local.version <= baseVersion) return;
        local.version = baseVersion;
        await _isar.deliverys.put(local);
        return;
      }
      final local = await _isar.orders
          .filter()
          .orderIdEqualTo(operation.entityId)
          .findFirst();
      if (local == null || local.version <= baseVersion) return;
      local.version = baseVersion;
      await _isar.orders.put(local);
    });
  }

  /// The pre-change version an update operation sent, if any.
  static int? _sentVersion(SyncOperation op) {
    try {
      final data = jsonDecode(op.data);
      return data is Map ? (data['version'] as num?)?.toInt() : null;
    } on FormatException {
      return null;
    }
  }

  /// Whether an operation for this entity is still on its way to the server
  /// (pending, syncing, or failed but retryable).
  Future<bool> _hasOpenUploads(String entityType, String entityId) async {
    final ops = await _isar.syncOperations
        .filter()
        .entityTypeEqualTo(entityType)
        .entityIdEqualTo(entityId)
        .not()
        .statusEqualTo(SyncStatus.synced)
        .findAll();
    return ops.any((op) => !SyncQueue.isRejected(op));
  }

  bool _isFresh(DateTime? last) =>
      last != null && DateTime.now().difference(last) < _minRefreshInterval;

  /// Drop cached marketplace crops (other users' listings) so sold/withdrawn
  /// ones disappear from browse + matching — but NEVER delete:
  /// - the current user's own rows, nor
  /// - any row that still has an un-synced local write queued (otherwise a
  ///   previous user's offline-created listing on a shared device could be
  ///   wiped before it ever reaches the server).
  /// Skipped entirely when the current user is unknown, to avoid an
  /// unscoped delete-everything.
  Future<void> _deleteStaleCrops(String? userId) async {
    if (userId == null) return;
    final protectedIds = await _entityIdsWithPendingUploads();
    if (protectedIds.isEmpty) {
      // Fast path: bulk-delete via the query without materialising rows.
      await _isar.cropListings
          .filter()
          .not()
          .farmerIdEqualTo(userId)
          .deleteAll();
      return;
    }
    final staleIds = (await _isar.cropListings
            .filter()
            .not()
            .farmerIdEqualTo(userId)
            .findAll())
        .where((c) => !protectedIds.contains(c.listingId))
        .map((c) => c.id)
        .toList();
    await _isar.cropListings.deleteAll(staleIds);
  }

  Future<void> _deleteStaleTrips(String? userId) async {
    if (userId == null) return;
    final protectedIds = await _entityIdsWithPendingUploads();
    if (protectedIds.isEmpty) {
      await _isar.trips.filter().not().truckerIdEqualTo(userId).deleteAll();
      return;
    }
    final staleIds =
        (await _isar.trips.filter().not().truckerIdEqualTo(userId).findAll())
            .where((t) => !protectedIds.contains(t.tripId))
            .map((t) => t.id)
            .toList();
    await _isar.trips.deleteAll(staleIds);
  }

  /// entityIds of local writes not yet confirmed synced to the server
  /// (pending / syncing / failed). These must survive a marketplace refresh.
  /// Operations the server rejected for good never get there, so they do not
  /// protect their row: a pull may overwrite it with the server's view.
  Future<Set<String>> _entityIdsWithPendingUploads() async {
    final ops = await _isar.syncOperations
        .filter()
        .not()
        .statusEqualTo(SyncStatus.synced)
        .findAll();
    return ops
        .where((op) => !SyncQueue.isRejected(op))
        .map((op) => op.entityId)
        .toSet();
  }

  Future<String?> _currentUserId() async {
    final getter = _getCurrentUserId;
    if (getter == null) return null;
    try {
      return await getter();
    } on Object {
      return null;
    }
  }

  Future<List<CropListing>> _fetchCrops() async {
    final results = <CropListing>[];
    String? nextToken;
    do {
      final params = <String, dynamic>{'status': 'available', 'limit': '50'};
      if (nextToken != null) params['nextToken'] = nextToken;
      final data = await _api.get<Map<String, dynamic>>(
        '/crops',
        queryParameters: params,
      );
      final items = data['crops'] as List<dynamic>? ??
          data['items'] as List<dynamic>? ??
          const <dynamic>[];
      for (final item in items) {
        results.add(_parseCrop(item as Map<String, dynamic>));
      }
      nextToken = data['nextToken'] as String?;
    } while (nextToken != null);
    return results;
  }

  Future<List<Trip>> _fetchTrips() async {
    final results = <Trip>[];
    String? nextToken;
    do {
      final params = <String, dynamic>{'status': 'open', 'limit': '50'};
      if (nextToken != null) params['nextToken'] = nextToken;
      final data = await _api.get<Map<String, dynamic>>(
        '/trips',
        queryParameters: params,
      );
      final items = data['trips'] as List<dynamic>? ??
          data['items'] as List<dynamic>? ??
          const <dynamic>[];
      for (final item in items) {
        results.add(_parseTrip(item as Map<String, dynamic>));
      }
      nextToken = data['nextToken'] as String?;
    } while (nextToken != null);
    return results;
  }

  CropListing _parseCrop(Map<String, dynamic> m) {
    return CropListing()
      // API uses "cropId"; sync-created listings echo "listingId".
      ..listingId = m['listingId'] as String? ?? m['cropId'] as String? ?? ''
      ..farmerId = m['farmerId'] as String? ?? ''
      ..farmerName = m['farmerName'] as String? ?? ''
      ..farmerPhone = m['farmerPhone'] as String? ?? ''
      ..cropType = m['cropType'] as String? ?? ''
      // See class doc: the number is quintals regardless of the key name.
      ..quantity = (m['quantity'] as num?)?.toDouble() ??
          (m['quantityKg'] as num?)?.toDouble() ??
          0
      ..pricePerKg = (m['pricePerKg'] as num?)?.toDouble() ?? 0
      ..location = (LatLng()
        ..latitude = (m['latitude'] as num?)?.toDouble() ?? 0
        ..longitude = (m['longitude'] as num?)?.toDouble() ?? 0)
      ..locationName =
          m['locationName'] as String? ?? m['woreda'] as String? ?? ''
      ..description = m['description'] as String? ?? ''
      ..farmerReliability = (m['farmerReliability'] as num?)?.toDouble()
      ..audioNoteRef = m['audioNoteRef'] as String?
      ..status = _parseListingStatus(m['status'] as String?)
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt =
          DateTime.tryParse(m['updatedAt'] as String? ?? '') ?? DateTime.now()
      ..version = (m['version'] as num?)?.toInt() ?? 1;
  }

  ListingStatus _parseListingStatus(String? s) {
    // Server browse status is "available"; the app's active state is "active".
    if (s == 'available') return ListingStatus.active;
    return ListingStatus.values.firstWhere(
      (e) => e.name == s,
      orElse: () => ListingStatus.active,
    );
  }

  Trip _parseTrip(Map<String, dynamic> m) {
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
      ..status = TripStatus.values.firstWhere(
        (e) => e.name == (m['status'] as String? ?? 'open'),
        orElse: () => TripStatus.open,
      )
      ..matchedListings = (m['matchedListings'] as List<dynamic>?)
              ?.map((e) => e as String)
              .toList() ??
          <String>[]
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..updatedAt =
          DateTime.tryParse(m['updatedAt'] as String? ?? '') ?? DateTime.now()
      ..version = (m['version'] as num?)?.toInt() ?? 1;
  }

  Future<List<Delivery>> _fetchDeliveries(String userId) async {
    // Query every party scope and merge: a farmer is not the ParticipantIndex
    // key (that is the buyer) and never authors the delivery, so they only
    // appear under ?farmerId=; a buyer appears under ?mine=true; a trucker under
    // ?truckerId= (TruckerIdCreatedAtIndex, SIM-16). Merging by deliveryId
    // returns the caller's deliveries whichever party they are, without this
    // layer needing to know the role. The scopes stay explicit rather than
    // relying on the server's role default so the pull also works against a
    // lagging deployment.
    final byId = <String, Delivery>{};
    for (final scope in <Map<String, dynamic>>[
      {'farmerId': userId},
      {'mine': 'true'},
      {'truckerId': userId},
    ]) {
      String? nextToken;
      do {
        final params = <String, dynamic>{...scope, 'limit': '50'};
        if (nextToken != null) params['nextToken'] = nextToken;
        final data = await _api.get<Map<String, dynamic>>(
          '/deliveries',
          queryParameters: params,
        );
        final items = data['deliveries'] as List<dynamic>? ??
            data['items'] as List<dynamic>? ??
            const <dynamic>[];
        for (final item in items) {
          final d = _parseDelivery(item as Map<String, dynamic>);
          if (d.deliveryId.isNotEmpty) byId[d.deliveryId] = d;
        }
        nextToken = data['nextToken'] as String?;
      } while (nextToken != null);
    }
    return byId.values.toList();
  }

  Delivery _parseDelivery(Map<String, dynamic> m) {
    final d = Delivery()
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
      ..orderId = m['orderId'] as String?
      // Without it a pulled self-pickup row turns back into a trucker
      // delivery: the buyer's scan button and the pending-to-delivered
      // confirmation both key on it.
      ..deliveryType = m['deliveryType'] as String?
      ..qrNonce = m['qrNonce'] as String?
      ..status = _parseDeliveryStatus(m['status'] as String?)
      ..pickupPhotoRef = m['pickupPhotoRef'] as String?
      ..dropoffPhotoRef = m['dropoffPhotoRef'] as String?
      ..disputeReason = m['disputeReason'] as String?
      ..pickedUpAt = _tryParseUtc(m['pickedUpAt'] as String?)
      ..confirmedAt = _tryParseUtc(m['confirmedAt'] as String?)
      ..createdAt =
          _tryParseUtc(m['createdAt'] as String?) ?? DateTime.now().toUtc()
      ..updatedAt =
          _tryParseUtc(m['updatedAt'] as String?) ?? DateTime.now().toUtc()
      ..version = (m['version'] as num?)?.toInt() ?? 1;

    final gp = m['gpsPickup'];
    if (gp is Map<String, dynamic>) {
      d.gpsPickup = LatLng()
        ..latitude = (gp['latitude'] as num?)?.toDouble() ?? 0
        ..longitude = (gp['longitude'] as num?)?.toDouble() ?? 0;
    }
    final gd = m['gpsDropoff'];
    if (gd is Map<String, dynamic>) {
      d.gpsDropoff = LatLng()
        ..latitude = (gd['latitude'] as num?)?.toDouble() ?? 0
        ..longitude = (gd['longitude'] as num?)?.toDouble() ?? 0;
    }
    return d;
  }

  DeliveryStatus _parseDeliveryStatus(String? s) {
    return DeliveryStatus.values.firstWhere(
      (e) => e.name == s,
      orElse: () => DeliveryStatus.pending,
    );
  }

  /// Fetch the user's orders scoped by their role. A user holds a single role,
  /// so one query suffices (farmer→farmerId, buyer→buyerId, trucker→truckerId);
  /// when the role is unknown, fetch all three and union by orderId.
  Future<List<Order>> _fetchOrders(String userId, String? role) async {
    final fields = switch (role) {
      'farmer' => const ['farmerId'],
      'buyer' => const ['buyerId'],
      'trucker' => const ['truckerId'],
      _ => const ['buyerId', 'farmerId', 'truckerId'],
    };
    final byId = <String, Order>{};
    for (final field in fields) {
      String? nextToken;
      do {
        final params = <String, dynamic>{field: userId, 'limit': '50'};
        if (nextToken != null) params['nextToken'] = nextToken;
        final data = await _api.get<Map<String, dynamic>>(
          '/orders',
          queryParameters: params,
        );
        final items = data['orders'] as List<dynamic>? ??
            data['items'] as List<dynamic>? ??
            const <dynamic>[];
        for (final item in items) {
          final o = _parseOrder(item as Map<String, dynamic>);
          if (o.orderId.isNotEmpty) byId[o.orderId] = o;
        }
        nextToken = data['nextToken'] as String?;
      } while (nextToken != null);
    }
    return byId.values.toList();
  }

  Order _parseOrder(Map<String, dynamic> m) {
    return Order()
      ..orderId = m['orderId'] as String? ?? ''
      ..listingId = m['listingId'] as String? ?? ''
      ..buyerId = m['buyerId'] as String? ?? ''
      ..farmerId = m['farmerId'] as String? ?? ''
      ..buyerName = m['buyerName'] as String? ?? ''
      ..farmerName = m['farmerName'] as String? ?? ''
      ..cropType = m['cropType'] as String? ?? ''
      ..quantity = (m['quantity'] as num?)?.toDouble() ?? 0
      ..pricePerUnit = (m['pricePerUnit'] as num?)?.toDouble() ?? 0
      ..totalPrice = (m['totalPrice'] as num?)?.toDouble() ?? 0
      ..status = _parseOrderStatus(m['status'] as String?)
      ..createdAt =
          DateTime.tryParse(m['createdAt'] as String? ?? '') ?? DateTime.now()
      ..respondedAt = DateTime.tryParse(m['respondedAt'] as String? ?? '')
      ..rejectionReason = m['rejectionReason'] as String?
      ..version = (m['version'] as num?)?.toInt() ?? 1
      ..tripId = m['tripId'] as String?
      ..truckerId = m['truckerId'] as String?
      ..truckerName = m['truckerName'] as String?
      ..cropCost = (m['cropCost'] as num?)?.toDouble()
      ..transportCost = (m['transportCost'] as num?)?.toDouble()
      ..commissionRate = (m['commissionRate'] as num?)?.toDouble()
      ..commissionAmount = (m['commissionAmount'] as num?)?.toDouble()
      ..orderTotal = (m['orderTotal'] as num?)?.toDouble()
      ..paymentMethod = m['paymentMethod'] as String?
      ..cbeTransactionId = m['cbeTransactionId'] as String?
      ..paymentVerifiedAt =
          DateTime.tryParse(m['paymentVerifiedAt'] as String? ?? '')
      ..cancelledAt = DateTime.tryParse(m['cancelledAt'] as String? ?? '')
      ..cancelReason = m['cancelReason'] as String?
      ..updatedAt = DateTime.tryParse(m['updatedAt'] as String? ?? '');
  }

  OrderStatus _parseOrderStatus(String? s) {
    // Server sends order status in snake_case; map it back to the camelCase enum.
    final name = s == null ? null : orderStatusFromWire(s);
    return OrderStatus.values.firstWhere(
      (e) => e.name == name,
      orElse: () => OrderStatus.created,
    );
  }

  DateTime? _tryParseUtc(String? s) {
    if (s == null || s.isEmpty) return null;
    return DateTime.tryParse(s)?.toUtc();
  }
}
