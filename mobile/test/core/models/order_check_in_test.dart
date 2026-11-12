import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';

final _pickupDate = DateTime.utc(2026, 11, 20, 6);
final _deadline = DateTime.utc(2026, 11, 21, 6);

/// An accepted trip order the server stamped for the pickup judgement.
Order _order({
  String? judgement = 'pending',
  OrderStatus status = OrderStatus.accepted,
  bool selfPickup = false,
}) =>
    Order()
      ..orderId = 'ord-1'
      ..listingId = 'listing-1'
      ..buyerId = 'buyer-1'
      ..farmerId = 'farmer-1'
      ..buyerName = 'Buyer'
      ..farmerName = 'Farmer'
      ..cropType = 'teff'
      ..quantity = 500
      ..pricePerUnit = 60
      ..totalPrice = 30000
      ..status = status
      ..createdAt = DateTime.utc(2026, 11, 10)
      ..version = 3
      ..tripId = selfPickup ? null : 'trip-1'
      ..truckerId = selfPickup ? null : 'trucker-1'
      ..fulfillment = selfPickup ? 'self_pickup' : 'trucker'
      ..pickupDate = selfPickup ? null : _pickupDate
      ..pickupDeadlineAt = _deadline
      ..pickupJudgement = judgement;

const _fix = HandoffCheckInFix(
  latitude: 8.7523,
  longitude: 38.9785,
  accuracyM: 35,
);

