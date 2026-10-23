import 'dart:io';

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
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_repository.dart';

class MockApiClient extends Mock implements ApiClient {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

/// Insert a test payment directly into Isar.
Future<Payment> _insertTestPayment(
  Isar isar, {
  String paymentId = 'esc-001',
  String deliveryId = 'del-001',
  String farmerId = 'farmer-001',
  String buyerId = 'buyer-001',
  double amount = 15000.0,
  PaymentStatus status = PaymentStatus.escrowed,
  DateTime? createdAt,
}) async {
  final now = createdAt ?? DateTime.now().toUtc();
  final payment = Payment()
    ..paymentId = paymentId
    ..deliveryId = deliveryId
    ..farmerId = farmerId
    ..buyerId = buyerId
    ..amount = amount
    ..commission = amount * 0.025 < 5 ? 5.0 : amount * 0.025
    ..commissionRate = 0.025
    ..farmerPayout = amount - (amount * 0.025 < 5 ? 5.0 : amount * 0.025)
    ..status = status
    ..createdAt = now
    ..updatedAt = now
    ..expiresAt = now.add(const Duration(days: 7))
    ..version = 1;

  await isar.writeTxn(() async {
    await isar.payments.put(payment);
  });

  return payment;
}

/// Insert a test payment intent directly into Isar.
Future<PaymentIntent> _insertTestIntent(
  Isar isar, {
  String idempotencyKey = 'buyer-001:del-001:initiate:1708300000000',
  String deliveryId = 'del-001',
  String buyerId = 'buyer-001',
  String farmerId = 'farmer-001',
  double amount = 15000.0,
  IntentStatus status = IntentStatus.pendingSync,
  DateTime? createdAt,
}) async {
  final intent = PaymentIntent()
    ..idempotencyKey = idempotencyKey
    ..deliveryId = deliveryId
    ..buyerId = buyerId
    ..farmerId = farmerId
    ..amount = amount
    ..status = status
    ..createdAt = createdAt ?? DateTime.now().toUtc();

  await isar.writeTxn(() async {
    await isar.paymentIntents.put(intent);
  });

  return intent;
}

void main() {
  group('EscrowRepository', () {
    late Isar isar;
    late Directory tempDir;
    late MockApiClient apiClient;
    late MockConnectivityMonitor connectivityMonitor;
    late EscrowRepository repository;

    setUpAll(() async {
      await Isar.initializeIsarCore(download: true);
    });

    setUp(() async {
      tempDir = Directory.systemTemp.createTempSync('escrow_repo_test_');
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
        name: 'test_escrow_repo_${DateTime.now().millisecondsSinceEpoch}',
      );
      apiClient = MockApiClient();
      connectivityMonitor = MockConnectivityMonitor();
      repository = EscrowRepository(
        isar: isar,
        apiClient: apiClient,
        connectivityMonitor: connectivityMonitor,
      );
    });

    tearDown(() async {
      await isar.close(deleteFromDisk: true);
      if (tempDir.existsSync()) {
        tempDir.deleteSync(recursive: true);
      }
    });

    // ── initiatePayment tests ──

    group('initiatePayment', () {
      test('succeeds online and caches payment', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
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
            'telebirrRef': 'TBR-001',
          },
        );

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowSuccess>());
        final success = result as EscrowSuccess;
        expect(success.payment.paymentId, 'esc-001');
        expect(success.payment.amount, 15000.0);
        expect(success.payment.commission, 375.0);
        expect(success.payment.farmerPayout, 14625.0);
        expect(success.payment.status, PaymentStatus.escrowed);

        // Verify cached in Isar
        final cached = await isar.payments
            .filter()
            .paymentIdEqualTo('esc-001')
            .findFirst();
        expect(cached, isNotNull);
        expect(cached!.amount, 15000.0);
      });

