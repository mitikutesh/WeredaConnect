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

  DeliveryBloc({required DeliveryRepository repository})
      : _repository = repository,
        super(const DeliveryInitial()) {
    on<LoadDeliveries>(_onLoadDeliveries);
    on<CreateDelivery>(_onCreateDelivery);
    on<ConfirmPickup>(_onConfirmPickup);
    on<ConfirmDelivery>(_onConfirmDelivery);
    on<RaiseDispute>(_onRaiseDispute);
    on<CancelDelivery>(_onCancelDelivery);
    on<FilterDeliveries>(_onFilterDeliveries);
  }

  Future<void> _onLoadDeliveries(
    LoadDeliveries event,
    Emitter<DeliveryState> emit,
  ) async {
    emit(const DeliveryLoading());

    try {
      final deliveries = await _repository.getDeliveriesByTrip(event.tripId);
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
      );
      emit(const PickupConfirmed());
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
      // Reload deliveries - we don't know the tripId here, so emit a generic success
      emit(const DeliveryLoading());
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
      emit(const DeliveryLoading());
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
        deliveries = await _repository.getDeliveriesByStatus(status);
      } else {
        deliveries = await _repository.getDeliveriesByTrip(event.tripId);
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
}
