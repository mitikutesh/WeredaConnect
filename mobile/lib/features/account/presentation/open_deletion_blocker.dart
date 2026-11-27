import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/utils/entity_id.dart';
import 'package:wereda_connect/features/account/data/repositories/account_deletion_repository.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// What a blocker of a deletion opens (FUP-106).
enum DeletionBlockerKind { order, delivery }

const _orderEscrowPrefix = 'escrow-order-';

/// The screen a blocker opens, or null when it names nothing the app can
/// show: a type this version does not know, an escrow from before
/// order-funded escrows, or an id that is not a plain token. An order, a
/// dispute (an order in `disputed`) and a payment waiting for a refund
/// name their order; an order's escrow (`escrow-order-{orderId}`) opens
/// that order; a delivery opens the delivery.
({DeletionBlockerKind kind, String id})? deletionBlockerTarget(
  AccountDeletionBlocker blocker,
) {
  final id = blocker.id;
  final (kind, target) = switch (blocker.type) {
    'order' || 'dispute' || 'payment_review' => (DeletionBlockerKind.order, id),
    'escrow' when id.startsWith(_orderEscrowPrefix) => (
        DeletionBlockerKind.order,
        id.substring(_orderEscrowPrefix.length),
      ),
    'delivery' => (DeletionBlockerKind.delivery, id),
    _ => (null, ''),
  };
  if (kind == null || !isPlainEntityId(target)) return null;
  return (kind: kind, id: target);
}

/// Opens [blocker]'s order or delivery for the signed-in user, whatever
/// their role on it: the order detail through [openOrder] (its route on
/// web), the delivery detail as the Deliveries tab opens it. A delivery the
/// phone has not pulled yet is read from the server first, best-effort; one
/// still missing says so.
Future<void> openDeletionBlocker(
  BuildContext context,
  AccountDeletionBlocker blocker, {
  Future<void> Function(BuildContext context, String orderId) openOrder =
      openOrderDetail,
}) async {
  final target = deletionBlockerTarget(blocker);
  if (target == null) return;
  if (target.kind == DeletionBlockerKind.order) {
    await openOrder(context, target.id);
    return;
  }

  final session = context.read<AuthBloc>().state;
  if (session is! AuthAuthenticated) return;
  final userId = session.userId;
  final repository = context.read<DeliveryBloc>().repository;
  MarketplaceSync? sync;
  try {
    sync = context.read<MarketplaceSync?>();
  } on Object {
    sync = null;
  }
  final navigator = Navigator.of(context);
  final messenger = ScaffoldMessenger.maybeOf(context);
  final notFound = AppLocalizations.of(context)!.deliveryErrorNotFound;

  Future<Delivery?> find() async {
    try {
      return await repository.getDeliveryById(target.id, userId: userId);
    } on Object {
      return null;
    }
  }

  var delivery = await find();
  if (delivery == null && sync != null) {
    try {
      await sync.pullDeliveries().timeout(const Duration(seconds: 8));
      delivery = await find();
    } on Object {
      // Offline or slow: say it cannot be found below.
    }
  }
  if (delivery == null) {
    messenger?.showSnackBar(SnackBar(content: Text(notFound)));
    return;
  }
  final found = delivery;
  await navigator.push(
    MaterialPageRoute<void>(
      builder: (_) =>
          DeliveryDetailScreen(delivery: found, currentUserId: userId),
    ),
  );
}
