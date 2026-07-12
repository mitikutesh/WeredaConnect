import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_validation_exception.dart';
import 'package:wereda_connect/features/delivery_verification/domain/repositories/base_delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';

/// BLoC for managing delivery verification state.
///
/// Handles creating, loading, confirming, disputing, and cancelling deliveries.
/// All operations go through the BaseDeliveryRepository (mobile or web).
class DeliveryBloc extends Bloc<DeliveryEvent, DeliveryState> {
  final BaseDeliveryRepository _repository;

  /// Optional server->local pull. Null on web (which reads the API directly)
  /// and in tests. When present, [LoadUserDeliveries] pulls the caller's
  /// deliveries into local Isar first so a buyer sees trucker-driven status
  /// changes they did not author.
  final MarketplaceSync? _marketplaceSync;

  /// Exposed for sub-screens that need to call repository methods directly
  /// (e.g., QrGenerateScreen persisting nonces).
  BaseDeliveryRepository get repository => _repository;

  DeliveryBloc({
    required BaseDeliveryRepository repository,
    MarketplaceSync? marketplaceSync,
  })  : _repository = repository,
        _marketplaceSync = marketplaceSync,
        super(const DeliveryInitial()) {
    on<LoadDeliveries>(_onLoadDeliveries);
    on<CreateDelivery>(_onCreateDelivery);
    on<CreateSelfPickupDelivery>(_onCreateSelfPickupDelivery);
    on<ConfirmPickup>(_onConfirmPickup);
    on<MarkInTransit>(_onMarkInTransit);
    on<ConfirmDelivery>(_onConfirmDelivery);
    on<RaiseDispute>(_onRaiseDispute);
    on<CancelDelivery>(_onCancelDelivery);
    on<FilterDeliveries>(_onFilterDeliveries);
    on<LoadUserDeliveries>(_onLoadUserDeliveries);
    on<LoadDeliveryDetail>(_onLoadDeliveryDetail);
    on<FilterUserDeliveries>(_onFilterUserDeliveries);
  }

  /// Emit a [DeliveryError] from an exception.
  ///
  /// Extracts the error code from [DeliveryValidationException] for
  /// l10n mapping in the presentation layer.
  DeliveryError _errorFromException(Object e) {
    if (e is DeliveryValidationException) {
      return DeliveryError(message: e.message, code: e.code);
    }
    return DeliveryError(
      message: e.toString(),
      code: DeliveryErrorCode.generic,
    );
  }

  Future<void> _onLoadDeliveries(
    LoadDeliveries event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());

    try {
      final deliveries = await _repository.getDeliveriesByTrip(
        event.tripId,
        userId: event.userId,
      );
      emit(DeliveriesLoaded(deliveries: deliveries));
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onCreateDelivery(
    CreateDelivery event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());

    try {
      final delivery = await _repository.createDelivery(
        tripId: event.tripId,
        orderId: event.orderId,
        listingId: event.listingId,
        farmerId: event.farmerId,
        farmerName: event.farmerName,
        truckerId: event.truckerId,
        truckerName: event.truckerName,
        buyerId: event.buyerId,
        buyerName: event.buyerName,
        qrToken: event.qrToken,
      );
      emit(DeliveryCreated(delivery: delivery));
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onCreateSelfPickupDelivery(
    CreateSelfPickupDelivery event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());

    try {
      final delivery = await _repository.createSelfPickupDelivery(
        orderId: event.orderId,
        listingId: event.listingId,
        farmerId: event.farmerId,
        farmerName: event.farmerName,
        buyerId: event.buyerId,
        buyerName: event.buyerName,
      );
      emit(DeliveryCreated(delivery: delivery));
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onConfirmPickup(
    ConfirmPickup event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());

    try {
      await _repository.confirmPickup(
        deliveryId: event.deliveryId,
        truckerId: event.truckerId,
        gpsPickup: event.gpsPickup,
        photoRef: event.photoRef,
        scannedQrNonce: event.scannedQrNonce,
      );
      emit(const PickupConfirmed());
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onMarkInTransit(
    MarkInTransit event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());

    try {
      await _repository.markInTransit(
        deliveryId: event.deliveryId,
        truckerId: event.truckerId,
      );
      emit(const MarkedInTransit());
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onConfirmDelivery(
    ConfirmDelivery event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());

    try {
      await _repository.confirmDelivery(
        deliveryId: event.deliveryId,
        buyerId: event.buyerId,
        gpsDropoff: event.gpsDropoff,
        photoRef: event.photoRef,
        scannedQrNonce: event.scannedQrNonce,
      );
      emit(const DeliveryConfirmed());
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onRaiseDispute(
    RaiseDispute event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());

    try {
      await _repository.raiseDispute(
        deliveryId: event.deliveryId,
        userId: event.userId,
        reason: event.reason,
      );
      emit(const DisputeRaised());
      final deliveries = await _repository.getDeliveriesByTrip(
        event.tripId,
        userId: event.userId,
      );
      emit(DeliveriesLoaded(deliveries: deliveries));
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onCancelDelivery(
    CancelDelivery event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());

    try {
      await _repository.cancelDelivery(
        deliveryId: event.deliveryId,
        userId: event.userId,
      );
      emit(const DeliveryCancelled());
      final deliveries = await _repository.getDeliveriesByTrip(
        event.tripId,
        userId: event.userId,
      );
      emit(DeliveriesLoaded(deliveries: deliveries));
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onFilterDeliveries(
    FilterDeliveries event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());

    try {
      final List<Delivery> deliveries;
      if (event.statusFilter != null) {
        final status = DeliveryStatus.values.firstWhere(
          (s) => s.name == event.statusFilter,
          orElse: () => DeliveryStatus.pending,
        );
        deliveries = await _repository.getDeliveriesByStatus(
          status,
          userId: event.userId,
        );
      } else {
        deliveries = await _repository.getDeliveriesByTrip(
          event.tripId,
          userId: event.userId,
        );
      }
      emit(
        DeliveriesLoaded(
          deliveries: deliveries,
          statusFilter: event.statusFilter,
        ),
      );
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onLoadUserDeliveries(
    LoadUserDeliveries event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());
    try {
      // Pull server-side status changes into local Isar first, so a buyer sees
      // trucker-driven updates they didn't author. Best-effort: offline or a
      // failed pull falls back to whatever is already cached locally.
      final sync = _marketplaceSync;
      if (sync != null) {
        try {
          await sync.pullDeliveries();
        } on Object {
          // Offline-first: ignore and read the local cache below.
        }
      }
      final deliveries = await _repository.getDeliveriesByUser(
        event.userId,
        role: event.role,
      );
      emit(DeliveriesLoaded(deliveries: deliveries));
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onLoadDeliveryDetail(
    LoadDeliveryDetail event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());
    try {
      final delivery = await _repository.getDeliveryById(
        event.deliveryId,
        userId: event.userId,
      );
      if (delivery != null) {
        emit(DeliveryDetailLoaded(delivery: delivery));
      } else {
        emit(
          const DeliveryError(
            message: 'Delivery not found',
            code: DeliveryErrorCode.notFound,
          ),
        );
      }
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }

  Future<void> _onFilterUserDeliveries(
    FilterUserDeliveries event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());
    try {
      final deliveries = await _repository.getDeliveriesByUser(
        event.userId,
        role: event.role,
        statusFilter: event.statusFilter,
      );
      emit(
        DeliveriesLoaded(
          deliveries: deliveries,
          statusFilter: event.statusFilter,
        ),
      );
    } on Object catch (e) {
      emit(_errorFromException(e));
    }
  }
}
