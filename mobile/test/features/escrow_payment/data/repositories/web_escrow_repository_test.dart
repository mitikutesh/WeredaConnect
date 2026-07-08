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
}
