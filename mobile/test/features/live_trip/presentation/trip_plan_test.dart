import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/dropoff_handoff_store.dart';
import 'package:wereda_connect/features/live_trip/domain/trip_plan.dart';

import 'live_trip_ui_fixtures.dart';

/// LT-09: the trucker's trip as trip mode lists it.
void main() {
  final morning = DateTime.utc(2026, 10, 28, 5);

  // LT-12: the buyer's landmark belongs to a drop-off still to do.
  group('TripStop.dropoffNote', () {
    TripPlan plan(DeliveryStatus status, {String? note}) => TripPlan.build(
          trip: tripFixture(),
          deliveries: [
            deliveryFixture(
              'd-1',
              status: status,
              farmer: 'Almaz',
              buyer: 'Hawi Flour Mill',
              pickedUpAt: status == DeliveryStatus.pending ? null : morning,
              confirmedAt: status == DeliveryStatus.delivered
                  ? morning.add(const Duration(hours: 3))
                  : null,
            ),
          ],
          orders: {
            'o-d-1': orderFixture(
              'd-1',
              crop: 'wheat',
              kg: 2000,
              dropoffNote: note,
            ),
          },
        );

    TripStop stop(TripPlan p, TripStopKind kind) =>
        p.stops.firstWhere((s) => s.kind == kind);

    test('at the drop-off, trimmed; never at the pickup', () {
      final p = plan(DeliveryStatus.inTransit, note: ' Merkato, gate 2 ');

      expect(stop(p, TripStopKind.dropoff).dropoffNote, 'Merkato, gate 2');
      expect(stop(p, TripStopKind.pickup).dropoffNote, isNull);
    });

    test('gone once the drop-off is done', () {
      final p = plan(DeliveryStatus.delivered, note: 'Merkato');

      expect(stop(p, TripStopKind.dropoff).dropoffNote, isNull);
    });

    test('none when the order has none (or the server withheld it)', () {
      expect(
        stop(plan(DeliveryStatus.inTransit), TripStopKind.dropoff).dropoffNote,
        isNull,
      );
      expect(
        stop(plan(DeliveryStatus.inTransit, note: '  '), TripStopKind.dropoff)
            .dropoffNote,
        isNull,
      );
    });
  });

  group('TripPlan.build', () {
    test(
        'pickups first in scan order, then drop-offs; next is the first '
        'stop not done', () {
      final plan = TripPlan.build(
        trip: tripFixture(),
        deliveries: [
          deliveryFixture(
            'd-2',
            status: DeliveryStatus.pending,
            farmer: 'Tolosa',
            buyer: 'Selam Traders',
          ),
          deliveryFixture(
            'd-1',
            status: DeliveryStatus.pickedUp,
            farmer: 'Almaz',
            buyer: 'Hawi Flour Mill',
            pickedUpAt: morning,
          ),
        ],
      );

      expect(
        [for (final s in plan.stops) '${s.kind.name} ${s.delivery.deliveryId}'],
        ['pickup d-1', 'pickup d-2', 'dropoff d-1', 'dropoff d-2'],
      );
      expect(plan.next?.kind, TripStopKind.pickup);
      expect(plan.next?.delivery.deliveryId, 'd-2');
      expect(plan.orderCount, 2);
    });

    test('with every load on the truck the next stop is the first drop-off',
        () {
      final plan = TripPlan.build(
        trip: tripFixture(),
        deliveries: [
          deliveryFixture(
            'd-2',
            status: DeliveryStatus.inTransit,
            farmer: 'Tolosa',
            buyer: 'Selam Traders',
            pickedUpAt: morning.add(const Duration(hours: 1)),
          ),
          deliveryFixture(
            'd-1',
            status: DeliveryStatus.pickedUp,
            farmer: 'Almaz',
            buyer: 'Hawi Flour Mill',
            pickedUpAt: morning,
          ),
        ],
      );

      expect(plan.next?.kind, TripStopKind.dropoff);
      expect(plan.next?.delivery.deliveryId, 'd-1');
    });

    test(
        'cancelled deliveries are off the trip; delivered and disputed '
        'drop-offs are done', () {
      final plan = TripPlan.build(
        trip: tripFixture(),
        deliveries: [
          deliveryFixture(
            'd-1',
            status: DeliveryStatus.delivered,
            farmer: 'Almaz',
            buyer: 'Hawi',
            pickedUpAt: morning,
            confirmedAt: morning.add(const Duration(hours: 3)),
          ),
          deliveryFixture(
            'd-2',
            status: DeliveryStatus.disputed,
            farmer: 'Tolosa',
            buyer: 'Selam',
            pickedUpAt: morning,
            confirmedAt: morning.add(const Duration(hours: 2)),
          ),
          deliveryFixture(
            'd-3',
            status: DeliveryStatus.cancelled,
            farmer: 'Kebede',
            buyer: 'Abel',
          ),
        ],
      );

      expect(plan.orderCount, 2);
      expect(plan.stops.every((s) => s.isDone), isTrue);
      expect(plan.next, isNull);
      expect(
        plan.stops.where((s) => s.kind == TripStopKind.dropoff).first.doneAt,
        morning.add(const Duration(hours: 2)),
      );
    });

    test('the pay is the transport cost of the orders on the trip', () {
      final deliveries = [
        deliveryFixture(
          'd-1',
          status: DeliveryStatus.pickedUp,
          farmer: 'Almaz',
          buyer: 'Hawi',
        ),
        deliveryFixture(
          'd-2',
          status: DeliveryStatus.pending,
          farmer: 'Tolosa',
          buyer: 'Selam',
        ),
        deliveryFixture(
          'd-3',
          status: DeliveryStatus.cancelled,
          farmer: 'Kebede',
          buyer: 'Abel',
        ),
      ];
      final orders = {
        for (final o in [
          orderFixture('d-1', crop: 'wheat', kg: 2000, transportCost: 3000),
          orderFixture('d-2', crop: 'maize', kg: 1000, transportCost: 1500),
          orderFixture('d-3', crop: 'teff', kg: 500, transportCost: 900),
        ])
          o.orderId: o,
      };

      final plan = TripPlan.build(
        trip: tripFixture(),
        deliveries: deliveries,
        orders: orders,
      );

      expect(plan.pay, 4500);
      expect(plan.stops.first.order?.cropType, 'wheat');
      expect(
        TripPlan.build(trip: null, deliveries: deliveries).pay,
        isNull,
      );
    });
  });

  group('TripPlanLoader', () {
    test('keeps only the trucker\'s own deliveries and reads their orders',
        () async {
      final loader = loaderFor(
        deliveries: [
          deliveryFixture(
            'd-1',
            status: DeliveryStatus.pickedUp,
            farmer: 'Almaz',
            buyer: 'Hawi',
          ),
          deliveryFixture(
            'd-9',
            status: DeliveryStatus.pickedUp,
            farmer: 'Other',
            buyer: 'Other',
            truckerId: 'trucker-2',
          ),
        ],
        orders: [
          orderFixture('d-1', crop: 'wheat', kg: 2000, transportCost: 3000),
        ],
      );

      final plan = await loader.load(tripId, userId: trucker);

      expect(plan.trip?.tripId, tripId);
      expect(plan.orderCount, 1);
      expect(plan.stops.first.order?.quantity, 2000);
      expect(plan.pay, 3000);
    });

    test('another trucker\'s trip is not shown as this one', () async {
      final loader = loaderFor(
        deliveries: const [],
        trip: tripFixture()..truckerId = 'trucker-2',
      );

      final plan = await loader.load(tripId, userId: trucker);

      expect(plan.trip, isNull);
      expect(plan.stops, isEmpty);
    });

    test('the kept drop-off handoffs of done deliveries are dropped (T-3)',
        () async {
      final handoffs = MemoryDropoffHandoffStore();
      for (final id in ['d-1', 'd-2', 'd-3']) {
        await handoffs.keep(trucker, id, KeptHandoff(code: id));
      }
      final loader = loaderFor(
        deliveries: [
          for (final (id, status) in [
            ('d-1', DeliveryStatus.delivered),
            ('d-2', DeliveryStatus.inTransit),
            ('d-3', DeliveryStatus.cancelled),
          ])
            deliveryFixture(id, status: status, farmer: 'F', buyer: 'B'),
        ],
        handoffs: handoffs,
      );

      await loader.load(tripId, userId: trucker);

      expect(await handoffs.read(trucker, 'd-1'), isNull);
      expect((await handoffs.read(trucker, 'd-2'))?.code, 'd-2');
      expect(await handoffs.read(trucker, 'd-3'), isNull);
    });

    test('deliveries that cannot be read fail the load', () async {
      final loader = loaderFor(deliveries: const [], failDeliveries: true);

      expect(loader.load(tripId, userId: trucker), throwsException);
    });
  });
}
