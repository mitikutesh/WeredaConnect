import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/order/data/repositories/web_order_repository.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class _MockApiClient extends Mock implements ApiClient {}

const _fix = HandoffCheckInFix(
  latitude: 8.7523,
  longitude: 38.9785,
  accuracyM: 35,
);

/// ESC-34 on web (FUP-285): the order reads the judgement and the
/// check-ins, a check-in is sent at once with exactly the server's shape,
/// and each refusal comes back with its own code and text.
void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  late _MockApiClient api;
  late WebOrderRepository repo;
  Map<String, dynamic>? sent;
  var answer = <String, dynamic>{'status': 'success', 'serverVersion': 4};

  Map<String, dynamic> orderDto({
    String? judgement = 'pending',
    bool selfPickup = false,
    Map<String, dynamic>? checkIns,
  }) {
    final now = DateTime.now().toUtc();
    return {
      'orderId': 'ord-1',
      'listingId': 'listing-1',
      'buyerId': 'buyer-1',
      'farmerId': 'farmer-1',
      'buyerName': 'Buyer',
      'farmerName': 'Farmer',
      'cropType': 'teff',
      'quantity': 500.0,
      'pricePerUnit': 60.0,
      'status': 'accepted',
      'createdAt': '2026-10-30T08:00:00Z',
      'version': 4,
      'fulfillment': selfPickup ? 'self_pickup' : 'trucker',
      if (!selfPickup) 'tripId': 'trip-1',
      if (!selfPickup) 'truckerId': 'trucker-1',
      if (!selfPickup)
        'pickupDate': now.subtract(const Duration(hours: 1)).toIso8601String(),
      'pickupDeadlineAt': now.add(const Duration(hours: 6)).toIso8601String(),
      if (judgement != null) 'pickupJudgement': judgement,
      if (checkIns != null) 'checkIns': checkIns,
    };
  }

  Map<String, dynamic> sentOp() =>
      (sent!['operations'] as List).single as Map<String, dynamic>;

  void serverHas(Map<String, dynamic> order) =>
      when(() => api.get<Map<String, dynamic>>('/orders/ord-1'))
          .thenAnswer((_) async => {'order': order});

  setUp(() {
    api = _MockApiClient();
    repo = WebOrderRepository(api: api);
    sent = null;
    answer = {'status': 'success', 'serverVersion': 4};
    when(
      () => api.post<Map<String, dynamic>>(
        '/sync',
        data: any<dynamic>(named: 'data'),
      ),
    ).thenAnswer((invocation) async {
      sent = invocation.namedArguments[#data] as Map<String, dynamic>;
      return {
        'results': [
          {'operationId': sentOp()['operationId'], ...answer},
        ],
      };
    });
  });

  test('reads the judgement and the check-ins of a server order', () async {
    serverHas(
      orderDto(
        judgement: 'both_present_no_scan',
        checkIns: {
          'arrived_pickup': {
            'receivedAt': '2026-11-20T05:40:00Z',
            'distanceChecked': true,
            'late': false,
          },
        },
      )..['pickupJudgedAt'] = '2026-11-21T06:01:00Z',
    );

    final order = (await repo.getOrderById('ord-1'))!;

    expect(order.pickupJudgement, 'both_present_no_scan');
    expect(order.pickupJudgedAt, DateTime.utc(2026, 11, 21, 6, 1));
    expect(checkInOf(order, 'arrived_pickup')!.distanceChecked, isTrue);
    expect(order.queuedCheckIns, isEmpty);
  });

  test("the trucker's arrival is sent at once with the server's shape",
      () async {
    serverHas(orderDto());

    await repo.checkIn(
      'ord-1',
      'trucker-1',
      kind: CheckInKind.arrivedPickup,
      fix: _fix,
    );

    final op = sentOp();
    expect(op['entityType'], 'handoff_check_in');
    expect(op['entityId'], 'ord-1#arrived_pickup');
    expect(op['action'], 'create');
    final data = op['data'] as Map<String, dynamic>;
    expect(data.keys.toSet(), {
      'orderId',
      'kind',
      'capturedAt',
      'latitude',
      'longitude',
      'accuracyM',
    });
    expect(data['latitude'], 8.7523);
    expect(data['accuracyM'], 35.0);
  });

  test("the farmer's crops are ready without a fix", () async {
    serverHas(orderDto());

    expect(
      await repo.checkIn('ord-1', 'farmer-1', kind: CheckInKind.cropsReady),
      isTrue,
    );

    expect(
      (sentOp()['data'] as Map).keys.toSet(),
      {'orderId', 'kind', 'capturedAt'},
    );
  });

  test('the self-pickup buyer arrives', () async {
    serverHas(orderDto(selfPickup: true));

    await repo.checkIn(
      'ord-1',
      'buyer-1',
      kind: CheckInKind.arrivedPickup,
      fix: _fix,
    );

    expect(sentOp()['entityId'], 'ord-1#arrived_pickup');
  });

  test('one the server has already is not sent again', () async {
    serverHas(
      orderDto(
        checkIns: {
          'crops_ready': {
            'receivedAt': '2026-11-20T05:40:00Z',
            'distanceChecked': false,
            'late': false,
          },
        },
      ),
    );

    expect(
      await repo.checkIn('ord-1', 'farmer-1', kind: CheckInKind.cropsReady),
      isFalse,
    );

    expect(sent, isNull);
  });

  test('the mobile guards hold: no fix, the wrong party, no stamp', () async {
    serverHas(orderDto());
    await expectLater(
      repo.checkIn('ord-1', 'trucker-1', kind: CheckInKind.arrivedPickup),
      throwsA(isA<OrderActionNotAllowedException>()),
    );
    await expectLater(
      repo.checkIn(
        'ord-1',
        'buyer-1',
        kind: CheckInKind.arrivedPickup,
        fix: _fix,
      ),
      throwsA(isA<OrderActionNotAllowedException>()),
    );
    serverHas(orderDto(judgement: null));
    await expectLater(
      repo.checkIn('ord-1', 'farmer-1', kind: CheckInKind.cropsReady),
      throwsA(isA<OrderActionNotAllowedException>()),
    );
    expect(sent, isNull);
  });

  group('each refusal has its own code and text', () {
    final cases = <String, (AppErrorCode, String)>{
      'CHECKIN_VALIDATION: kind must be arrived_pickup or crops_ready.': (
        AppErrorCode.checkInNotSaved,
        en.errorCheckInValidation,
      ),
      "CHECKIN_NOT_PARTY: Only the order's trucker says they have arrived.": (
        AppErrorCode.checkInNotParty,
        en.errorCheckInNotParty,
      ),
      'CHECKIN_NOT_OPEN: Arrival check-ins are not open for this order.': (
        AppErrorCode.checkInNotOpen,
        en.errorCheckInNotOpen,
      ),
      'CHECKIN_TOO_EARLY: Check-ins open at 2026-11-19T06:00:00Z.': (
        AppErrorCode.checkInTooEarly,
        en.errorCheckInTooEarly,
      ),
      'CHECKIN_TOO_FAR: You are 7.2 km from the farm (at most 5 km).': (
        AppErrorCode.checkInTooFar,
        en.errorCheckInTooFar,
      ),
      'CHECKIN_TOO_LATE: It was kept for review.': (
        AppErrorCode.checkInTooLate,
        en.errorCheckInTooLate,
      ),
      "Unsupported entity type: 'handoff_check_in'.": (
        AppErrorCode.checkInNotOpen,
        en.errorCheckInNotOpen,
      ),
    };

    for (final MapEntry(key: message, value: (code, text)) in cases.entries) {
      test(message.split(':').first, () async {
        serverHas(orderDto());
        answer = {'status': 'error', 'message': message};

        Object? error;
        try {
          await repo.checkIn(
            'ord-1',
            'trucker-1',
            kind: CheckInKind.arrivedPickup,
            fix: _fix,
          );
        } on Object catch (e) {
          error = e;
        }

        expect(error, isA<SyncRejectedException>());
        expect(mapErrorToCode(error!), code);
        expect(localizeErrorCode(code, en), text);
      });
    }
  });

  // FUP-327, FUP-371: web sends a check-in at once and keeps no queue, so
  // Home's orders are as the server gave them, with no request.
  test('Home: no queued check-ins on web, and nothing is read', () async {
    serverHas(orderDto());
    final order = (await repo.getOrderById('ord-1'))!;
    clearInteractions(api);

    final orders = await repo.ordersWithQueuedCheckIns([order], 'trucker-1');

    expect(orders.single, same(order));
    expect(orders.single.queuedCheckIns, isEmpty);
    verifyZeroInteractions(api);
  });

  test('another entity unsupported is not read as a check-in refusal', () {
    expect(
      mapErrorToCode(
        const SyncRejectedException("Unsupported entity type: 'gadget'."),
      ),
      AppErrorCode.server,
    );
  });
}
