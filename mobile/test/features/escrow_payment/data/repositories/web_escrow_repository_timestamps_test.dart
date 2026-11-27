import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/web_escrow_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// FUP-58: a payment's createdAt and expiresAt come back as UTC. A payment
/// created at 02:30 on 23 Oct in Ethiopia is 2026-10-22T23:30:00.000Z, which
/// web kept in UTC and showed as 22 Oct.
// intl renders a narrow no-break space (U+202F) before AM/PM; normalize
// it so the literal expectations below stay readable.
String _norm(String s) => s.replaceAll('\u202f', ' ').replaceAll('\u00a0', ' ');

void main() {
  const stored = '2026-10-22T23:30:00.000Z';

  Map<String, dynamic> paymentDto() => {
        'escrowId': 'esc-1',
        'deliveryId': 'del-1',
        'farmerId': 'farmer-1',
        'buyerId': 'buyer-1',
        'amount': 5000.0,
        'commission': 125.0,
        'status': 'escrowed',
        'createdAt': stored,
        'updatedAt': stored,
        'expiresAt': stored,
      };

  late _MockApiClient api;
  late WebEscrowRepository repository;

  setUp(() {
    api = _MockApiClient();
    repository = WebEscrowRepository(api: api);
  });

  test('createdAt and expiresAt show Ethiopia date and time', () async {
    when(() => api.get<Map<String, dynamic>>('/escrow/esc-1'))
        .thenAnswer((_) async => paymentDto());

    final payment = (await repository.getEscrow('esc-1'))!;

    expect(payment.createdAt.isAtSameMomentAs(DateTime.parse(stored)), isTrue);
    expect(payment.expiresAt!.isAtSameMomentAs(DateTime.parse(stored)), isTrue);

    // Escrow status screen and timeline (date and time).
    final detail = DateFormat.yMMMd().add_jm();
    expect(
      _norm(detail.format(instantInEthiopia(payment.createdAt))),
      'Oct 23, 2026 2:30 AM',
    );
    expect(
      _norm(detail.format(instantInEthiopia(payment.expiresAt!))),
      'Oct 23, 2026 2:30 AM',
    );

    // Payment history list (date only).
    expect(
      DateFormat.yMMMd().format(dayInEthiopia(payment.createdAt)),
      'Oct 23, 2026',
    );
    expect(
      DateFormat.yMMMd().format(payment.createdAt.toUtc()),
      'Oct 22, 2026',
    );
  });
}
