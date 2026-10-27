import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/screens/order_detail_screen.dart';

/// Opens the order-detail screen for [orderId] under its OWN route-scoped
/// [OrderBloc] reusing the app's order repository, as the Orders tab does
/// (a detail load must not drive the shared list bloc). Used by the inbox,
/// the Action-needed cards and a tapped push (ESC-21).
///
/// On mobile an order the device has not pulled yet (a push about a new
/// order) is read from the server first, best-effort. On web the order opens
/// through its route (`/orders/{orderId}`), so the address bar names it and a
/// reload keeps it.
Future<void> openOrderDetail(BuildContext context, String orderId) async {
  final router = kIsWeb ? GoRouter.maybeOf(context) : null;
  if (router != null) {
    await router.push<void>('/orders/${Uri.encodeComponent(orderId)}');
    return;
  }
  final repository = context.read<OrderBloc>().repository;
  MarketplaceSync? sync;
  try {
    sync = context.read<MarketplaceSync?>();
  } on Object {
    sync = null;
  }
  final navigator = Navigator.of(context);
  await pullOrderIfMissing(repository, sync, orderId);
  await navigator.push(orderDetailRoute(repository, orderId));
}

/// The detail route itself, for callers that hold a [NavigatorState] but no
/// context below the app's providers (the push tap).
MaterialPageRoute<void> orderDetailRoute(
  BaseOrderRepository repository,
  String orderId,
) =>
    MaterialPageRoute<void>(
      builder: (_) => BlocProvider<OrderBloc>(
        create: (_) => OrderBloc(repository: repository),
        child: OrderDetailScreen(orderId: orderId),
      ),
    );

/// Reads [orderId] from the server into the device when [repository] does
/// not hold it yet. Mobile only ([sync] is null on web, which reads the
/// server anyway); offline it gives up quietly and the screen says so.
Future<void> pullOrderIfMissing(
  BaseOrderRepository repository,
  MarketplaceSync? sync,
  String orderId,
) async {
  if (sync == null) return;
  try {
    if (await repository.getOrderById(orderId) != null) return;
    await sync.pullOrder(orderId).timeout(const Duration(seconds: 8));
  } on Object {
    // Offline or slow: the detail screen shows what the device has.
  }
}