      test('queues intent when offline', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => false);

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowQueued>());
        final queued = result as EscrowQueued;
        expect(queued.intent.deliveryId, 'del-001');
        expect(queued.intent.amount, 15000.0);
        expect(queued.intent.status, IntentStatus.pendingSync);

        // Verify persisted in Isar
        final intents = await isar.paymentIntents.where().findAll();
        expect(intents.length, 1);
        expect(intents.first.buyerId, 'buyer-001');
      });

      test('returns error for amount below minimum (100 ETB)', () async {
        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 50.0,
        );

        expect(result, isA<EscrowError>());
        final error = result as EscrowError;
        expect(error.code, 'ESCROW_001');
      });

      test('returns error for amount above maximum (500,000 ETB)', () async {
        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 600000.0,
        );

        expect(result, isA<EscrowError>());
        final error = result as EscrowError;
        expect(error.code, 'ESCROW_001');
      });

      test('returns error for self-trading (buyerId == farmerId)', () async {
        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'user-001',
          farmerId: 'user-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowError>());
        final error = result as EscrowError;
        expect(error.code, 'ESCROW_001');
        expect(error.message, contains('yourself'));
      });

      test('falls back to intent queue on network error', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const NetworkException());

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowQueued>());
      });

      test('falls back to intent queue on timeout', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const TimeoutException());

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowQueued>());
      });

      test('returns API error for validation failure', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(
          const ValidationException(
            'Amount must be between 100 and 500000',
          ),
        );

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowError>());
        final error = result as EscrowError;
        expect(error.code, 'ESCROW_VALIDATION');
      });

      test('accepts boundary amount 100 ETB', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => false);

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 100.0,
        );

        expect(result, isA<EscrowQueued>());
      });

      test('accepts boundary amount 500,000 ETB', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => false);

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 500000.0,
        );

        expect(result, isA<EscrowQueued>());
      });
    });

    // ── raiseDispute tests ──

    group('raiseDispute', () {
      test('succeeds online and updates local cache', () async {
        // Insert a payment to update
        await _insertTestPayment(isar);

        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenAnswer(
          (_) async => {
            'escrowId': 'esc-001',
            'status': 'disputed',
            'disputeId': 'disp-001',
            'disputedBy': 'buyer-001',
            'reason': 'damaged_goods',
            'disputeWindowEndsAt': '2026-02-21T10:00:00Z',
            'createdAt': '2026-02-19T10:00:00Z',
          },
        );

        final result = await repository.raiseDispute(
          escrowId: 'esc-001',
          disputedBy: 'buyer-001',
          reason: 'damaged_goods',
          description: 'Bags were wet',
        );

        expect(result, isA<EscrowSuccess>());
        final success = result as EscrowSuccess;
        expect(success.payment.status, PaymentStatus.disputed);
        expect(success.payment.disputeReason, 'damaged_goods');
      });

      test('returns error when offline', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => false);

        final result = await repository.raiseDispute(
          escrowId: 'esc-001',
          disputedBy: 'buyer-001',
          reason: 'damaged_goods',
        );

        expect(result, isA<EscrowError>());
        final error = result as EscrowError;
        expect(error.code, 'ESCROW_OFFLINE');
      });

      test('returns error on API failure', () async {
        await _insertTestPayment(isar);

        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const NotFoundException('Payment not found'));

        final result = await repository.raiseDispute(
          escrowId: 'esc-001',
          disputedBy: 'buyer-001',
          reason: 'damaged_goods',
        );

        expect(result, isA<EscrowError>());
        final error = result as EscrowError;
        expect(error.code, 'ESCROW_016');
      });
    });

    // ── getEscrow tests ──

    group('getEscrow', () {
      test('returns payment from cache', () async {
        await _insertTestPayment(isar, paymentId: 'esc-cached');

        final payment = await repository.getEscrow('esc-cached');
        expect(payment, isNotNull);
        expect(payment!.paymentId, 'esc-cached');
      });

      test('returns null when not in cache and offline', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => false);
        final payment = await repository.getEscrow('esc-nonexistent');
        expect(payment, isNull);
      });

      test('falls through to server on cache miss when online (H2)', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(() => apiClient.get<Map<String, dynamic>>(any())).thenAnswer(
          (_) async => {
            'escrowId': 'esc-net',
            'deliveryId': 'del-net',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'amount': 15000.0,
            'commission': 375.0,
            'commissionRate': 0.025,
            'farmerPayout': 14625.0,
            'currency': 'ETB',
            'status': 'escrowed',
            'createdAt': '2026-02-17T10:00:00Z',
            'updatedAt': '2026-02-17T10:00:00Z',
            'expiresAt': '2026-02-24T10:00:00Z',
            'version': 1,
          },
        );

        final payment = await repository.getEscrow('esc-net');
        expect(payment, isNotNull);
        expect(payment!.paymentId, 'esc-net');
      });
    });

    // ── refreshEscrow tests ──

    group('refreshEscrow', () {
      test('fetches from server when online and updates cache', () async {
        await _insertTestPayment(isar);

        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(() => apiClient.get<Map<String, dynamic>>(any())).thenAnswer(
          (_) async => {
            'escrowId': 'esc-001',
            'deliveryId': 'del-001',
            'buyerId': 'buyer-001',
            'farmerId': 'farmer-001',
            'amount': 15000.0,
            'commission': 375.0,
            'commissionRate': 0.025,
            'farmerPayout': 14625.0,
            'currency': 'ETB',
            'status': 'released',
            'createdAt': '2026-02-17T10:00:00Z',
            'updatedAt': '2026-02-19T10:00:00Z',
            'expiresAt': '2026-02-24T10:00:00Z',
            'version': 2,
          },
        );

        final payment = await repository.refreshEscrow('esc-001');
        expect(payment, isNotNull);
        expect(payment!.status, PaymentStatus.released);
        expect(payment.version, 2);
      });

      test('caches the payee\'s expected payout date (FRD-13)', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(() => apiClient.get<Map<String, dynamic>>(any())).thenAnswer(
          (_) async => {
            'escrowId': 'esc-001',
            'status': 'escrowed',
            'createdAt': '2026-02-17T10:00:00Z',
            'updatedAt': '2026-02-19T10:00:00Z',
            'expiresAt': '2026-02-24T10:00:00Z',
            'disputeWindowEndsAt': '2026-02-21T10:00:00Z',
            'releaseNotBefore': '2026-02-22T10:00:00.0000000Z',
          },
        );

        final payment = await repository.refreshEscrow('esc-001');

        expect(
          payment!.releaseNotBefore!.toUtc(),
          DateTime.utc(2026, 2, 22, 10),
        );
        final cached = await repository.getEscrow('esc-001');
        expect(
          cached!.releaseNotBefore!.toUtc(),
          DateTime.utc(2026, 2, 22, 10),
        );
      });

      test('falls back to cache when offline', () async {
        await _insertTestPayment(isar);

        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => false);

        final payment = await repository.refreshEscrow('esc-001');
        expect(payment, isNotNull);
        expect(payment!.status, PaymentStatus.escrowed);
      });
    });

    // ── getHistory tests ──

    group('getHistory', () {
      test('returns all payments from cache', () async {
        await _insertTestPayment(isar);
        await _insertTestPayment(
          isar,
          paymentId: 'esc-002',
          deliveryId: 'del-002',
        );

        final payments = await repository.getHistory(userId: 'farmer-001');
        expect(payments.length, 2);
      });

      test('filters by status', () async {
        await _insertTestPayment(
          isar,
        );
        await _insertTestPayment(
          isar,
          paymentId: 'esc-002',
          deliveryId: 'del-002',
          status: PaymentStatus.completed,
        );

        final payments = await repository.getHistory(
          userId: 'farmer-001',
          statusFilter: 'escrowed',
        );
        expect(payments.length, 1);
        expect(payments.first.paymentId, 'esc-001');
      });

      test('returns empty list when no payments', () async {
        final payments = await repository.getHistory(userId: 'farmer-001');
        expect(payments, isEmpty);
      });
    });

    // ── refreshHistory upsert tests (order-funded escrow party ids) ──

    group('refreshHistory', () {
      Map<String, dynamic> summaryJson({
        String escrowId = 'escrow-order-ord-1',
        String deliveryId = '',
        String buyerId = 'buyer-001',
        String farmerId = 'farmer-001',
        double? farmerPayout,
        double? truckerPayout,
        String? truckerId,
        String? expiresAt,
        bool? refundPending,
        String? refundReason,
        String? releaseNotBefore,
      }) =>
          {
            'escrows': [
              {
                'escrowId': escrowId,
                'deliveryId': deliveryId,
                'orderId': 'ord-1',
                'buyerId': buyerId,
                'farmerId': farmerId,
                'counterpartyName': 'Abebe',
                'amount': 1500.0,
                'commission': 37.5,
                if (farmerPayout != null) 'farmerPayout': farmerPayout,
                if (truckerPayout != null) 'truckerPayout': truckerPayout,
                if (truckerId != null) 'truckerId': truckerId,
                if (expiresAt != null) 'expiresAt': expiresAt,
                if (refundPending != null) 'refundPending': refundPending,
                if (refundReason != null) 'refundReason': refundReason,
                if (releaseNotBefore != null)
                  'releaseNotBefore': releaseNotBefore,
                'currency': 'ETB',
                'status': 'escrowed',
                'createdAt': '2026-03-01T10:00:00.0000000Z',
              },
            ],
            'hasMore': false,
            'total': 1,
          };

      test(
          'caches a server-created row with its party ids so it survives '
          'offline getHistory (order-funded escrow, empty deliveryId)',
          () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.get<Map<String, dynamic>>(
            any(),
            queryParameters: any(named: 'queryParameters'),
          ),
        ).thenAnswer((_) async => summaryJson());

        await repository.refreshHistory(userId: 'farmer-001');

        // The farmer never initiated this payment locally, yet the offline
        // read must keep it — the summary's party ids make the own-party
        // filter match (regression: rows used to be cached with empty ids
        // and vanish from every subsequent local read).
        final cached = await repository.getHistory(userId: 'farmer-001');
        expect(cached.length, 1);
        expect(cached.first.paymentId, 'escrow-order-ord-1');
        expect(cached.first.buyerId, 'buyer-001');
        expect(cached.first.farmerId, 'farmer-001');
        expect(cached.first.deliveryId, isEmpty);
      });

      test('preserves cached party ids when an older server sends none',
          () async {
        await _insertTestPayment(isar, paymentId: 'escrow-order-ord-1');

        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.get<Map<String, dynamic>>(
            any(),
            queryParameters: any(named: 'queryParameters'),
          ),
        ).thenAnswer((_) async => summaryJson(buyerId: '', farmerId: ''));

        await repository.refreshHistory(userId: 'farmer-001');

        final cached = await repository.getHistory(userId: 'farmer-001');
        expect(cached.length, 1);
        expect(cached.first.buyerId, 'buyer-001');
        expect(cached.first.farmerId, 'farmer-001');
      });

      test(
          'caches the per-party legs and lets the trucker read its own row '
          'offline (SIM-15)', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.get<Map<String, dynamic>>(
            any(),
            queryParameters: any(named: 'queryParameters'),
          ),
        ).thenAnswer(
          (_) async => summaryJson(
            farmerPayout: 1362.5,
            truckerPayout: 100.0,
            truckerId: 'trucker-001',
          ),
        );

        await repository.refreshHistory(
          userId: 'trucker-001',
          roleFilter: 'trucker',
        );

        // The own-party filter now matches on truckerId too.
        final cached = await repository.getHistory(userId: 'trucker-001');
        expect(cached.length, 1);
        final row = cached.first;
        expect(row.truckerId, 'trucker-001');
        expect(row.truckerPayout, 100.0);
        expect(row.farmerPayout, 1362.5);
        expect(row.payoutFor('trucker'), 100.0);
        expect(row.payoutFor('farmer'), 1362.5);
        expect(row.payoutFor('buyer'), 1500.0);
        // amount = farmer + trucker + commission.
        expect(row.farmerPayout + row.truckerPayout! + row.commission, 1500.0);
      });

      test(
          'caches the order reference, expiry and refund-pending flag of a '
          'cancelled-order escrow (SIM-20)', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.get<Map<String, dynamic>>(
            any(),
            queryParameters: any(named: 'queryParameters'),
          ),
        ).thenAnswer(
          (_) async => summaryJson(
            expiresAt: '2026-03-01T10:05:00.000Z',
            refundPending: true,
            refundReason: 'order_cancelled',
          ),
        );

        await repository.refreshHistory(userId: 'farmer-001');

        final row = (await repository.getHistory(userId: 'farmer-001')).single;
        expect(row.orderId, 'ord-1');
        expect(row.deliveryId, isEmpty);
        // Isar hands DateTimes back in local time: compare the instant.
        expect(
          row.expiresAt!.toUtc(),
          DateTime.parse('2026-03-01T10:05:00.000Z'),
        );
        expect(row.refundPending, isTrue);
        expect(row.refundReason, 'order_cancelled');
        expect(row.isRefundPending, isTrue);
      });

      test('caches the farmer\'s expected payout date (FRD-13)', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.get<Map<String, dynamic>>(
            any(),
            queryParameters: any(named: 'queryParameters'),
          ),
        ).thenAnswer(
          (_) async =>
              summaryJson(releaseNotBefore: '2026-03-04T10:00:00.0000000Z'),
        );

        await repository.refreshHistory(
          userId: 'farmer-001',
          roleFilter: 'farmer',
        );

        final row = (await repository.getHistory(userId: 'farmer-001')).single;
        expect(row.releaseNotBefore!.toUtc(), DateTime.utc(2026, 3, 4, 10));
      });

      test(
          'a refresh without the date clears a cached one, so a buyer on a '
          'shared device never keeps a payee\'s date (FRD-13)', () async {
        final cached =
            await _insertTestPayment(isar, paymentId: 'escrow-order-ord-1');
        cached.releaseNotBefore = DateTime.utc(2026, 3, 4, 10);
        await isar.writeTxn(() => isar.payments.put(cached));

        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.get<Map<String, dynamic>>(
            any(),
            queryParameters: any(named: 'queryParameters'),
          ),
        ).thenAnswer((_) async => summaryJson());

        await repository.refreshHistory(userId: 'buyer-001');

        final row = (await repository.getHistory(userId: 'buyer-001')).single;
        expect(row.releaseNotBefore, isNull);
      });

      test('derives the farmer leg when an older server sends no legs',
          () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.get<Map<String, dynamic>>(
            any(),
            queryParameters: any(named: 'queryParameters'),
          ),
        ).thenAnswer((_) async => summaryJson());

        await repository.refreshHistory(userId: 'farmer-001');

        final cached = await repository.getHistory(userId: 'farmer-001');
        expect(cached.first.farmerPayout, 1500.0 - 37.5);
        expect(cached.first.truckerPayout, isNull);
        expect(cached.first.payoutFor('trucker'), 0);
      });
    });

    // ── cancelIntent tests ──

    group('cancelIntent', () {
      test('cancels a pending intent', () async {
        final intent = await _insertTestIntent(isar);

        await repository.cancelIntent(intent.idempotencyKey);

        final updated = await isar.paymentIntents.get(intent.id);
        expect(updated!.status, IntentStatus.cancelled);
      });

      test('does nothing if intent not found', () async {
        // Should not throw
        await repository.cancelIntent('nonexistent-key');
      });

      test('does nothing if intent is not pendingSync', () async {
        await _insertTestIntent(isar, status: IntentStatus.syncing);

        await repository
            .cancelIntent('buyer-001:del-001:initiate:1708300000000');

        final intent = await isar.paymentIntents.where().findFirst();
        expect(intent!.status, IntentStatus.syncing);
      });
    });

    // ── getPendingIntents tests ──

    group('getPendingIntents', () {
      test('returns only pendingSync intents', () async {
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-1',
        );
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-2',
          deliveryId: 'del-002',
          status: IntentStatus.completed,
        );
        await _insertTestIntent(
          isar,
          idempotencyKey: 'key-3',
          deliveryId: 'del-003',
          status: IntentStatus.failed,
        );

        final pending = await repository.getPendingIntents();
        expect(pending.length, 1);
        expect(pending.first.idempotencyKey, 'key-1');
      });

      test('returns empty list when no pending intents', () async {
        final pending = await repository.getPendingIntents();
        expect(pending, isEmpty);
      });
    });

    // ── Idempotency key format tests ──

    group('idempotency key format', () {
      test(
          'generates key in correct format {actor}:{entityId}:{action}:{timestamp}',
          () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => false);

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowQueued>());
        final queued = result as EscrowQueued;
        final key = queued.intent.idempotencyKey;

        final parts = key.split(':');
        expect(parts.length, 4);
        expect(parts[0], 'buyer-001'); // actor
        expect(parts[1], 'del-001'); // entityId
        expect(parts[2], 'initiate'); // action
        expect(int.tryParse(parts[3]), isNotNull); // timestamp
      });
    });

    // ── API exception mapping tests ──

    group('API exception mapping', () {
      test('maps AuthException to ESCROW_005', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const AuthException());

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowError>());
        expect((result as EscrowError).code, 'ESCROW_005');
      });

      test('maps ConflictException to ESCROW_009', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const ConflictException('Already exists'));

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowError>());
        expect((result as EscrowError).code, 'ESCROW_009');
      });

      test('keeps the server code on a 409: ESCROW_024 is funds already held',
          () async {
        // SIM-24: a 409 is not always "already paid for this delivery".
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(
          const ConflictException(
            'Funds for this order are already held in escrow.',
            409,
            'ESCROW_024',
          ),
        );

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowError>());
        expect((result as EscrowError).code, 'ESCROW_024');
      });

      test('keeps a 403 ESCROW_007 over the generic login fallback', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(
          const AuthException('Fayda verification required', 403, 'ESCROW_007'),
        );

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect((result as EscrowError).code, 'ESCROW_007');
      });

      test('maps ServerException 502 to ESCROW_013 (gateway error)', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const ServerException('Bad Gateway', 502));

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowError>());
        expect((result as EscrowError).code, 'ESCROW_013');
      });

      test('maps ServerException 503 to ESCROW_014 (service unavailable)',
          () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const ServerException('Service Unavailable', 503));

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowError>());
        expect((result as EscrowError).code, 'ESCROW_014');
      });

      test(
          'keeps the server code on a 422: ESCROW_039 is the live refusal of '
          'the delivery path (FUP-36)', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(
          const ServerException(
            'Online payment for a delivery is not available here.',
            422,
            'ESCROW_039',
          ),
        );

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect((result as EscrowError).code, 'ESCROW_039');
      });

      test('a 500 without a server code still reads as ESCROW_CLIENT',
          () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const ServerException('Internal error', 500));

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect((result as EscrowError).code, 'ESCROW_CLIENT');
      });

      test('maps RateLimitException to ESCROW_011', () async {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const RateLimitException());

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000.0,
        );

        expect(result, isA<EscrowError>());
        expect((result as EscrowError).code, 'ESCROW_011');
      });
    });

    group('step-up (403 AUTH_040)', () {
      setUp(() {
        when(() => connectivityMonitor.isConnected())
            .thenAnswer((_) async => true);
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(
          const StepUpRequiredException('AUTH_040: step-up required'),
        );
      });

      test('initiatePayment maps to the step-up code, not a queued intent',
          () async {
        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000,
        );

        expect(result, isA<EscrowError>());
        expect((result as EscrowError).code, stepUpRequiredCode);
        expect(await isar.paymentIntents.count(), 0);
      });

      test('raiseDispute maps to the step-up code', () async {
        await _insertTestPayment(isar);

        final result = await repository.raiseDispute(
          escrowId: 'esc-001',
          disputedBy: 'buyer-001',
          reason: 'damaged',
        );

        expect(result, isA<EscrowError>());
        expect((result as EscrowError).code, stepUpRequiredCode);
      });

      test('initiatePayment keeps AUTH_041 so the screen asks for Fayda',
          () async {
        when(
          () => apiClient.post<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(
          const StepUpRequiredException('Fayda needed', 403, 'AUTH_041'),
        );

        final result = await repository.initiatePayment(
          deliveryId: 'del-001',
          buyerId: 'buyer-001',
          farmerId: 'farmer-001',
          amount: 15000,
        );

        expect((result as EscrowError).code, faydaStepUpRequiredCode);
        expect(await isar.paymentIntents.count(), 0);
      });
    });

    group('openOrderCheckout (ESC-44)', () {
      test('returns the page, its window and the server clock', () async {
        when(
          () => apiClient.postWithServerDate<Map<String, dynamic>>(
            '/escrow/order-checkout',
            data: any<dynamic>(named: 'data'),
          ),
        ).thenAnswer(
          (_) async => (
            data: <String, dynamic>{
              'orderId': 'order-1',
              'amount': 2448.75,
              'currency': 'ETB',
              'toPayUrl': 'https://pay.example/checkout?prepay_id=P1',
              'openUntil': '2026-10-09T12:10:00.0000000Z',
              'isExisting': true,
            },
            serverDate: DateTime.utc(2026, 10, 9, 10),
          ),
        );

        final result = await repository.openOrderCheckout('order-1');

        expect(result, isA<OrderCheckoutOpened>());
        final opened = result as OrderCheckoutOpened;
        expect(opened.toPayUrl, contains('prepay_id=P1'));
        expect(opened.openUntil, DateTime.utc(2026, 10, 9, 12, 10));
        expect(opened.amount, 2448.75);
        expect(opened.isExisting, isTrue);
        expect(opened.serverDate, DateTime.utc(2026, 10, 9, 10));
        verify(
          () => apiClient.postWithServerDate<Map<String, dynamic>>(
            '/escrow/order-checkout',
            data: {'orderId': 'order-1'},
          ),
        ).called(1);
      });

      test('offline: a failure, never a queued intent', () async {
        when(
          () => apiClient.postWithServerDate<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(const NetworkException('no route'));

        final result = await repository.openOrderCheckout('order-1');

        expect(result, isA<OrderCheckoutFailed>());
        expect((result as OrderCheckoutFailed).code, 'ESCROW_OFFLINE');
        expect(result.statusCode, isNull);
        expect(await isar.paymentIntents.count(), 0);
      });

      test('keeps the server code and the HTTP status', () async {
        when(
          () => apiClient.postWithServerDate<Map<String, dynamic>>(
            any(),
            data: any<dynamic>(named: 'data'),
          ),
        ).thenThrow(
          const ConflictException('payment time ran out', 409, 'ESCROW_030'),
        );

        final result = await repository.openOrderCheckout('order-1')
            as OrderCheckoutFailed;

        expect(result.code, 'ESCROW_030');
        expect(result.statusCode, 409);
      });
    });
  });
}
