import 'dart:async';
import 'dart:io';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/escrow_payment/data/services/escrow_sync_service.dart';

class MockApiClient extends Mock implements ApiClient {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

/// Insert a test payment intent directly into Isar.
Future<PaymentIntent> _insertTestIntent(
  Isar isar, {
  String idempotencyKey = 'buyer-001:del-001:initiate:1708300000000',
  String deliveryId = 'del-001',
  String buyerId = 'buyer-001',
  String farmerId = 'farmer-001',
  double amount = 15000.0,
  IntentStatus status = IntentStatus.pendingSync,
  int retryCount = 0,
  DateTime? createdAt,
  DateTime? lastAttemptAt,
}) async {
  final intent = PaymentIntent()
    ..idempotencyKey = idempotencyKey
    ..deliveryId = deliveryId
    ..buyerId = buyerId
    ..farmerId = farmerId
    ..amount = amount
    ..status = status
    ..retryCount = retryCount
    ..createdAt = createdAt ?? DateTime.now().toUtc()
    ..lastAttemptAt = lastAttemptAt;

  await isar.writeTxn(() async {
    await isar.paymentIntents.put(intent);
  });

  return intent;
}

void main() {
  group('EscrowSyncService', () {
    late Isar isar;
    late Directory tempDir;
    late MockApiClient apiClient;
    late MockConnectivityMonitor connectivityMonitor;
    late EscrowSyncService syncService;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('escrow_sync_test_');
      isar = await Isar.open(
        [
          UserSchema,
          CropListingSchema,
          TripSchema,
          DeliverySchema,
          PaymentSchema,
          PaymentIntentSchema,
          SyncOperationSchema,
        ],
        directory: tempDir.path,
        name: 'test_escrow_sync_${DateTime.now().millisecondsSinceEpoch}',
      );
      apiClient = MockApiClient();
      connectivityMonitor = MockConnectivityMonitor();
      syncService = EscrowSyncService(
        isar: isar,
        apiClient: apiClient,
        connectivityMonitor: connectivityMonitor,
      );
    });

    tearDown(() async {
      syncService.dispose();
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    // ── processQueuedIntents tests ──

    group('processQueuedIntents', () {
      test('processes pending intent successfully', () async {
        await _insertTestIntent(isar);

        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenAnswer(
          (_) async => {
            'escrowId': 'esc-001',
            'deliveryId': 'del-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'amount': 15000.0,
            'commission': 375.0,
            'farmerPayout': 14625.0,
            'currency': 'ETB',
            'status': 'escrowed',
            'createdAt': '2026-02-19T10:00:00Z',
            'expiresAt': '2026-02-26T10:00:00Z',
          },
        );

        await syncService.processQueuedIntents();

        // Intent should be marked completed
        final intent = await isar.paymentIntents.where().findFirst();
        expect(intent!.status, IntentStatus.completed);

        // Payment should be cached
        final payment = await isar.payments
            .filter()
            .paymentIdEqualTo('esc-001')
            .findFirst();
        expect(payment, isNotNull);
        expect(payment!.amount, 15000.0);
      });

      test('processing lock prevents concurrent execution (MH4)', () async {
        await _insertTestIntent(isar);

        // Simulate a slow API call
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenAnswer((_) async {
          await Future<void>.delayed(const Duration(milliseconds: 200));
          return {
            'escrowId': 'esc-001',
            'deliveryId': 'del-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'amount': 15000.0,
            'commission': 375.0,
            'farmerPayout': 14625.0,
            'currency': 'ETB',
            'status': 'escrowed',
            'createdAt': '2026-02-19T10:00:00Z',
            'expiresAt': '2026-02-26T10:00:00Z',
          };
        });

        // Fire two concurrent processing attempts
        final future1 = syncService.processQueuedIntents();
        final future2 = syncService.processQueuedIntents();

        await Future.wait([future1, future2]);

        // API should only be called once (lock prevented second call)
        verify(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).called(1);
      });

      test('marks intent as failed after max retries', () async {
        await _insertTestIntent(isar, retryCount: 2); // Already at 2 retries

        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const ServerException('Internal error', 500));

        await syncService.processQueuedIntents();

        final intent = await isar.paymentIntents.where().findFirst();
        expect(intent!.status, IntentStatus.failed);
        expect(intent.retryCount, 3);
      });

      test('puts intent back to pendingSync when retries remain', () async {
        await _insertTestIntent(isar);

        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const ServerException('Internal error', 500));

        await syncService.processQueuedIntents();

        final intent = await isar.paymentIntents.where().findFirst();
        expect(intent!.status, IntentStatus.pendingSync);
        expect(intent.retryCount, 1);
      });

      test('uses persisted idempotency key on retry (MH2)', () async {
        const persistedKey = 'buyer-001:del-001:initiate:1708300000000';
        await _insertTestIntent(isar);

        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenAnswer(
          (_) async => {
            'escrowId': 'esc-001',
            'deliveryId': 'del-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'amount': 15000.0,
            'commission': 375.0,
            'farmerPayout': 14625.0,
            'currency': 'ETB',
            'status': 'escrowed',
            'createdAt': '2026-02-19T10:00:00Z',
            'expiresAt': '2026-02-26T10:00:00Z',
          },
        );

        await syncService.processQueuedIntents();

        // Verify the API was called with the persisted key
        final captured = verify(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: captureAny<dynamic>(named: 'data'),
          ),
        ).captured.single as Map<String, dynamic>;
        expect(captured['idempotencyKey'], persistedKey);
      });

      test('does not process non-pendingSync intents', () async {
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-1',
          status: IntentStatus.completed,
        );
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-2',
          deliveryId: 'del-002',
          status: IntentStatus.failed,
        );
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-3',
          deliveryId: 'del-003',
          status: IntentStatus.cancelled,
        );

        await syncService.processQueuedIntents();

        verifyNever(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        );
      });
    });

    // ── cleanupStaleIntents tests ──

    group('cleanupStaleIntents', () {
      test('deletes completed intents older than 24h', () async {
        final staleTime =
            DateTime.now().toUtc().subtract(const Duration(hours: 25));
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-stale',
          status: IntentStatus.completed,
          createdAt: staleTime,
        );
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-fresh',
          deliveryId: 'del-002',
          status: IntentStatus.completed,
          createdAt: DateTime.now().toUtc(),
        );

        await syncService.cleanupStaleIntents();

        final remaining = await isar.paymentIntents.where().findAll();
        expect(remaining.length, 1);
        expect(remaining.first.idempotencyKey, 'key-fresh');
      });

      test('deletes failed intents older than 24h', () async {
        final staleTime =
            DateTime.now().toUtc().subtract(const Duration(hours: 25));
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-stale-failed',
          status: IntentStatus.failed,
          createdAt: staleTime,
        );

        await syncService.cleanupStaleIntents();

        final remaining = await isar.paymentIntents.where().findAll();
        expect(remaining, isEmpty);
      });

      test('deletes cancelled intents older than 24h', () async {
        final staleTime =
            DateTime.now().toUtc().subtract(const Duration(hours: 25));
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-stale-cancelled',
          status: IntentStatus.cancelled,
          createdAt: staleTime,
        );

        await syncService.cleanupStaleIntents();

        final remaining = await isar.paymentIntents.where().findAll();
        expect(remaining, isEmpty);
      });

      test('does not delete pendingSync intents', () async {
        final staleTime =
            DateTime.now().toUtc().subtract(const Duration(hours: 25));
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-pending',
          createdAt: staleTime,
        );

        await syncService.cleanupStaleIntents();

        final remaining = await isar.paymentIntents.where().findAll();
        expect(remaining.length, 1);
        expect(remaining.first.status, IntentStatus.pendingSync);
      });
    });

    // ── cancelAllPendingIntents tests ──

    group('cancelAllPendingIntents', () {
      test('cancels all pending intents (MH8)', () async {
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-1',
        );
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-2',
          deliveryId: 'del-002',
        );
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-3',
          deliveryId: 'del-003',
          status: IntentStatus.completed,
        );

        // ignore: deprecated_member_use_from_same_package
        await syncService.cancelAllPendingIntents();

        final all = await isar.paymentIntents.where().findAll();
        expect(all.length, 3);

        final pending =
            all.where((i) => i.status == IntentStatus.pendingSync).toList();
        expect(pending, isEmpty);

        final cancelled =
            all.where((i) => i.status == IntentStatus.cancelled).toList();
        expect(cancelled.length, 2);

        // Completed intent should remain unchanged
        final completed =
            all.where((i) => i.status == IntentStatus.completed).toList();
        expect(completed.length, 1);
      });
    });

    // ── startListening tests ──

    group('startListening', () {
      test('processes intents when connectivity restored', () async {
        final controller =
            StreamController<List<ConnectivityResult>>.broadcast();
        when(() => connectivityMonitor.onConnectivityChanged)
            .thenAnswer((_) => controller.stream);
        when(() => connectivityMonitor.isConnectedFromResults(any()))
            .thenReturn(true);

        // Insert a pending intent
        await _insertTestIntent(isar);

        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenAnswer(
          (_) async => {
            'escrowId': 'esc-001',
            'deliveryId': 'del-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'amount': 15000.0,
            'commission': 375.0,
            'farmerPayout': 14625.0,
            'currency': 'ETB',
            'status': 'escrowed',
            'createdAt': '2026-02-19T10:00:00Z',
            'expiresAt': '2026-02-26T10:00:00Z',
          },
        );

        syncService.startListening();

        // Emit connectivity restored event
        controller.add([ConnectivityResult.wifi]);
        await Future<void>.delayed(const Duration(milliseconds: 200));

        verify(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).called(1);

        await controller.close();
      });
    });

    // ── Exponential backoff tests ──

    group('exponential backoff', () {
      test('retry count 0 has no delay (immediate)', () async {
        await _insertTestIntent(isar);

        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenAnswer(
          (_) async => {
            'escrowId': 'esc-001',
            'deliveryId': 'del-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'amount': 15000.0,
            'commission': 375.0,
            'farmerPayout': 14625.0,
            'currency': 'ETB',
            'status': 'escrowed',
            'createdAt': '2026-02-19T10:00:00Z',
            'expiresAt': '2026-02-26T10:00:00Z',
          },
        );

        final stopwatch = Stopwatch()..start();
        await syncService.processQueuedIntents();
        stopwatch.stop();

        // First attempt should be nearly immediate (< 1s)
        expect(stopwatch.elapsedMilliseconds, lessThan(1000));
      });

      test('max retries is 3', () {
        expect(EscrowSyncService.maxRetries, 3);
      });

      test('stale threshold is 24 hours', () {
        expect(
          EscrowSyncService.staleThreshold,
          const Duration(hours: 24),
        );
      });
    });
  });
}
