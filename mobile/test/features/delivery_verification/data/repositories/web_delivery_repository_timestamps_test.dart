import 'package:flutter_test/flutter_test.dart';
import 'package:intl/intl.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/web_delivery_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// FUP-58: delivery timestamps come back from the server as UTC. A pickup or
/// confirmation at 02:30 on 23 Oct in Ethiopia is 2026-10-22T23:30:00.000Z,
/// which web kept in UTC and showed as 22 Oct.
// intl renders a narrow no-break space (U+202F) before AM/PM; normalize
// it so the literal expectations below stay readable.
String _norm(String s) => s.replaceAll('\u202f', ' ').replaceAll('\u00a0', ' ');

void main() {
  const stored = '2026-10-22T23:30:00.000Z';

  Map<String, dynamic> deliveryDto() => {
        'deliveryId': 'del-1',
        'tripId': 'trip-1',
        'listingId': 'listing-1',
        'farmerId': 'farmer-1',
        'farmerName': 'Farmer',
        'truckerId': 'trucker-1',
        'truckerName': 'Trucker',
        'buyerId': 'buyer-1',
        'buyerName': 'Buyer',
        'qrToken': 'qr',
        'status': 'delivered',
        'pickedUpAt': stored,
        'confirmedAt': stored,
        'createdAt': stored,
        'updatedAt': stored,
        'version': 1,
      };

  late _MockApiClient api;
  late WebDeliveryRepository repository;

  setUp(() {
    api = _MockApiClient();
    repository = WebDeliveryRepository(api: api);
  });

  test('pickedUpAt, confirmedAt and createdAt show Ethiopia time', () async {
    when(() => api.get<Map<String, dynamic>>('/deliveries/del-1'))
        .thenAnswer((_) async => {'delivery': deliveryDto()});

    final delivery =
        (await repository.getDeliveryById('del-1', userId: 'buyer-1'))!;

    expect(
      delivery.pickedUpAt!.isAtSameMomentAs(DateTime.parse(stored)),
      isTrue,
    );
    expect(
      delivery.confirmedAt!.isAtSameMomentAs(DateTime.parse(stored)),
      isTrue,
    );

    // Delivery detail (date and time).
    final detail = DateFormat.yMMMd().add_jm();
    expect(
      _norm(detail.format(instantInEthiopia(delivery.pickedUpAt!))),
      'Oct 23, 2026 2:30 AM',
    );
    expect(
      _norm(detail.format(instantInEthiopia(delivery.confirmedAt!))),
      'Oct 23, 2026 2:30 AM',
    );

    // Delivery card (date only).
    expect(
      DateFormat.yMMMd().format(dayInEthiopia(delivery.createdAt)),
      'Oct 23, 2026',
    );
    expect(
      DateFormat.yMMMd().format(delivery.createdAt.toUtc()),
      'Oct 22, 2026',
    );
  });
}
