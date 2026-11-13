import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http_mock_adapter/http_mock_adapter.dart' as mock;
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/message.dart';
import 'package:wereda_connect/core/database/schemas/order.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/order_dropoff_check_in.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/core/sync/delivery_photo_rules.dart';
import 'package:wereda_connect/core/sync/dropoff_check_in_preflight.dart';
import 'package:wereda_connect/core/sync/handoff_check_in_rules.dart';
import 'package:wereda_connect/core/sync/isar_marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/core/sync/sync_queue.dart';
import 'package:wereda_connect/features/order/data/repositories/order_repository.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

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

const _deliveryId = '6f1c2a4e-1111-4b2c-9d3e-0a1b2c3d4e5f';
const _photoPath = '/data/user/0/app/photos/$_deliveryId/arrival-1700000.webp';
const _serverKey = 'uploads/photos/$_deliveryId/arrival-1700000.webp';
const _photoEntity = '$_deliveryId#arrival';
const _checkInEntity = 'ord-1#arrived_dropoff';

const _fix =
    HandoffCheckInFix(latitude: 9.031, longitude: 38.74, accuracyM: 20);

/// ESC-36 on the phone (FUP-336): the arrival photo is queued first and the
/// drop-off check-in waits for it in the same sync run; the check-in goes
/// with exactly the server's fields; every refusal is permanent, takes the
/// mark back, reads the order again and is said with the drop-off texts;
/// `CHECKIN_PHOTO_REQUIRED` forgets the photo the phone kept.
void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  late Isar isar;
  late Directory tempDir;
  late SyncQueue queue;
  late OrderRepository repo;
  late _OnlineSource source;
  late Dio dio;
  late mock.DioAdapter adapter;
  late ConnectivityMonitor monitor;
  late SecurityEvents events;
  late List<SecurityEvent> emitted;
  late StreamSubscription<SecurityEvent> sub;
  late _MockApiClient api;
  late SyncExecutor executor;
  late List<List<Map<String, dynamic>>> batches;
  late Map<String, Map<String, Object>> answers;
  late bool photoReady;
  late Map<String, dynamic> serverOrder;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('order_dropoff_test_');
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
      name: 'order_dropoff_${DateTime.now().microsecondsSinceEpoch}',
    );
    queue = SyncQueue(isar);
    repo = OrderRepository(isar: isar, syncQueue: queue);
    source = _OnlineSource();
    dio = Dio(BaseOptions(baseUrl: 'https://api.test.com'));
    adapter = mock.DioAdapter(dio: dio);
    monitor = ConnectivityMonitor(source: source, dio: dio);
    events = SecurityEvents.forTesting();
    emitted = [];
    sub = events.events.listen(emitted.add);
    api = _MockApiClient();
    batches = [];
    answers = {};
    photoReady = true;
    serverOrder = _serverOrder();
    when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
        .thenAnswer((_) async => {'order': serverOrder});
    final marketplace = IsarMarketplaceSync(api: api, isar: isar);
    final dropoffPreflight = DropoffCheckInSyncPreflight(
      waiting: queue.hasWaitingOperationsFor,
    );
    executor = SyncExecutor(
      syncQueue: queue,
      connectivityMonitor: monitor,
      dio: dio,
      clientId: 'mobile-1',
      securityEvents: events,
      onPermanentRejection: marketplace.revertRejected,
      // The photo's upload (FRD-12) stands in: ready, or still uploading.
      preflight: (op) async =>
          (op.entityType != deliveryPhotoEntityType || photoReady) &&
          await dropoffPreflight(op),
    );
    adapter.onGet('/health', (server) => server.reply(200, {'status': 'ok'}));
    adapter.onPost(
      '/sync',
      (server) => server.replyCallback(200, (options) {
        final body = Map<String, dynamic>.from(options.data as Map);
        final ops = (body['operations'] as List).cast<Map<String, dynamic>>();
        batches.add(ops);
        return {
          'syncBatchId': body['syncBatchId'],
          'results': [
            for (final op in ops)
              {
                'operationId': op['operationId'],
                ...answers[op['entityType']] ??
                    const {'status': 'success', 'serverVersion': 6},
              },
          ],
        };
      }),
      data: mock.Matchers.any,
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

  /// An order on the road, stamped for the drop-off judgement, T8 a day on.
  Future<void> putOrder({Duration deadlineIn = const Duration(days: 1)}) {
    final now = DateTime.now().toUtc();
    return isar.writeTxn(() async {
      await isar.orders.filter().orderIdEqualTo('ord-1').deleteAll();
      await isar.orders.put(
        Order()
          ..orderId = 'ord-1'
          ..listingId = 'listing-1'
          ..buyerId = 'buyer-1'
          ..farmerId = 'farmer-1'
          ..buyerName = 'Buyer'
          ..farmerName = 'Farmer'
          ..cropType = 'teff'
          ..quantity = 500
          ..pricePerUnit = 60
          ..totalPrice = 30000
          ..status = OrderStatus.inTransit
          ..createdAt = now.subtract(const Duration(days: 4))
          ..version = 6
          ..tripId = 'trip-1'
          ..truckerId = 'trucker-1'
          ..fulfillment = 'trucker'
          ..dropoffJudgement = DropoffJudgementOutcome.pending
          ..dropoffDeadlineAt = now.add(deadlineIn),
      );
    });
  }

  Future<void> putDelivery({String? arrivalPhotoRef}) =>
      isar.writeTxn(() async {
        final now = DateTime.now().toUtc();
        await isar.deliverys.put(
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
            ..qrToken = ''
            ..orderId = 'ord-1'
            ..status = DeliveryStatus.inTransit
            ..arrivalPhotoRef = arrivalPhotoRef
            ..createdAt = now
            ..updatedAt = now
            ..version = 3,
        );
      });

  Future<Delivery> delivery() async => (await isar.deliverys
      .filter()
      .deliveryIdEqualTo(_deliveryId)
      .findFirst())!;

  Future<List<SyncOperation>> ops(String entityType) =>
      isar.syncOperations.filter().entityTypeEqualTo(entityType).findAll();

  Future<bool> checkIn({String userId = 'trucker-1', String? photo}) =>
      repo.checkInAtDropoff(
        'ord-1',
        userId,
        deliveryId: _deliveryId,
        fix: _fix,
        arrivalPhotoRef: photo,
      );

  /// The texts the app shows for the refusal events.
  List<String> rejectionTexts() => [
        for (final event in emitted.whereType<SyncOperationRejected>())
          localizeErrorCode(
            syncRejectionErrorCode(
              entityType: event.entityType,
              action: event.action,
              message: event.message,
              entityId: event.entityId,
            ),
            en,
          ),
      ];

  test('the photo goes first, and the check-in follows in the same run',
      () async {
    await putOrder();
    await putDelivery();

    expect(await checkIn(photo: _photoPath), isTrue);

    final photoOp = (await ops(deliveryPhotoEntityType)).single;
    expect(photoOp.entityId, _photoEntity);
    expect(jsonDecode(photoOp.data), {'_localPath': _photoPath});
    final checkInOp = (await ops(handoffCheckInEntityType)).single;
    expect(checkInOp.entityId, _checkInEntity);
    expect(checkInOp.createdAt.isAfter(photoOp.createdAt), isTrue);
    expect(
      jsonDecode(checkInOp.data)[handoffCheckInDeliveryIdKey],
      _deliveryId,
    );
    // The photo shows on the phone; the order shows the waiting check-in.
    expect((await delivery()).arrivalPhotoRef, _photoPath);
    final order = (await repo.getOrderById('ord-1'))!;
    expect(order.version, 6);
    expect(order.queuedCheckIns[CheckInKind.arrivedDropoff]!.sent, isFalse);

    await executor.syncNow();

    expect(batches, hasLength(2));
    expect(batches[0].single['entityType'], deliveryPhotoEntityType);
    final sent = batches[1].single;
    expect(sent['entityType'], handoffCheckInEntityType);
    expect(sent['entityId'], _checkInEntity);
    expect(sent['action'], 'create');
    final data = sent['data'] as Map<String, dynamic>;
    expect(data.keys.toSet(), {
      'orderId',
      'kind',
      'capturedAt',
      'latitude',
      'longitude',
      'accuracyM',
    });
    expect(data['kind'], 'arrived_dropoff');
    expect(data['latitude'], 9.031);
    final after = (await repo.getOrderById('ord-1'))!;
    expect(after.queuedCheckIns[CheckInKind.arrivedDropoff]!.sent, isTrue);
  });

  test('the check-in waits while the photo cannot go yet', () async {
    await putOrder();
    await putDelivery();
    await checkIn(photo: _photoPath);
    photoReady = false;

    final held = await executor.syncNow();

    expect(batches, isEmpty);
    expect(held.deferred, greaterThan(0));
    expect(
      (await ops(handoffCheckInEntityType)).single.status,
      SyncStatus.pending,
    );

    photoReady = true;
    await executor.syncNow();
    expect(
      [for (final batch in batches) batch.single['entityType']],
      [deliveryPhotoEntityType, handoffCheckInEntityType],
    );
  });

  test('a delivery with its photo gets the check-in alone', () async {
    await putOrder();
    await putDelivery(arrivalPhotoRef: _serverKey);

    await checkIn();

    expect(await ops(deliveryPhotoEntityType), isEmpty);
    await executor.executeBatch();
    expect(batches.single.single['entityType'], handoffCheckInEntityType);
    expect((await delivery()).arrivalPhotoRef, _serverKey);
  });

  test('a photo already waiting is not queued twice; a repeat sends nothing',
      () async {
    await putOrder();
    await putDelivery();
    await queue.enqueue(
      userId: 'trucker-1',
      entityType: deliveryPhotoEntityType,
      entityId: _photoEntity,
      action: 'create',
      data: jsonEncode({deliveryPhotoLocalPathKey: _photoPath}),
      operationId: 'op-photo',
    );

    expect(await checkIn(photo: '${_photoPath}x'), isTrue);
    expect(await checkIn(photo: _photoPath), isFalse);

    expect(await ops(deliveryPhotoEntityType), hasLength(1));
    expect(await ops(handoffCheckInEntityType), hasLength(1));
  });

  test('refused on the phone: the wrong party, no fix, T8 passed', () async {
    await putOrder();
    await putDelivery();
    Matcher refused(AppErrorCode code) => throwsA(
          isA<CheckInNotAllowedException>().having((e) => e.code, 'code', code),
        );

    await expectLater(
      checkIn(userId: 'buyer-1', photo: _photoPath),
      refused(AppErrorCode.dropoffCheckInNotParty),
    );
    await expectLater(
      repo.checkInAtDropoff(
        'ord-1',
        'trucker-1',
        deliveryId: _deliveryId,
        fix: null,
        arrivalPhotoRef: _photoPath,
      ),
      refused(AppErrorCode.dropoffCheckInNeedsLocation),
    );
    await putOrder(deadlineIn: const Duration(minutes: -1));
    await expectLater(
      checkIn(photo: _photoPath),
      refused(AppErrorCode.dropoffCheckInClosed),
    );

    expect(await ops(deliveryPhotoEntityType), isEmpty);
    expect(await ops(handoffCheckInEntityType), isEmpty);
    expect((await delivery()).arrivalPhotoRef, isNull);
  });

  group('every refusal is permanent and said with the drop-off texts', () {
    const tooFar = 'CHECKIN_TOO_FAR: You are 7.2 km from the drop-off '
        '(at most 5 km).';
    final cases = <String, String>{
      'CHECKIN_PHOTO_REQUIRED: Take the arrival photo first, then check in '
          'at the drop-off.': en.errorCheckInPhotoRequired,
      tooFar: en.errorDropoffCheckInTooFar,
      '$tooFar The attempt was kept for review.':
          en.errorDropoffCheckInTooFarKept,
      'CHECKIN_TOO_LATE: The delivery deadline passed before this check-in '
              'reached the server. It was kept for review.':
          en.errorDropoffCheckInTooLate,
      'CHECKIN_NOT_OPEN: This delivery is no longer on the road.':
          en.errorDropoffCheckInNotOpen,
      "CHECKIN_NOT_PARTY: Only the order's trucker checks in at the drop-off.":
          en.errorDropoffCheckInNotParty,
      'CHECKIN_VALIDATION: arrived_dropoff is not taken yet.':
          en.errorCheckInValidation,
    };

    for (final MapEntry(key: message, value: text) in cases.entries) {
      final kept = message.endsWith(checkInAttemptKeptSuffix);
      test('${message.split(':').first}${kept ? ' (attempt kept)' : ''}',
          () async {
        await putOrder();
        await putDelivery(arrivalPhotoRef: _serverKey);
        await checkIn();
        answers[handoffCheckInEntityType] = {
          'status': 'error',
          'message': message,
        };

        await executor.executeBatch();

        final op = (await ops(handoffCheckInEntityType)).single;
        expect(SyncQueue.isRejected(op), isTrue, reason: 'never retried');
        expect(await queue.getFailedCount(), 0);
        expect(rejectionTexts(), [text]);
        // The mark went back: the button is offered again (closer, or
        // after the photo).
        final order = (await repo.getOrderById('ord-1'))!;
        expect(order.queuedCheckIns, isEmpty);
        expect(
          dropoffCheckInChanceFor(order, 'trucker-1', DateTime.now().toUtc()),
          CheckInChance.open,
        );
        // Read again from the server.
        verify(() => api.get<Map<String, dynamic>>('/orders/ord-1')).called(1);
        // The server key of the photo stays.
        expect((await delivery()).arrivalPhotoRef, _serverKey);
        expect(await queue.retryFailed(), 0);
      });
    }
  });

  test('CHECKIN_PHOTO_REQUIRED forgets the photo the phone kept', () async {
    await putOrder();
    // The phone kept a photo whose upload was given up (rejected).
    await putDelivery(arrivalPhotoRef: _photoPath);
    await checkIn();
    answers[handoffCheckInEntityType] = {
      'status': 'error',
      'message': 'CHECKIN_PHOTO_REQUIRED: Take the arrival photo first.',
    };

    await executor.executeBatch();

    expect((await delivery()).arrivalPhotoRef, isNull);
    expect(rejectionTexts(), [en.errorCheckInPhotoRequired]);
  });

  test(
      'queued offline before T8, synced after it: the late text, and the '
      'order shows the late check-in', () async {
    await putOrder();
    await putDelivery(arrivalPhotoRef: _serverKey);
    await checkIn();
    answers[handoffCheckInEntityType] = {
      'status': 'error',
      'message': 'CHECKIN_TOO_LATE: The delivery deadline passed before this '
          'check-in reached the server. It was kept for review.',
    };
    serverOrder = _serverOrder(
      checkIns: {
        'arrived_dropoff': {
          'receivedAt': DateTime.now().toUtc().toIso8601String(),
          'distanceChecked': true,
          'late': true,
        },
      },
    );

    await executor.executeBatch();

    expect(rejectionTexts(), [en.errorDropoffCheckInTooLate]);
    final order = (await repo.getOrderById('ord-1'))!;
    expect(order.queuedCheckIns, isEmpty);
    expect(dropoffCheckInOf(order)!.isLate, isTrue);
    expect(silentBuyerConfirmsAt(order), isNull);
  });
}

/// The order as `GET /orders/ord-1` returns it (version 7, after the local
/// copy's 6, so the pull keeps it).
Map<String, dynamic> _serverOrder({Map<String, dynamic>? checkIns}) => {
      'orderId': 'ord-1',
      'listingId': 'listing-1',
      'buyerId': 'buyer-1',
      'farmerId': 'farmer-1',
      'buyerName': 'Buyer',
      'farmerName': 'Farmer',
      'cropType': 'teff',
      'quantity': 500,
      'pricePerUnit': 60,
      'totalPrice': 30000,
      'status': 'in_transit',
      'createdAt': '2026-11-10T08:00:00.000Z',
      'version': 7,
      'tripId': 'trip-1',
      'truckerId': 'trucker-1',
      'fulfillment': 'trucker',
      'dropoffJudgement': 'pending',
      'dropoffDeadlineAt':
          DateTime.now().toUtc().add(const Duration(days: 1)).toIso8601String(),
      if (checkIns != null) 'checkIns': checkIns,
    };
