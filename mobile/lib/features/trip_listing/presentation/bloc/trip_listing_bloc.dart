import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';

/// BLoC for managing trip listing state.
///
/// Handles creating, loading, filtering, and cancelling trip listings.
/// All operations go through the BaseTripListingRepository (mobile or web).
class TripListingBloc extends Bloc<TripListingEvent, TripListingState> {
  final BaseTripListingRepository _repository;

  /// Exposed for screens that need to query trips directly
  /// (e.g., CreateDeliveryScreen loading trucker's open trips).
  BaseTripListingRepository get repository => _repository;

  TripListingBloc({required BaseTripListingRepository repository})
      : _repository = repository,
        super(const TripListingInitial()) {
    on<LoadTrips>(_onLoadTrips);
    on<LoadOpenTrips>(_onLoadOpenTrips);
    on<CreateTrip>(_onCreateTrip);
    on<CancelTrip>(_onCancelTrip);
    on<StartTrip>(_onStartTrip);
    on<MarkTripDelivered>(_onMarkTripDelivered);
    on<FilterTrips>(_onFilterTrips);
  }

  Future<void> _onLoadTrips(
    LoadTrips event,
    Emitter<TripListingState> emit,
  ) async {
    emit(const TripListingLoading());

    try {
      final trips = await _repository.getTripsByTrucker(event.truckerId);
      emit(TripsLoaded(trips: trips));
    } on Exception catch (e) {
      emit(TripListingError(message: e.toString()));
    }
  }

  Future<void> _onLoadOpenTrips(
    LoadOpenTrips event,
    Emitter<TripListingState> emit,
  ) async {
    emit(const TripListingLoading());

    try {
      final trips = await _repository.getOpenTrips(
        fromWoreda: event.fromWoreda,
        toWoreda: event.toWoreda,
      );
      emit(
        TripsLoaded(
          trips: trips,
          fromWoredaFilter: event.fromWoreda,
          toWoredaFilter: event.toWoreda,
        ),
      );
    } on Exception catch (e) {
      emit(TripListingError(message: e.toString()));
    }
  }

  Future<void> _onCreateTrip(
    CreateTrip event,
    Emitter<TripListingState> emit,
  ) async {
    emit(const TripListingLoading());

    try {
      final trip = await _repository.createTrip(
        truckerId: event.truckerId,
        truckerName: event.truckerName,
        truckerPhone: event.truckerPhone,
        vehiclePlate: event.vehiclePlate,
        vehicleDescription: event.vehicleDescription,
        fromWoreda: event.fromWoreda,
        toWoreda: event.toWoreda,
        departureDate: event.departureDate,
        capacity: event.capacity,
        pricePerQuintal: event.pricePerQuintal,
      );
      emit(TripCreated(trip: trip));
    } on Exception catch (e) {
      emit(TripListingError(message: e.toString()));
    }
  }

  Future<void> _onCancelTrip(
    CancelTrip event,
    Emitter<TripListingState> emit,
  ) async {
    emit(const TripListingLoading());

    try {
      await _repository.cancelTrip(
        tripId: event.tripId,
        truckerId: event.truckerId,
      );
      // Reload trips after cancel
      final trips = await _repository.getTripsByTrucker(event.truckerId);
      emit(TripsLoaded(trips: trips));
    } on Exception catch (e) {
      emit(TripListingError(message: e.toString()));
    }
  }

  Future<void> _onStartTrip(
    StartTrip event,
    Emitter<TripListingState> emit,
  ) async {
    emit(const TripListingLoading());

    try {
      final trip = await _repository.startTrip(
        tripId: event.tripId,
        truckerId: event.truckerId,
      );
      emit(TripStarted(trip: trip));
    } on Exception catch (e) {
      emit(TripListingError(message: e.toString()));
    }
  }

  Future<void> _onMarkTripDelivered(
    MarkTripDelivered event,
    Emitter<TripListingState> emit,
  ) async {
    emit(const TripListingLoading());

    try {
      final trip = await _repository.markDelivered(
        tripId: event.tripId,
        truckerId: event.truckerId,
      );
      emit(TripDelivered(trip: trip));
    } on Exception catch (e) {
      emit(TripListingError(message: e.toString()));
    }
  }

  Future<void> _onFilterTrips(
    FilterTrips event,
    Emitter<TripListingState> emit,
  ) async {
    emit(const TripListingLoading());

    try {
      final trips = (event.fromWoreda != null || event.toWoreda != null)
          ? await _repository.getOpenTrips(
              fromWoreda: event.fromWoreda,
              toWoreda: event.toWoreda,
            )
          : await _repository.getTripsByTrucker(event.truckerId);
      emit(
        TripsLoaded(
          trips: trips,
          fromWoredaFilter: event.fromWoreda,
          toWoredaFilter: event.toWoreda,
        ),
      );
    } on Exception catch (e) {
      emit(TripListingError(message: e.toString()));
    }
  }
}
