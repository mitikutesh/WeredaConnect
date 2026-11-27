import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// FUP-58: the server stores order timestamps as UTC. An order created at
/// 02:30 on 23 Oct in Ethiopia comes back as 2026-10-22T23:30:00.000Z. Web
/// kept it in UTC and formatted it, so it showed 22 Oct (the previous day).
// intl renders a narrow no-break space (U+202F) before AM/PM; normalize
// it so the literal expectations below stay readable.
String _norm(String s) => s.replaceAll('\u202f', ' ').replaceAll('\u00a0', ' ');

void main() {
  const stored = '2026-10-22T23:30:00.000Z';

  Map<String, dynamic> orderDto() => {
        'orderId': 'ord-1',
        'listingId': 'listing-1',
        'buyerId': 'buyer-1',
        'farmerId': 'farmer-1',
        'buyerName': 'Buyer',
        'farmerName': 'Farmer',
        'cropType': 'teff',
        'quantity': 100.0,
        'pricePerUnit': 50.0,
        'totalPrice': 5000.0,
        'status': 'accepted',
        'createdAt': stored,
        'pickupDeadlineAt': stored,
        'version': 1,
      };

  late _MockApiClient api;
  late WebOrderRepository repository;

  setUp(() {
    api = _MockApiClient();
    repository = WebOrderRepository(api: api);
  });

  test('createdAt and the pickup deadline show Ethiopia date and time',
      () async {
    when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
        .thenAnswer((_) async => {'order': orderDto()});

    final order = (await repository.getOrderById('ord-1'))!;

    // The parse keeps the exact instant: status and sorting still compare it.
    expect(order.createdAt.isAtSameMomentAs(DateTime.parse(stored)), isTrue);
    expect(
      order.pickupDeadlineAt!.isAtSameMomentAs(DateTime.parse(stored)),
      isTrue,
    );

    // Order card (date only) shows the Ethiopia day, not the UTC day.
    expect(
      DateFormat.yMMMd().format(dayInEthiopia(order.createdAt)),
      'Oct 23, 2026',
    );
    expect(DateFormat.yMMMd().format(order.createdAt.toUtc()), 'Oct 22, 2026');

    // Order detail (date and time) shows the Ethiopia wall clock.
    final detail = DateFormat.yMMMd().add_jm();
    expect(
      _norm(detail.format(instantInEthiopia(order.createdAt))),
      'Oct 23, 2026 2:30 AM',
    );
    expect(
      _norm(detail.format(instantInEthiopia(order.pickupDeadlineAt!))),
      'Oct 23, 2026 2:30 AM',
    );
  });
}
