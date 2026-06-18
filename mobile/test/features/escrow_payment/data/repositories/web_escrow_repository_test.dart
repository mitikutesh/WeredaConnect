import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:wereda_connect/core/network/api_client.dart';
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

    test('returns null when the delivery has no escrow (404)', () async {
      when(
        () => api.get<Map<String, dynamic>>('/escrow/by-delivery/del-404'),
      ).thenThrow(Exception('404 not found'));

      final result = await repo.getEscrowByDelivery('del-404');

      expect(result, isNull);
    });
  });
}
