import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/live_trip/domain/trip_plan.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';

/// Reads the trucker's trip for trip mode (LT-09) through the app's
/// repositories: the local Isar copy on mobile, the API on web. Only the
/// trucker's own trip and deliveries are kept (a trucker sees only their own
/// trips).
class TripPlanLoader {
  TripPlanLoader({
    required BaseTripListingRepository trips,
    required BaseDeliveryRepository deliveries,
    required BaseOrderRepository orders,
  })  : _trips = trips,
        _deliveries = deliveries,
        _orders = orders;

  final BaseTripListingRepository _trips;
  final BaseDeliveryRepository _deliveries;
  final BaseOrderRepository _orders;

  /// The plan of [tripId] for the trucker [userId]. Throws when the
  /// deliveries cannot be read; a trip or an order that cannot be read is
  /// left out (no header, no crop line, no pay).
  Future<TripPlan> load(String tripId, {required String userId}) async {
    Trip? trip;
    try {
      trip = await _trips.getTripById(tripId);
    } on Object catch (e) {
      debugPrint('[LiveTrip] trip unreadable: $e');
    }
    if (trip != null && trip.truckerId != userId) trip = null;

    final deliveries = [
      for (final d
          in await _deliveries.getDeliveriesByTrip(tripId, userId: userId))
        if (d.truckerId == userId) d,
    ];

    final orders = <String, Order>{};
    final orderIds = {
      for (final d in deliveries)
        if (d.orderId != null && d.orderId!.isNotEmpty) d.orderId!,
    };
    await Future.wait(
      orderIds.map((id) async {
        try {
          final order = await _orders.getOrderById(id);
          if (order != null) orders[id] = order;
        } on Object catch (e) {
          debugPrint('[LiveTrip] order unreadable: $e');
        }
      }),
    );

    return TripPlan.build(trip: trip, deliveries: deliveries, orders: orders);
  }
}
