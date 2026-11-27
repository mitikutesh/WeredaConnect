import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:isar/isar.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/payment.dart';
import 'package:wereda_connect/core/database/schemas/payment_intent.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_config.dart';
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

  Future<void> cache(List<(String, String, PaymentStatus)> rows) =>
      isar.writeTxn(() async {
        final now = DateTime.now().toUtc();
        for (final (id, orderId, status) in rows) {
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

  test('offline, the order list uses the cached held escrows', () async {
    online(false);
    await cache([
      ('escrow-order-ord-1', 'ord-1', PaymentStatus.held),
      ('escrow-order-ord-2', 'ord-2', PaymentStatus.escrowed),
    ]);

    expect(
      await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
      {'ord-1'},
    );
    expect(await isOrderEscrowHeld(repo, 'ord-1'), isTrue);
    expect(await isOrderEscrowHeld(repo, 'ord-2'), isFalse);
  });

  // FUP-79: the held read pages on (the server applies the limit before
  // the status filter), and re-reads a cached held escrow it no longer lists.
  group('held read', () {
    List<Map<String, dynamic>> answerPages(List<Map<String, dynamic>> pages) {
      final asked = <Map<String, dynamic>>[];
      when(
        () => api.get<Map<String, dynamic>>(
          ApiConfig.escrowHistory,
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer((call) async {
        asked.add(
          call.namedArguments[#queryParameters] as Map<String, dynamic>,
        );
        return pages[asked.length - 1];
      });
      return asked;
    }

    void answerDetail(String escrowId, String status) => when(
          () => api.get<Map<String, dynamic>>('/escrow/$escrowId'),
        ).thenAnswer(
          (_) async => {
            ..._row(escrowId: escrowId, status: status),
            'updatedAt': '2026-10-02T10:00:00Z',
            'expiresAt': '2026-10-08T10:00:00Z',
          },
        );

    test('a held escrow on a later page is flagged and cached', () async {
      online(true);
      final asked = answerPages([
        {'escrows': <Object>[], 'hasMore': true, 'nextToken': 't-1'},
        {
          'escrows': [_row(escrowId: 'escrow-order-ord-9', orderId: 'ord-9')],
          'hasMore': false,
        },
      ]);

      expect(
        await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
        {'ord-9'},
      );
      expect(asked, hasLength(2));
      expect(asked.last['nextToken'], 't-1');
      expect(asked.last['status'], 'held');
      expect(asked.last['role'], 'farmer');

      online(false);
      expect(
        await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
        {'ord-9'},
      );
    });

    test(
        'a cached held escrow an admin refunded is read again, '
        'so offline it no longer reads Under Review', () async {
      await cache([('escrow-order-ord-1', 'ord-1', PaymentStatus.held)]);
      online(true);
      answerPages([
        {'escrows': <Object>[], 'hasMore': false},
      ]);
      answerDetail('escrow-order-ord-1', 'refunded');

      expect(
        await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
        isEmpty,
      );
      verify(
        () => api.get<Map<String, dynamic>>('/escrow/escrow-order-ord-1'),
      ).called(1);

      online(false);
      expect(
        await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
        isEmpty,
      );
      expect(await isOrderEscrowHeld(repo, 'ord-1'), isFalse);
    });

    test('a cached escrow still held past the pages read counts', () async {
      await cache([('escrow-order-ord-1', 'ord-1', PaymentStatus.held)]);
      online(true);
      answerPages([
        for (var i = 0; i < heldHistoryMaxPages; i++)
          {'escrows': <Object>[], 'hasMore': true, 'nextToken': 't-$i'},
      ]);
      answerDetail('escrow-order-ord-1', 'held');

      expect(
        await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
        {'ord-1'},
      );
    });

    test('a listed held escrow is not read again', () async {
      await cache([('escrow-order-ord-1', 'ord-1', PaymentStatus.held)]);
      online(true);
      answerPages([
        {
          'escrows': [_row()],
          'hasMore': false,
        },
      ]);

      expect(
        await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
        {'ord-1'},
      );
      verifyNever(
        () => api.get<Map<String, dynamic>>('/escrow/escrow-order-ord-1'),
      );
    });

    test('a failed re-read leaves the cached row and does not flag it online',
        () async {
      await cache([('escrow-order-ord-1', 'ord-1', PaymentStatus.held)]);
      online(true);
      answerPages([
        {'escrows': <Object>[], 'hasMore': false},
      ]);
      when(
        () => api.get<Map<String, dynamic>>('/escrow/escrow-order-ord-1'),
      ).thenThrow(const ServerException('boom', 500));

      expect(
        await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
        isEmpty,
      );
      online(false);
      expect(
        await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
        {'ord-1'},
      );
    });

    test('at most heldRereadMax cached rows are read again per load', () async {
      await cache([
        for (var i = 1; i <= heldRereadMax + 2; i++)
          ('escrow-order-ord-$i', 'ord-$i', PaymentStatus.held),
      ]);
      online(true);
      answerPages([
        {'escrows': <Object>[], 'hasMore': false},
      ]);
      for (var i = 1; i <= heldRereadMax + 2; i++) {
        answerDetail('escrow-order-ord-$i', 'released');
      }

      await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer');

      final reads = verify(
        () => api.get<Map<String, dynamic>>(
          any(that: startsWith('/escrow/escrow-order-ord-')),
        ),
      ).callCount;
      expect(reads, heldRereadMax);
    });

    test('the first failed re-read ends the re-reads of that load', () async {
      await cache([
        ('escrow-order-ord-1', 'ord-1', PaymentStatus.held),
        ('escrow-order-ord-2', 'ord-2', PaymentStatus.held),
        ('escrow-order-ord-3', 'ord-3', PaymentStatus.held),
      ]);
      online(true);
      answerPages([
        {'escrows': <Object>[], 'hasMore': false},
      ]);
      when(
        () => api.get<Map<String, dynamic>>(
          any(that: startsWith('/escrow/escrow-order-ord-')),
        ),
      ).thenThrow(const NetworkException('offline'));

      expect(
        await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
        isEmpty,
      );
      verify(
        () => api.get<Map<String, dynamic>>(
          any(that: startsWith('/escrow/escrow-order-ord-')),
        ),
      ).called(1);
    });

    test('a failed page falls back to the cached held escrows', () async {
      await cache([('escrow-order-ord-1', 'ord-1', PaymentStatus.held)]);
      online(true);
      when(
        () => api.get<Map<String, dynamic>>(
          ApiConfig.escrowHistory,
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenThrow(const NetworkException());

      expect(
        await heldOrderIds(repo, userId: 'farmer-001', role: 'farmer'),
        {'ord-1'},
      );
    });
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
