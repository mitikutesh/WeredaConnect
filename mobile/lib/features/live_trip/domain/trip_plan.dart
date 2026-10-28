import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/trip.dart';

/// A pickup at a farmer or a drop-off at a buyer.
enum TripStopKind { pickup, dropoff }

/// One stop of the trucker's trip in trip mode (LT-09). The trucker is a
/// party to every order on the trip, so a stop carries the names.
class TripStop {
  const TripStop({required this.kind, required this.delivery, this.order});

  final TripStopKind kind;
  final Delivery delivery;

  /// The delivery's order (crop, quantity, transport cost), when it could be
  /// read.
  final Order? order;

  /// The pickup was scanned, or the buyer confirmed (or disputed) the
  /// drop-off.
  bool get isDone => switch (kind) {
        TripStopKind.pickup => delivery.status != DeliveryStatus.pending,
        TripStopKind.dropoff => delivery.status == DeliveryStatus.delivered ||
            delivery.status == DeliveryStatus.disputed,
      };

  /// When it was done: the pickup scan, or the buyer's confirmation.
  DateTime? get doneAt => switch (kind) {
        TripStopKind.pickup => delivery.pickedUpAt,
        TripStopKind.dropoff => delivery.confirmedAt,
      };
}

/// The trucker's own trip as trip mode shows it: the trip, its stops in
/// driving order and the pay. Built from the phone's copy on mobile and the
/// API on web, never from a location.
class TripPlan {
  const TripPlan._({
    required this.trip,
    required this.stops,
    required this.orderCount,
    required this.pay,
  });

  /// [deliveries] of the trip with their [orders] (by order id). Cancelled
  /// deliveries are off the trip. Pickups come first (the scanned ones in
  /// scan order, then the waiting ones in the order they were booked), then
  /// the drop-offs (the confirmed ones, then the ones on the road in pickup
  /// order, then the ones not picked up yet): without a road map the phone
  /// cannot plan a better route, and a truck is loaded before it unloads.
  factory TripPlan.build({
    required Trip? trip,
    required List<Delivery> deliveries,
    Map<String, Order> orders = const {},
  }) {
    final legs =
        deliveries.where((d) => d.status != DeliveryStatus.cancelled).toList();
    Order? orderOf(Delivery d) => d.orderId == null ? null : orders[d.orderId];

    int byTime(DateTime? a, DateTime? b, Delivery x, Delivery y) {
      if (a != null && b != null) {
        final c = a.compareTo(b);
        if (c != 0) return c;
      }
      return x.createdAt.compareTo(y.createdAt);
    }

    int pickupRank(Delivery d) => d.status == DeliveryStatus.pending ? 1 : 0;
    int dropoffRank(Delivery d) => switch (d.status) {
          DeliveryStatus.delivered || DeliveryStatus.disputed => 0,
          DeliveryStatus.pickedUp || DeliveryStatus.inTransit => 1,
          _ => 2,
        };

    final pickups = [...legs]..sort((x, y) {
        final r = pickupRank(x) - pickupRank(y);
        if (r != 0) return r;
        return byTime(x.pickedUpAt, y.pickedUpAt, x, y);
      });
    final dropoffs = [...legs]..sort((x, y) {
        final r = dropoffRank(x) - dropoffRank(y);
        if (r != 0) return r;
        return dropoffRank(x) == 0
            ? byTime(x.confirmedAt, y.confirmedAt, x, y)
            : byTime(x.pickedUpAt, y.pickedUpAt, x, y);
      });

    double? pay;
    for (final leg in legs) {
      final cost = orderOf(leg)?.transportCost;
      if (cost != null) pay = (pay ?? 0) + cost;
    }

    return TripPlan._(
      trip: trip,
      stops: [
        for (final d in pickups)
          TripStop(kind: TripStopKind.pickup, delivery: d, order: orderOf(d)),
        for (final d in dropoffs)
          TripStop(kind: TripStopKind.dropoff, delivery: d, order: orderOf(d)),
      ],
      orderCount: legs.length,
      pay: pay,
    );
  }

  /// The trip, when it could be read.
  final Trip? trip;

  final List<TripStop> stops;

  /// The orders on the trip (one delivery each).
  final int orderCount;

  /// The trucker's pay for the trip: the transport cost of its orders, null
  /// when no order's cost is known.
  final double? pay;

  /// The first stop not done yet, or null when every stop is done.
  TripStop? get next => stops.where((s) => !s.isDone).firstOrNull;
}
