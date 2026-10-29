import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/escrow_payment/domain/escrow_hold.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_bloc.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/notifications/domain/base_notification_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';

// The live trip screens read what they show from the app's own repositories
// (the same ones on mobile and web), never from Isar directly, and never
// through the shared list blocs' states (a detail read must not turn a list
// into Loading). Each read is best effort: a screen without it still shows
// the track.

T? _maybeRead<T>(BuildContext context) {
  try {
    return context.read<T>();
  } on ProviderNotFoundException {
    return null;
  }
}

/// The order [delivery] fulfils, from the order repository (Isar on mobile,
/// `GET /orders/{id}` on web), or null.
Future<Order?> loadDeliveryOrder(
  BuildContext context,
  Delivery delivery,
) async {
  final orderId = delivery.orderId;
  if (orderId == null || orderId.isEmpty) return null;
  final repository = _maybeRead<OrderBloc>(context)?.repository;
  if (repository == null) return null;
  try {
    return await repository.getOrderById(orderId);
  } on Object catch (e) {
    debugPrint('[LiveTrip] order read failed: $e');
    return null;
  }
}

/// The delivery as this device knows it now (it may have moved on since the
/// list was read), or null.
Future<Delivery?> reloadDelivery(
  BuildContext context,
  Delivery delivery,
  String userId,
) async {
  final repository = _maybeRead<DeliveryBloc>(context)?.repository;
  if (repository == null) return null;
  try {
    return await repository.getDeliveryById(
      delivery.deliveryId,
      userId: userId,
    );
  } on Object catch (e) {
    debugPrint('[LiveTrip] delivery read failed: $e');
    return null;
  }
}

/// The escrow of the delivery's order (`escrow-order-{orderId}`), else the
/// delivery's own, read from the server when it can (mobile falls back to its
/// cached copy offline), or null.
Future<Payment?> loadDeliveryEscrow(
  BuildContext context,
  Delivery delivery,
) async {
  final repository = _maybeRead<EscrowBloc>(context)?.repository;
  if (repository == null) return null;
  try {
    final orderId = delivery.orderId;
    if (orderId != null && orderId.isNotEmpty) {
      final escrow = await repository.refreshEscrow(orderEscrowId(orderId));
      if (escrow != null) return escrow;
    }
    return await repository.getEscrowByDelivery(delivery.deliveryId);
  } on Object catch (e) {
    debugPrint('[LiveTrip] escrow read failed: $e');
    return null;
  }
}

/// Whether [userId]'s inbox holds the `truck_nearby` entry of [deliveryId].
Future<bool> loadTruckNearby(
  BuildContext context,
  String userId,
  String deliveryId,
) async {
  final repository = _maybeRead<BaseNotificationRepository>(context);
  if (repository == null || userId.isEmpty) return false;
  try {
    return hasTruckNearbyEntry(await repository.recent(userId), deliveryId);
  } on Object catch (e) {
    debugPrint('[LiveTrip] inbox read failed: $e');
    return false;
  }
}

/// The bundled place list for town names, loaded once for every screen and
/// card that asks at the same time (parsing it twice would cost a low-end
/// phone for nothing); empty when it cannot be read.
Future<List<Place>> loadPlaces() {
  final repository = PlaceRepository.instance;
  if (repository.isLoaded) return Future.value(repository.places);
  return _placesLoad ??= () async {
    try {
      await repository.ensureLoaded();
      return repository.places;
    } on Object catch (e) {
      debugPrint('[LiveTrip] place list unavailable: $e');
      return const <Place>[];
    } finally {
      _placesLoad = null;
    }
  }();
}

Future<List<Place>>? _placesLoad;