/// ESC-34, the client part (FUP-285): what the order reads from the server,
/// whose check-in each party gives, when, and what is sent.
void main() {
  group('applyOrderCheckInFields (pull, web, conflict echo)', () {
    test('reads the judgement, when it was made and each check-in', () {
      final order = _order(judgement: null);
      applyOrderCommitmentFields(order, {
        'pickupJudgement': 'farmer_no_show',
        'pickupJudgedAt': '2026-11-21T06:01:00Z',
        'checkIns': {
          'crops_ready': {
            'receivedAt': '2026-11-21T07:00:00Z',
            'distanceChecked': false,
            'late': true,
          },
          'arrived_pickup': {
            'receivedAt': '2026-11-20T05:40:00Z',
            'capturedAt': '2026-11-20T05:39:30Z',
            'distanceChecked': true,
            'late': false,
          },
        },
      });

      expect(order.pickupJudgement, 'farmer_no_show');
      expect(order.pickupJudgedAt, DateTime.utc(2026, 11, 21, 6, 1));
      expect(order.checkIns.map((c) => c.kind), [
        'arrived_pickup',
        'crops_ready',
      ]);
      final arrived = checkInOf(order, CheckInKind.arrivedPickup)!;
      expect(arrived.receivedAt, DateTime.utc(2026, 11, 20, 5, 40));
      expect(arrived.capturedAt, DateTime.utc(2026, 11, 20, 5, 39, 30));
      expect(arrived.distanceChecked, isTrue);
      expect(arrived.isLate, isFalse);
      final ready = checkInOf(order, CheckInKind.cropsReady)!;
      expect(ready.isLate, isTrue);
      expect(ready.distanceChecked, isFalse);
      expect(ready.capturedAt, isNull);
    });

    test('a row without them clears the copy; a broken entry is left out', () {
      final order = _order()
        ..checkIns = [OrderCheckIn()..kind = 'arrived_pickup'];
      applyOrderCheckInFields(order, {
        'checkIns': {
          'arrived_pickup': {'distanceChecked': true},
          'crops_ready': 'not a map',
        },
      });
      expect(order.pickupJudgement, isNull);
      expect(order.pickupJudgedAt, isNull);
      expect(order.checkIns, isEmpty);
    });
  });

  group('whose check-in', () {
    test('trip order: the farmer says ready, the trucker arrives', () {
      final order = _order();
      expect(checkInKindFor(order, 'farmer-1'), CheckInKind.cropsReady);
      expect(checkInKindFor(order, 'trucker-1'), CheckInKind.arrivedPickup);
      expect(checkInKindFor(order, 'buyer-1'), isNull);
      expect(checkInKindFor(order, 'stranger'), isNull);
      expect(checkInKindFor(order, ''), isNull);
    });

    test('self-pickup order: the buyer arrives', () {
      final order = _order(selfPickup: true);
      expect(isSelfPickupOrder(order), isTrue);
      expect(checkInKindFor(order, 'buyer-1'), CheckInKind.arrivedPickup);
      expect(checkInKindFor(order, 'farmer-1'), CheckInKind.cropsReady);
    });

    test('a row funded before the commitments without a trip is self-pickup',
        () {
      final order = _order(selfPickup: true)..fulfillment = null;
      expect(isSelfPickupOrder(order), isTrue);
      order.truckerId = 'trucker-1';
      expect(isSelfPickupOrder(order), isFalse);
    });
  });

  group('checkInChanceFor', () {
    final open = _pickupDate.subtract(const Duration(hours: 2));

    test('opens 24 hours before the pickup day, closes at the deadline', () {
      final order = _order();
      expect(
        checkInChanceFor(
          order,
          'trucker-1',
          _pickupDate.subtract(const Duration(hours: 25)),
        ),
        CheckInChance.notYet,
      );
      expect(checkInChanceFor(order, 'trucker-1', open), CheckInChance.open);
      expect(checkInChanceFor(order, 'farmer-1', open), CheckInChance.open);
      expect(
        checkInChanceFor(order, 'trucker-1', _deadline),
        CheckInChance.open,
      );
      expect(
        checkInChanceFor(
          order,
          'trucker-1',
          _deadline.add(const Duration(seconds: 1)),
        ),
        CheckInChance.closed,
      );
    });

    test('a self-pickup order is open from its acceptance', () {
      expect(
        checkInChanceFor(
          _order(selfPickup: true),
          'buyer-1',
          DateTime.utc(2026, 11, 10),
        ),
        CheckInChance.open,
      );
    });

    test('given once the server has it or it waits on the phone', () {
      final received = _order()
        ..checkIns = [
          OrderCheckIn()
            ..kind = 'arrived_pickup'
            ..receivedAt = open,
        ];
      expect(
        checkInChanceFor(received, 'trucker-1', open),
        CheckInChance.given,
      );
      expect(checkInChanceFor(received, 'farmer-1', open), CheckInChance.open);

      final queued = _order()
        ..queuedCheckIns = {'crops_ready': QueuedCheckIn(madeAt: open)};
      expect(checkInChanceFor(queued, 'farmer-1', open), CheckInChance.given);
    });

    test('none without the stamp, once judged, after pickup, for the buyer',
        () {
      expect(
        checkInChanceFor(_order(judgement: null), 'trucker-1', open),
        CheckInChance.none,
      );
      expect(
        checkInChanceFor(_order(judgement: 'scanned'), 'trucker-1', open),
        CheckInChance.none,
      );
      expect(
        checkInChanceFor(
          _order(status: OrderStatus.pickupConfirmed),
          'trucker-1',
          open,
        ),
        CheckInChance.none,
      );
      expect(checkInChanceFor(_order(), 'buyer-1', open), CheckInChance.none);
    });
  });

  group('checkCheckInAllowed (both repositories)', () {
    final open = _pickupDate.subtract(const Duration(hours: 2));

    test('the arrival needs a usable fix; the farmer does not', () {
      expect(
        checkCheckInAllowed(
          _order(),
          'trucker-1',
          CheckInKind.arrivedPickup,
          _fix,
          open,
        ),
        isTrue,
      );
      expect(
        () => checkCheckInAllowed(
          _order(),
          'trucker-1',
          CheckInKind.arrivedPickup,
          null,
          open,
        ),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      expect(
        checkCheckInAllowed(
          _order(),
          'farmer-1',
          CheckInKind.cropsReady,
          null,
          open,
        ),
        isTrue,
      );
    });

    test('the wrong party, kind or window is refused; a repeat is a no-op', () {
      expect(
        () => checkCheckInAllowed(
          _order(),
          'buyer-1',
          CheckInKind.arrivedPickup,
          _fix,
          open,
        ),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      expect(
        () => checkCheckInAllowed(
          _order(),
          'farmer-1',
          CheckInKind.arrivedPickup,
          _fix,
          open,
        ),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      expect(
        () => checkCheckInAllowed(
          _order(),
          'farmer-1',
          CheckInKind.cropsReady,
          null,
          _deadline.add(const Duration(minutes: 1)),
        ),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      expect(
        () => checkCheckInAllowed(
          _order(judgement: null),
          'farmer-1',
          CheckInKind.cropsReady,
          null,
          open,
        ),
        throwsA(isA<OrderActionNotAllowedException>()),
      );
      final given = _order()
        ..queuedCheckIns = {'crops_ready': QueuedCheckIn(madeAt: open)};
      expect(
        checkCheckInAllowed(
          given,
          'farmer-1',
          CheckInKind.cropsReady,
          null,
          open,
        ),
        isFalse,
      );
    });
  });

  group('handoffCheckInData', () {
    final at = DateTime.utc(2026, 11, 20, 6, 40);

    test('exactly the fields the server takes, with a usable fix', () {
      expect(
        handoffCheckInData(
          orderId: 'ord-1',
          kind: 'arrived_pickup',
          capturedAt: at,
          fix: _fix,
        ),
        {
          'orderId': 'ord-1',
          'kind': 'arrived_pickup',
          'capturedAt': '2026-11-20T06:40:00.000Z',
          'latitude': 8.7523,
          'longitude': 38.9785,
          'accuracyM': 35.0,
        },
      );
    });

    test('a fix the server would refuse is left out (the farmer goes on)', () {
      for (final fix in const [
        HandoffCheckInFix(latitude: 51.5, longitude: -0.12),
        HandoffCheckInFix(latitude: 8.75, longitude: 38.97, accuracyM: 6000),
        HandoffCheckInFix(latitude: double.nan, longitude: 38.97),
      ]) {
        expect(isUsableCheckInFix(fix), isFalse);
        expect(
          handoffCheckInData(
            orderId: 'ord-1',
            kind: 'crops_ready',
            capturedAt: at,
            fix: fix,
          ),
          {
            'orderId': 'ord-1',
            'kind': 'crops_ready',
            'capturedAt': '2026-11-20T06:40:00.000Z',
          },
        );
      }
    });

    test('no accuracy is sent without one', () {
      final data = handoffCheckInData(
        orderId: 'ord-1',
        kind: 'crops_ready',
        capturedAt: at,
        fix: const HandoffCheckInFix(latitude: 8.75, longitude: 38.97),
      );
      expect(data.containsKey('accuracyM'), isFalse);
      expect(data['latitude'], 8.75);
    });
  });

  group("the buyer's deadline cancel", () {
    final after = _deadline.add(const Duration(hours: 1));

    test('is not offered on a stamped order, whatever the judgement', () {
      for (final judgement in ['pending', 'both_present_no_scan']) {
        final order = _order(judgement: judgement);
        expect(isTransportFailureCancelOpen(order, after), isFalse);
        expect(canCancelOrder(order, 'buyer-1', after), isFalse);
      }
    });

    test('stays on an order accepted without the stamp', () {
      final order = _order(judgement: null);
      expect(isTransportFailureCancelOpen(order, after), isTrue);
      expect(canCancelOrder(order, 'buyer-1', after), isTrue);
    });
  });
}
