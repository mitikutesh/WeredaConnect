import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/web_escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/domain/escrow_hold.dart';

class MockApiClient extends Mock implements ApiClient {}

Map<String, dynamic> _escrow({
  String escrowId = 'escrow-order-ord-1',
  String? orderId = 'ord-1',
  String status = 'held',
}) =>
    {
      'escrowId': escrowId,
      'deliveryId': '',
      if (orderId != null) 'orderId': orderId,
      'buyerId': 'buyer-001',
      'farmerId': 'farmer-001',
      'amount': 1500.0,
      'commission': 37.5,
      'farmerPayout': 1462.5,
      'currency': 'ETB',
      'status': status,
      'createdAt': '2026-10-01T10:00:00Z',
    };

/// FRD-06 on web: the escrow status is read tolerantly (never `initiated` for
/// a value the app does not know), `held` reaches the order screens, and
/// FRAUD_ACCOUNT_HELD keeps its code.
void main() {
  late MockApiClient api;
  late WebEscrowRepository repo;

  setUp(() {
    api = MockApiClient();
    repo = WebEscrowRepository(api: api);
  });

  void answerHistory(List<Map<String, dynamic>> rows) {
    when(
      () => api.get<Map<String, dynamic>>(
        '/escrow/history',
        queryParameters: any(named: 'queryParameters'),
      ),
    ).thenAnswer((_) async => {'escrows': rows, 'hasMore': false});
  }

  group('status parsing', () {
    test('held, dispute_resolved and an unknown value never read initiated',
        () async {
      answerHistory([
        _escrow(),
        _escrow(escrowId: 'e-2', status: 'dispute_resolved'),
        _escrow(escrowId: 'e-3', status: 'frozen_for_audit'),
      ]);

      final rows = await repo.refreshHistory(userId: 'farmer-001');

      expect(rows.map((p) => p.status).toList(), [
        PaymentStatus.held,
        PaymentStatus.disputed,
        PaymentStatus.unknown,
      ]);
    });

    test('a held escrow detail reads held', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/escrow-order-ord-1'),
      ).thenAnswer((_) async => _escrow());

      final payment = await repo.getEscrow('escrow-order-ord-1');

      expect(payment!.status, PaymentStatus.held);
    });
  });

  test('a 403 FRAUD_ACCOUNT_HELD on initiate keeps its code', () async {
    when(
      () => api.post<Map<String, dynamic>>(
        '/escrow/initiate',
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

  group('heldOrderIds', () {
    for (final role in const ['buyer', 'farmer', 'trucker']) {
      test('$role: asks for this party\'s held escrows only', () async {
        answerHistory([
          _escrow(),
          _escrow(escrowId: 'e-2', orderId: 'ord-2', status: 'escrowed'),
          _escrow(escrowId: 'e-3', orderId: null),
        ]);

        final held = await heldOrderIds(repo, userId: '$role-001', role: role);

        expect(held, {'ord-1'});
        final params = verify(
          () => api.get<Map<String, dynamic>>(
            '/escrow/history',
            queryParameters: captureAny(named: 'queryParameters'),
          ),
        ).captured.single as Map<String, dynamic>;
        expect(params['status'], 'held');
        expect(params['role'], role);
        expect(params['limit'], '50');
      });
    }

    test('a failed read flags nothing', () async {
      when(
        () => api.get<Map<String, dynamic>>(
          '/escrow/history',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenThrow(const NetworkException());

      expect(
        await heldOrderIds(repo, userId: 'buyer-001', role: 'buyer'),
        isEmpty,
      );
    });
  });

  group('isOrderEscrowHeld', () {
    test('reads the order escrow by its id', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/escrow-order-ord-1'),
      ).thenAnswer((_) async => _escrow());

      expect(await isOrderEscrowHeld(repo, 'ord-1'), isTrue);
    });

    test('an escrowed order escrow is not under review', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/escrow-order-ord-1'),
      ).thenAnswer((_) async => _escrow(status: 'escrowed'));

      expect(await isOrderEscrowHeld(repo, 'ord-1'), isFalse);
    });

    test('no escrow, or a failed read, is not under review', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/escrow-order-ord-1'),
      ).thenThrow(const NotFoundException());
      expect(await isOrderEscrowHeld(repo, 'ord-1'), isFalse);

      when(
        () => api.get<Map<String, dynamic>>('/escrow/escrow-order-ord-1'),
      ).thenThrow(const ServerException('boom', 500));
      expect(await isOrderEscrowHeld(repo, 'ord-1'), isFalse);
    });
  });
}
