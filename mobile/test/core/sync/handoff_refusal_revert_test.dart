import 'dart:async';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/message.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/isar_marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/delivery_repository.dart';

import '../../helpers/wait_for.dart';

class _OnlineSource implements ConnectivitySource {
  // Closed in tearDown.
  // ignore: close_sinks
  final controller = StreamController<List<ConnectivityResult>>.broadcast();

  @override
  Stream<List<ConnectivityResult>> get onConnectivityChanged =>
      controller.stream;

  @override
  Future<List<ConnectivityResult>> checkConnectivity() async =>
      [ConnectivityResult.wifi];
}

class _MockApiClient extends Mock implements ApiClient {}

const _deliveryId = '7c9e6679-7425-40de-944b-e07fc1f90ae7';

/// FUP-439 on the phone, the whole chain: the trucker's pickup is queued with
/// the drop-off code registration the scan screen queues behind it (ESC-35),
/// the server refuses the pickup for its GPS and holds the registration back
/// while the delivery is pending. The queue, the revert and the delivery row
/// together must end with the server's `pending` on the phone, nothing left
/// to resend and nothing counted as "Sync failed". Before FUP-439 the code
/// was retried; with it permanent but the registration still waiting, the
/// revert skipped the delivery and the phone kept `pickedUp`.
void main() {
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late DeliveryRepository deliveries;
  late _OnlineSource source;
  late Dio dio;
  late DioAdapter adapter;
  late ConnectivityMonitor monitor;
  late SecurityEvents events;
  late List<SecurityEvent> emitted;
  late StreamSubscription<SecurityEvent> sub;
  late _MockApiClient api;
  late SyncExecutor executor;
  late int syncPosts;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('handoff_refusal_test_');
    isar = await Isar.open(
      [
        OrderSchema,
        DeliverySchema,
        SyncOperationSchema,
        MessageSchema,
        CropListingSchema,
        TripSchema,
      ],
      directory: tempDir.path,
      name: 'handoff_refusal_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    deliveries = DeliveryRepository(isar: isar, syncQueue: queue);
    source = _OnlineSource();
    dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
    adapter = DioAdapter(dio: dio);
    syncPosts = 0;
    dio.interceptors.add(
      InterceptorsWrapper(
        onRequest: (options, handler) {
          if (options.path == '/sync') syncPosts++;
          handler.next(options);
        },
      ),
    );
    monitor = ConnectivityMonitor(source: source, dio: dio);
    events = SecurityEvents.forTesting();
    emitted = [];
    sub = events.events.listen(emitted.add);
    api = _MockApiClient();
    executor = SyncExecutor(
      syncQueue: queue,
      connectivityMonitor: monitor,
      dio: dio,
      clientId: 'mobile-1',
      securityEvents: events,
      onPermanentRejection:
          IsarMarketplaceSync(api: api, isar: isar).revertRejected,
    );

    await isar.writeTxn(
      () => isar.deliverys.put(
        Delivery()
          ..deliveryId = _deliveryId
          ..tripId = 'trip-1'
          ..listingId = 'listing-1'
          ..farmerId = 'farmer-1'
          ..farmerName = 'Farmer'
          ..truckerId = 'trucker-1'
          ..truckerName = 'Trucker'
          ..buyerId = 'buyer-1'
          ..buyerName = 'Buyer'
          ..qrToken = 'qr'
          ..status = DeliveryStatus.pending
          ..createdAt = DateTime.utc(2026, 11, 12)
          ..updatedAt = DateTime.utc(2026, 11, 12)
          ..version = 1,
      ),
    );
    when(() => api.get<Map<String, dynamic>>('/deliveries/$_deliveryId'))
        .thenAnswer(
      (_) async => {
        'delivery': {
          'deliveryId': _deliveryId,
          'farmerId': 'farmer-1',
          'truckerId': 'trucker-1',
          'buyerId': 'buyer-1',
          'status': 'pending',
          'version': 1,
        },
      },
    );
  });

  tearDown(() async {
    await sub.cancel();
    executor.dispose();
    monitor.dispose();
    await source.controller.close();
    queue.dispose();
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  Future<Delivery> loadDelivery() async => (await isar.deliverys
      .filter()
      .deliveryIdEqualTo(_deliveryId)
      .findFirst())!;

  test(
      "a pickup refused for its GPS puts the trucker's phone back to pending, "
      'with the drop-off code behind it', () async {
    await deliveries.confirmPickup(
      deliveryId: _deliveryId,
      truckerId: 'trucker-1',
      gpsPickup: LatLng()
        ..latitude = 9.1
        ..longitude = 38.7,
      scannedQrNonce: 'n',
    );
    // The scan screen's PickupConfirmed queues the trucker's drop-off code.
    await deliveries.persistDropoffQrNonce(
      deliveryId: _deliveryId,
      nonce: 'd.abc',
      truckerId: 'trucker-1',
    );
    expect((await loadDelivery()).status, DeliveryStatus.pickedUp);

    final ops = await queue.waitingOperationsFor('delivery', _deliveryId);
    expect(ops, hasLength(2));
    final pickup = ops.firstWhere((op) => op.data.contains('pickedUp'));
    final registration =
        ops.firstWhere((op) => op.data.contains('dropoffQrNonce'));
    const refusal = 'DELIVERY_GPS_PROXIMITY: pickup_far: Pickup GPS is 11.1km '
        'from listing location (max 5km).';
    adapter.onPost(
      '/sync',
      (server) => server.reply(200, {
        'syncBatchId': 'b',
        'results': [
          {
            'operationId': pickup.operationId,
            'status': 'error',
            'message': refusal,
          },
          {
            'operationId': registration.operationId,
            'status': 'deferred',
            'message': 'DELIVERY_NONCE_PENDING: Waiting for the pickup to '
                'sync. Will retry automatically.',
          },
        ],
      }),
      data: Matchers.any,
    );

    await executor.executeBatch();
    await waitFor(() => emitted.whereType<SyncOperationRejected>().isNotEmpty);

    final delivery = await loadDelivery();
    expect(delivery.status, DeliveryStatus.pending);
    expect(delivery.version, 1);
    expect(await queue.waitingOperationsFor('delivery', _deliveryId), isEmpty);
    expect(await queue.getFailedCount(), 0);
    expect(
      emitted.whereType<SyncOperationRejected>().single.message,
      refusal,
    );
    // A reconnect sends nothing.
    await executor.syncNow();
    expect(syncPosts, 1);
  });
}
