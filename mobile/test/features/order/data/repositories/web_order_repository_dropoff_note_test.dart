import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';

class _MockApiClient extends Mock implements ApiClient {}

/// LT-12 on web (no local copy, direct `/sync`): the drop-off landmark goes
/// out with the order create, the buyer's change carries the order's current
/// version, the mobile guards hold, and the server's refusals come back as
/// their own codes.
void main() {
  late _MockApiClient api;
  late WebOrderRepository repo;
  Map<String, dynamic>? sent;
  var answer = <String, dynamic>{'status': 'success'};

  Map<String, dynamic> orderDto({
    String status = 'accepted',
    String? note = 'Merkato',
    bool trip = true,
  }) =>
      {
        'orderId': 'ord-1',
        'listingId': 'listing-1',
        'buyerId': 'buyer-1',
        'farmerId': 'farmer-1',
        'buyerName': 'Buyer',
        'farmerName': 'Farmer',
        'cropType': 'teff',
        'quantity': 500.0,
        'pricePerUnit': 60.0,
        'status': status,
        'createdAt': '2026-10-30T08:00:00Z',
        'version': 4,
        if (trip) 'tripId': 'trip-1',
        if (trip) 'truckerId': 'trucker-1',
        if (note != null) 'dropoffNote': note,
      };

  Map<String, dynamic> sentData() =>
      ((sent!['operations'] as List).single as Map<String, dynamic>)['data']
          as Map<String, dynamic>;

  void serverHas(Map<String, dynamic> order) =>
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
          .thenAnswer((_) async => {'order': order});

  setUp(() {
    api = _MockApiClient();
    repo = WebOrderRepository(api: api);
    sent = null;
    answer = {'status': 'success'};
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      sent = invocation.namedArguments[#data] as Map<String, dynamic>;
      return {
        'results': [answer],
      };
    });
  });

  test('parses the note of a server order', () async {
    serverHas(orderDto(note: 'Merkato, near Anwar Mosque'));

    final order = await repo.getOrderById('ord-1');

    expect(order!.dropoffNote, 'Merkato, near Anwar Mosque');
  });

  test('a server order without the note has none', () async {
    serverHas(orderDto(note: null));

    expect((await repo.getOrderById('ord-1'))!.dropoffNote, isNull);
  });

  group('order create', () {
    Future<void> create(String? note) => repo.createOrder(
          listingId: 'listing-1',
          buyerId: 'buyer-1',
          farmerId: 'farmer-1',
          buyerName: 'Buyer',
          farmerName: 'Farmer',
          cropType: 'teff',
          quantity: 500,
          pricePerUnit: 60,
          tripId: 'trip-1',
          truckerId: 'trucker-1',
          truckerName: 'Trucker',
          transportCost: 600,
          dropoffNote: note,
        );

    test('sends the note trimmed', () async {
      await create(' Merkato, near Anwar Mosque ');

      expect(sentData()['dropoffNote'], 'Merkato, near Anwar Mosque');
    });

    test('leaves a blank note out', () async {
      await create('');

      expect(sentData().containsKey('dropoffNote'), isFalse);
    });

    test('a phone number refusal reads as the contact text', () async {
      answer = {
        'status': 'error',
        'message': 'ORDER_CONTACT_IN_TEXT: Remove the phone number from '
            'dropoffNote. Contact opens after payment is held in escrow.',
      };

      await expectLater(
        create('call 0911234567'),
        throwsA(
          isA<SyncRejectedException>().having(
            mapErrorToCode,
            'code',
            AppErrorCode.contactInText,
          ),
        ),
      );
    });
  });

  group('the buyer changes it', () {
    test('with the current version, before pickup', () async {
      serverHas(orderDto());

      await repo.updateDropoffNote('ord-1', 'buyer-1', ' Kality ');

      final op = (sent!['operations'] as List).single as Map<String, dynamic>;
      expect(op['entityType'], 'order');
      expect(op['action'], 'update');
      expect(sentData(), {'dropoffNote': 'Kality', 'version': 4});
    });

    test('clears it with an empty note', () async {
      serverHas(orderDto());

      await repo.updateDropoffNote('ord-1', 'buyer-1', null);

      expect(sentData()['dropoffNote'], '');
    });

    test('the same note again sends nothing', () async {
      serverHas(orderDto());

      await repo.updateDropoffNote('ord-1', 'buyer-1', 'Merkato ');

      expect(sent, isNull);
    });

    test('a refusal after pickup reads as its own code', () async {
      serverHas(orderDto());
      answer = {
        'status': 'error',
        'message': 'ORDER_DROPOFF_NOTE_LOCKED: The drop-off note can only be '
            'changed before pickup.',
      };

      await expectLater(
        repo.updateDropoffNote('ord-1', 'buyer-1', 'Kality'),
        throwsA(
          isA<SyncRejectedException>().having(
            mapErrorToCode,
            'code',
            AppErrorCode.dropoffNoteLocked,
          ),
        ),
      );
    });
  });

  group('refused before anything is sent', () {
    test('after pickup', () async {
      serverHas(orderDto(status: 'in_transit'));

      await expectLater(
        repo.updateDropoffNote('ord-1', 'buyer-1', 'Kality'),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      expect(sent, isNull);
    });

    test('the farmer or the trucker', () async {
      serverHas(orderDto());

      for (final caller in ['farmer-1', 'trucker-1']) {
        await expectLater(
          repo.updateDropoffNote('ord-1', caller, 'Kality'),
          throwsA(isA<OrderActionNotAllowedException>()),
        );
      }
      expect(sent, isNull);
    });

    test('a self-pickup order', () async {
      serverHas(orderDto(trip: false));

      await expectLater(
        repo.updateDropoffNote('ord-1', 'buyer-1', 'Kality'),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      expect(sent, isNull);
    });
  });
}
