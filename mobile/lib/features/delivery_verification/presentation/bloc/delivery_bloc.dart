import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/repositories/delivery_repository.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';

/// BLoC for managing delivery verification state.
///
/// Handles creating, loading, confirming, disputing, and cancelling deliveries.
/// All operations go through the offline-first DeliveryRepository.
class DeliveryBloc extends Bloc<DeliveryEvent, DeliveryState> {
  final DeliveryRepository _repository;

  /// Exposed for sub-screens that need to call repository methods directly
  /// (e.g., QrGenerateScreen persisting nonces).
  DeliveryRepository get repository => _repository;

  DeliveryBloc({required DeliveryRepository repository})
      : _repository = repository,
        super(const DeliveryInitial()) {
    on<LoadDeliveries>(_onLoadDeliveries);
    on<CreateDelivery>(_onCreateDelivery);
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
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
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
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
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
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
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
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
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
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
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
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
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
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
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
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
    }
  }

  Future<void> _onLoadUserDeliveries(
    LoadUserDeliveries event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());
    try {
      final deliveries = await _repository.getDeliveriesByUser(
        event.userId,
        role: event.role,
      );
      emit(DeliveriesLoaded(deliveries: deliveries));
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
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
        emit(const DeliveryError(message: 'Delivery not found'));
      }
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
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
    } on Exception catch (e) {
      emit(DeliveryError(message: e.toString()));
    }
  }
}
