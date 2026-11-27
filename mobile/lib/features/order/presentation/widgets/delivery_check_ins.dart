import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_check_in.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/sync/sync_executor.dart'
    if (dart.library.html) 'package:wereda_connect/core/sync/sync_executor_web.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/widgets/dropoff_check_in_card.dart';
import 'package:wereda_connect/features/order/presentation/widgets/pickup_check_in_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// FUP-327, FUP-371: the check-ins of a delivery's order on the delivery
/// detail, where the parties already are. Before pickup the arrival
/// check-ins ([PickupCheckInCard]: the trucker's "I have arrived", the
/// self-pickup buyer's, the farmer's "Crops are ready"); after it the
/// drop-off check-in ([DropoffCheckInCard]: the trucker's "Arrived at
/// drop-off", for the buyer by when to confirm). The same cards, rules and
/// repository calls as the order detail, through an order bloc of its own
/// over the app's order repository, so the shared list bloc is never
/// driven. The order is read from the repository (the phone's copy with the
/// signed-in user's queued check-ins, or the server on web), read again
/// after a check-in, and on the phone after each sync while a check-in made
/// here waits for the server's copy. Nothing shows without the order or on
/// an order the server did not stamp for the judgement.
class DeliveryCheckIns extends StatelessWidget {
  final Delivery delivery;
  final String userId;

  /// Around the card when one shows; nothing takes room otherwise.
  final EdgeInsetsGeometry padding;

  /// Where a check-in's GPS fix comes from; the device's GPS when null.
  final GpsCaptureService? gpsService;

  /// Takes the trucker's arrival photo at the drop-off; the camera when
  /// null.
  final ArrivalPhotoTaker? takeArrivalPhoto;

  /// The time now (UTC); the device clock when null. Tests move it.
  final DateTime Function()? clock;

  const DeliveryCheckIns({
    required this.delivery,
    required this.userId,
    this.padding = EdgeInsets.zero,
    this.gpsService,
    this.takeArrivalPhoto,
    this.clock,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final orderId = delivery.orderId;
    if (orderId == null || orderId.isEmpty) return const SizedBox.shrink();
    final BaseOrderRepository repository;
    try {
      repository = context.read<OrderBloc>().repository;
    } on Object {
      // No order side above this screen: nothing shows.
      return const SizedBox.shrink();
    }
    return BlocProvider<OrderBloc>(
      create: (_) => OrderBloc(repository: repository),
      child: _DeliveryCheckInsBody(config: this, orderId: orderId),
    );
  }
}

class _DeliveryCheckInsBody extends StatefulWidget {
  final DeliveryCheckIns config;
  final String orderId;

  const _DeliveryCheckInsBody({required this.config, required this.orderId});

  @override
  State<_DeliveryCheckInsBody> createState() => _DeliveryCheckInsBodyState();
}

class _DeliveryCheckInsBodyState extends State<_DeliveryCheckInsBody> {
  /// The order last read. A reload or an action keeps it on screen.
  Order? _shown;

  /// The phone's sync results ([_onSyncResult]). Null where there is no
  /// sync executor (web).
  StreamSubscription<SyncBatchResult>? _syncResults;

  /// A read of the server's copy runs; a second one is not sent.
  bool _serverReadRunning = false;

  DeliveryCheckIns get _config => widget.config;

  String get _orderId => widget.orderId;

  @override
  void initState() {
    super.initState();
    context.read<OrderBloc>().add(LoadOrderById(orderId: _orderId));
    try {
      _syncResults =
          context.read<SyncExecutor>().onSyncResult.listen(_onSyncResult);
    } on Object {
      // No sync executor here (web, tests): nothing is queued to follow.
    }
  }

  @override
  void dispose() {
    _syncResults?.cancel();
    super.dispose();
  }

  /// As on the order detail (FUP-285): a sync ran while a check-in made on
  /// this phone waits for the server's copy. The order is read again, so a
  /// refused one gives the button back at once, and after a sync that sent
  /// something the server's copy, with its receipt time, is read too.
  void _onSyncResult(SyncBatchResult result) {
    final order = _shown;
    if (!mounted || order == null || !hasCheckInAwaitingServer(order)) return;
    final bloc = context.read<OrderBloc>();
    if (bloc.isClosed) return;
    bloc.add(LoadOrderById(orderId: _orderId, quiet: true));
    if (result.succeeded > 0) _readFromServer().ignore();
  }

  /// The server's copy of the order into the phone (`GET /orders/{id}`),
  /// then the stored row. Mobile only (web reads the server already); best
  /// effort and silent: offline or refused, the phone's copy stays.
  Future<void> _readFromServer() async {
    final MarketplaceSync? sync;
    try {
      sync = context.read<MarketplaceSync?>();
    } on Object {
      return;
    }
    if (sync == null || _serverReadRunning) return;
    _serverReadRunning = true;
    final bloc = context.read<OrderBloc>();
    try {
      final server = await sync.pullOrder(_orderId);
      if (server != null && mounted && !bloc.isClosed) {
        bloc.add(LoadOrderById(orderId: _orderId, quiet: true));
      }
    } on Object {
      // Offline, or the server refused the read: the phone's copy stays.
    } finally {
      _serverReadRunning = false;
    }
  }

  @override
  Widget build(BuildContext context) {
    return BlocConsumer<OrderBloc, OrderState>(
      listener: (context, state) {
        if (state is CheckInSaved) {
          showCheckInSaved(context, state);
          context.read<OrderBloc>().add(LoadOrderById(orderId: _orderId));
        } else if (state is OrderError && _shown != null) {
          // A check-in refused or not saved. A first read that fails shows
          // nothing, as before the order is known.
          final l10n = AppLocalizations.of(context)!;
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                localizeErrorCode(state.code, l10n, message: state.message),
              ),
            ),
          );
        }
      },
      builder: (context, state) {
        if (state is SingleOrderLoaded && state.order.orderId == _orderId) {
          _shown = state.order;
        }
        final order = _shown;
        if (order == null) return const SizedBox.shrink();
        final delivery = _config.delivery;
        final busy = state is OrderLoading;
        final Widget card;
        if (delivery.status == DeliveryStatus.pending) {
          if (!PickupCheckInCard.showsFor(order)) {
            return const SizedBox.shrink();
          }
          card = PickupCheckInCard(
            order: order,
            userId: _config.userId,
            busy: busy,
            gpsService: _config.gpsService,
            clock: _config.clock,
          );
        } else {
          if (!DropoffCheckInCard.showsFor(order)) {
            return const SizedBox.shrink();
          }
          card = DropoffCheckInCard(
            order: order,
            userId: _config.userId,
            busy: busy,
            delivery: delivery,
            gpsService: _config.gpsService,
            takeArrivalPhoto: _config.takeArrivalPhoto,
            clock: _config.clock,
          );
        }
        return Padding(padding: _config.padding, child: card);
      },
    );
  }
}
