import 'dart:async';

import 'package:app_links/app_links.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/telebirr_launcher.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_checkout_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Mobile: Telebirr sent the buyer back (`weredaconnect://payment/complete`)
/// and no checkout screen is open, typically because the app was killed
/// while the buyer paid (ESC-44). Says "Checking your payment...", reads the
/// buyer's orders that wait for payment from the server, and confirms when
/// one is now funded. A checkout screen that is open handles the link
/// itself.
class PaymentReturnListener extends StatefulWidget {
  final Widget child;

  /// The app's incoming links; app_links by default (the first listener
  /// also gets the link that started the app). Tests inject one.
  final Stream<Uri>? links;

  const PaymentReturnListener({required this.child, this.links, super.key});

  @override
  State<PaymentReturnListener> createState() => _PaymentReturnListenerState();
}

class _PaymentReturnListenerState extends State<PaymentReturnListener> {
  StreamSubscription<Uri>? _sub;

  @override
  void initState() {
    super.initState();
    _sub = (widget.links ?? _appLinks()).listen(
      _onLink,
      onError: (Object _) {},
    );
  }

  static Stream<Uri> _appLinks() {
    try {
      return AppLinks().uriLinkStream;
    } on Object {
      return const Stream.empty();
    }
  }

  @override
  void dispose() {
    unawaited(_sub?.cancel());
    super.dispose();
  }

  Future<void> _onLink(Uri uri) async {
    if (!isTelebirrReturnLink(uri) || TelebirrReturnWatchers.any) return;
    if (!mounted) return;
    final auth = context.read<AuthBloc>().state;
    if (auth is! AuthAuthenticated || auth.role != UserRole.buyer) return;
    final MarketplaceSync? sync;
    try {
      sync = context.read<MarketplaceSync?>();
    } on Object {
      return;
    }
    if (sync == null) return;
    final orders = context.read<OrderBloc>().repository;
    final messenger = ScaffoldMessenger.maybeOf(context);
    final l10n = AppLocalizations.of(context)!;
    messenger?.showSnackBar(
      SnackBar(content: Text(l10n.orderCheckoutCheckingPayment)),
    );

    final waiting = await orders.getOrdersByBuyer(
      auth.userId,
      statusFilter: OrderStatus.paymentSubmitted.name,
    );
    var paid = false;
    for (final order in waiting) {
      try {
        final server = await sync.pullOrder(order.orderId);
        if (server != null && paidOrderStatuses.contains(server.status)) {
          paid = true;
        }
      } on Object {
        // Offline: the order list catches up on the next pull.
      }
    }
    if (paid && mounted) {
      messenger?.showSnackBar(
        SnackBar(content: Text(l10n.escrowPaymentConfirmed)),
      );
    }
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
