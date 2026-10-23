import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/domain/escrow_hold.dart';

class MockApiClient extends Mock implements ApiClient {}

class MockConnectivityMonitor extends Mock implements ConnectivityMonitor {}

Map<String, dynamic> _row({
  String escrowId = 'escrow-order-ord-1',
  String orderId = 'ord-1',
  String status = 'held',
}) =>
    {
      'escrowId': escrowId,
      'deliveryId': '',
      'orderId': orderId,
      'buyerId': 'buyer-001',
      'farmerId': 'farmer-001',
      'amount': 1500.0,
      'commission': 37.5,
      'farmerPayout': 1462.5,
      'currency': 'ETB',
      'status': status,
      'createdAt': '2026-10-01T10:00:00.0000000Z',
    };

/// FRD-06 on mobile: `held` and values the app does not know are parsed
/// instead of throwing (the history refresh used to fail on
/// `dispute_resolved`), cached in Isar, and used offline.
void main() {
  late Isar isar;
  late Directory tempDir;
  late MockApiClient api;
  late MockConnectivityMonitor monitor;
  late EscrowRepository repo;

  setUpAll(() async {
    await Isar.initializeIsarCore(download: true);
  });

  setUp(() async {
    tempDir = Directory.systemTemp.createTempSync('escrow_held_test_');
    isar = await Isar.open(
      [PaymentSchema, PaymentIntentSchema],
      directory: tempDir.path,
      name: 'escrow_held_${DateTime.now().microsecondsSinceEpoch}',
    );
    api = MockApiClient();
    monitor = MockConnectivityMonitor();
    repo = EscrowRepository(
      isar: isar,
      apiClient: api,
      connectivityMonitor: monitor,
    );
  });

  tearDown(() async {
    await isar.close(deleteFromDisk: true);
    if (tempDir.existsSync()) tempDir.deleteSync(recursive: true);
  });

  void online(bool value) =>
      when(() => monitor.isConnected()).thenAnswer((_) async => value);

  test('the history refresh parses held, dispute_resolved and unknown values',
      () async {
    online(true);
    when(
      () => api.get<Map<String, dynamic>>(
        any(),
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer(
      (_) async => {
        'escrows': [
          _row(),
          _row(escrowId: 'e-2', orderId: 'ord-2', status: 'dispute_resolved'),
          _row(escrowId: 'e-3', orderId: 'ord-3', status: 'frozen_for_audit'),
        ],
        'hasMore': false,
      },
    );

    final rows = await repo.refreshHistory(userId: 'farmer-001');

    expect(rows.map((p) => p.status).toList(), [
      PaymentStatus.held,
      PaymentStatus.disputed,
      PaymentStatus.unknown,
    ]);
    // Cached, so offline reads keep them.
    online(false);
    final cached = await repo.getHistory(userId: 'farmer-001');
    expect(cached, hasLength(3));
    expect(
      (await repo.getHistory(userId: 'farmer-001', statusFilter: 'held'))
          .single
          .paymentId,
      'escrow-order-ord-1',
    );
  });

  test('the escrow detail parses held', () async {
    online(true);
    when(() => api.get<Map<String, dynamic>>(any())).thenAnswer(
      (_) async => {
        ..._row(),
        'updatedAt': '2026-10-02T10:00:00Z',
        'expiresAt': '2026-10-08T10:00:00Z',
      },
    );

    final payment = await repo.refreshEscrow('escrow-order-ord-1');

    expect(payment!.status, PaymentStatus.held);
    expect(await isOrderEscrowHeld(repo, 'ord-1'), isTrue);
  });

  test('offline, the order list uses the cached held escrows', () async {
    online(false);
    await isar.writeTxn(() async {
      final now = DateTime.now().toUtc();
      for (final (id, orderId, status) in [
        ('escrow-order-ord-1', 'ord-1', PaymentStatus.held),
        ('escrow-order-ord-2', 'ord-2', PaymentStatus.escrowed),
      ]) {
        await isar.payments.put(
          Payment()
            ..paymentId = id
            ..deliveryId = ''
            ..orderId = orderId
            ..farmerId = 'farmer-001'
            ..buyerId = 'buyer-001'
            ..amount = 1500
            ..commission = 37.5
            ..commissionRate = 0.025
            ..farmerPayout = 1462.5
            ..status = status
            ..createdAt = now
            ..updatedAt = now
            ..version = 1,
        );
      }
    });

    expect(
      await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
      {'ord-1'},
    );
    expect(await isOrderEscrowHeld(repo, 'ord-1'), isTrue);
    expect(await isOrderEscrowHeld(repo, 'ord-2'), isFalse);
  });

  test('a 403 FRAUD_ACCOUNT_HELD on initiate keeps its code', () async {
    online(true);
    when(
      () => api.post<Map<String, dynamic>>(
        any(),
        data: any<dynamic>(named: 'data'),
      ),
    ).thenThrow(
      const AuthException('under review', 403, fraudAccountHeldCode),
    );

    final result = await repo.initiatePayment(
      deliveryId: 'del-1',
      buyerId: 'buyer-001',
      farmerId: 'farmer-001',
      amount: 1500,
    );

    expect((result as EscrowError).code, fraudAccountHeldCode);
  });
}
