import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/web_escrow_repository.dart';

class MockApiClient extends Mock implements ApiClient {}

void main() {
  late MockApiClient api;
  late WebEscrowRepository repo;

  setUp(() {
    api = MockApiClient();
    repo = WebEscrowRepository(api: api);
  });

  Map<String, dynamic> escrowDto({String escrowId = 'esc-001'}) => {
        'escrowId': escrowId,
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

  group('WebEscrowRepository payout legs (SIM-15)', () {
    test('parses the trucker leg and keeps the server farmer leg', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/by-delivery/del-001'),
      ).thenAnswer(
        (_) async => {
          ...escrowDto(),
          'amount': 16000.0,
          'truckerPayout': 1000.0,
          'truckerId': 'trucker-001',
        },
      );

      final result = await repo.getEscrowByDelivery('del-001');

      expect(result!.farmerPayout, 14625.0);
      expect(result.truckerPayout, 1000.0);
      expect(result.truckerId, 'trucker-001');
      expect(result.payoutFor('trucker'), 1000.0);
      expect(result.payoutFor('farmer'), 14625.0);
      expect(result.payoutFor('buyer'), 16000.0);
    });

    test('parses the order reference and refund-pending flag (SIM-20)',
        () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/by-delivery/del-001'),
      ).thenAnswer(
        (_) async => {
          ...escrowDto(),
          'orderId': 'ord-1',
          'refundPending': true,
          'refundReason': 'order_cancelled',
        },
      );

      final result = await repo.getEscrowByDelivery('del-001');

      expect(result!.orderId, 'ord-1');
      expect(result.refundPending, isTrue);
      expect(result.refundReason, 'order_cancelled');
      expect(result.isRefundPending, isTrue);
    });

    test('derives the farmer leg for history rows from older servers',
        () async {
      final legacy = escrowDto()..remove('farmerPayout');
      when(
        () => api.get<Map<String, dynamic>>(
          '/escrow/history',
          queryParameters: any(named: 'queryParameters'),
        ),
      ).thenAnswer(
        (_) async => {
          'escrows': [
            legacy,
            {...legacy, 'escrowId': 'esc-002', 'truckerPayout': 1000.0},
          ],
          'hasMore': false,
        },
      );

      final rows = await repo.refreshHistory(
        userId: 'farmer-001',
        roleFilter: 'farmer',
      );

      expect(rows.length, 2);
      expect(rows[0].farmerPayout, 15000.0 - 375.0);
      expect(rows[1].farmerPayout, 15000.0 - 375.0 - 1000.0);
      expect(rows[1].truckerPayout, 1000.0);
    });
  });

  group('WebEscrowRepository.getEscrowByDelivery (F-33)', () {
    test('calls the by-delivery endpoint and returns that escrow', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/by-delivery/del-001'),
      ).thenAnswer((_) async => escrowDto());

      final result = await repo.getEscrowByDelivery('del-001');

      expect(result, isNotNull);
      expect(result!.paymentId, 'esc-001'); // paymentId <- server escrowId
      // Must hit the dedicated endpoint, NOT the unfiltered /escrow/history.
      verify(
        () => api.get<Map<String, dynamic>>('/escrow/by-delivery/del-001'),
      ).called(1);
    });

    test('returns null ONLY for a genuine 404 (no escrow for delivery)',
        () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/by-delivery/del-404'),
      ).thenThrow(const NotFoundException('No escrow'));

      final result = await repo.getEscrowByDelivery('del-404');

      expect(result, isNull);
    });

    // A real request failure must NOT be swallowed into null (which the BLoC
    // would misreport as "payment not found"). It must propagate so the BLoC
    // can surface a distinct, retryable error.
    test('rethrows a 403 (caller is not a party to the escrow)', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/by-delivery/del-403'),
      ).thenThrow(const AuthException('Forbidden', 403));

      await expectLater(
        repo.getEscrowByDelivery('del-403'),
        throwsA(isA<AuthException>()),
      );
    });

    test('rethrows a 401 (unauthenticated)', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/by-delivery/del-401'),
      ).thenThrow(const AuthException('Unauthorized'));

      await expectLater(
        repo.getEscrowByDelivery('del-401'),
        throwsA(isA<AuthException>()),
      );
    });

    test('rethrows a 5xx server error', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/by-delivery/del-500'),
      ).thenThrow(const ServerException('Server error', 500));

      await expectLater(
        repo.getEscrowByDelivery('del-500'),
        throwsA(isA<ServerException>()),
      );
    });

    test('rethrows a network failure', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/by-delivery/del-net'),
      ).thenThrow(const NetworkException());

      await expectLater(
        repo.getEscrowByDelivery('del-net'),
        throwsA(isA<NetworkException>()),
      );
    });
  });

  group('WebEscrowRepository.getEscrow', () {
    test('returns the escrow on success', () async {
      when(() => api.get<Map<String, dynamic>>('/escrow/esc-001'))
          .thenAnswer((_) async => escrowDto());

      final result = await repo.getEscrow('esc-001');

      expect(result, isNotNull);
      expect(result!.paymentId, 'esc-001');
    });

    test('returns null ONLY for a genuine 404', () async {
      when(() => api.get<Map<String, dynamic>>('/escrow/esc-404'))
          .thenThrow(const NotFoundException('No escrow'));

      expect(await repo.getEscrow('esc-404'), isNull);
    });

    // A real failure must propagate so the BLoC surfaces a distinct error
    // instead of a misleading "payment not found".
    test('rethrows a 403', () async {
      when(() => api.get<Map<String, dynamic>>('/escrow/esc-403'))
          .thenThrow(const AuthException('Forbidden', 403));

      await expectLater(
        repo.getEscrow('esc-403'),
        throwsA(isA<AuthException>()),
      );
    });

    test('rethrows a 5xx server error', () async {
      when(() => api.get<Map<String, dynamic>>('/escrow/esc-500'))
          .thenThrow(const ServerException('Server error', 500));

      await expectLater(
        repo.getEscrow('esc-500'),
        throwsA(isA<ServerException>()),
      );
    });
  });

  group('WebEscrowRepository.refreshEscrow (silent fallback)', () {
    test('returns the refreshed escrow on success', () async {
      when(() => api.get<Map<String, dynamic>>('/escrow/esc-001'))
          .thenAnswer((_) async => escrowDto());

      final result = await repo.refreshEscrow('esc-001');

      expect(result, isNotNull);
      expect(result!.paymentId, 'esc-001');
    });

    // Unlike the initial load, a background refresh must NOT throw or surface
    // an error over already-displayed data — it falls back to null silently.
    test('swallows a transient failure to null', () async {
      when(() => api.get<Map<String, dynamic>>('/escrow/esc-500'))
          .thenThrow(const ServerException('Server error', 500));

      expect(await repo.refreshEscrow('esc-500'), isNull);
    });
  });

  group('WebEscrowRepository.initiatePayment', () {
    test('surfaces toPayUrl from an async (real) gateway response', () async {
      when(
        () => api.post<Map<String, dynamic>>(
          '/escrow/initiate',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer(
        (_) async => {
          ...escrowDto(escrowId: 'esc-async'),
          'status': 'initiated',
          'toPayUrl': 'https://pay.example/paygate?prepay_id=XYZ',
        },
      );

      final result = await repo.initiatePayment(
        deliveryId: 'del-001',
        buyerId: 'buyer-001',
        farmerId: 'farmer-001',
        amount: 15000.0,
      );

      expect(result, isA<EscrowSuccess>());
      expect(
        (result as EscrowSuccess).toPayUrl,
        'https://pay.example/paygate?prepay_id=XYZ',
      );
    });

    test('keeps the server code on a 409: ESCROW_024 is funds already held',
        () async {
      // SIM-24: mirrors the mobile repository.
      when(
        () => api.post<Map<String, dynamic>>(
          '/escrow/initiate',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenThrow(
        const ConflictException(
          'Funds for this order are already held in escrow.',
          409,
          'ESCROW_024',
        ),
      );

      final result = await repo.initiatePayment(
        deliveryId: 'del-001',
        buyerId: 'buyer-001',
        farmerId: 'farmer-001',
        amount: 15000.0,
      );

      expect((result as EscrowError).code, 'ESCROW_024');
    });

    test('a 409 without a server code still reads as ESCROW_009', () async {
      when(
        () => api.post<Map<String, dynamic>>(
          '/escrow/initiate',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenThrow(const ConflictException('Conflict'));

      final result = await repo.initiatePayment(
        deliveryId: 'del-001',
        buyerId: 'buyer-001',
        farmerId: 'farmer-001',
        amount: 15000.0,
      );

      expect((result as EscrowError).code, 'ESCROW_009');
    });

    test('toPayUrl is null for a mock/sync gateway response', () async {
      when(
        () => api.post<Map<String, dynamic>>(
          '/escrow/initiate',
          data: any<dynamic>(named: 'data'),
        ),
      ).thenAnswer((_) async => escrowDto());

      final result = await repo.initiatePayment(
        deliveryId: 'del-001',
        buyerId: 'buyer-001',
        farmerId: 'farmer-001',
        amount: 15000.0,
      );

      expect(result, isA<EscrowSuccess>());
      expect((result as EscrowSuccess).toPayUrl, isNull);
    });
  });

  group('WebEscrowRepository step-up (403 AUTH_040)', () {
    void stubPostThrows(String path) {
      when(
        () => api.post<Map<String, dynamic>>(
          path,
          data: any<dynamic>(named: 'data'),
        ),
      ).thenThrow(const StepUpRequiredException('AUTH_040: step-up required'));
    }

    test('initiatePayment maps to the step-up code', () async {
      stubPostThrows('/escrow/initiate');

      final result = await repo.initiatePayment(
        deliveryId: 'del-001',
        buyerId: 'buyer-001',
        farmerId: 'farmer-001',
        amount: 15000,
      );

      expect(result, isA<EscrowError>());
      expect((result as EscrowError).code, stepUpRequiredCode);
    });

    test('raiseDispute maps to the step-up code', () async {
      stubPostThrows('/escrow/dispute');

      final result = await repo.raiseDispute(
        escrowId: 'esc-001',
        disputedBy: 'buyer-001',
        reason: 'damaged',
      );

      expect(result, isA<EscrowError>());
      expect((result as EscrowError).code, stepUpRequiredCode);
    });
  });
}
