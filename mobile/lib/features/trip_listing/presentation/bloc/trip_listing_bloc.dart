import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/trip.dart';
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
    } on Object catch (e) {
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
    } on Object catch (e) {
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
    } on Object catch (e) {
      emit(TripListingError(message: e.toString()));
    }
  }

  Future<void> _onCancelTrip(
    CancelTrip event,
    Emitter<TripListingState> emit,
  ) async {
    // Capture current trips for optimistic update
    final previousTrips =
        state is TripsLoaded ? (state as TripsLoaded).trips : <Trip>[];

    emit(const TripListingLoading());

    try {
      await _repository.cancelTrip(
        tripId: event.tripId,
        truckerId: event.truckerId,
      );

      // Optimistic update: mark the trip as cancelled locally.
      // The DynamoDB GSI is eventually consistent, so an immediate
      // reload may return stale data with the old status.
      final updatedTrips = previousTrips.map((t) {
        if (t.tripId == event.tripId) {
          t.status = TripStatus.cancelled;
        }
        return t;
      }).toList();
      emit(TripsLoaded(trips: updatedTrips));
    } on Object catch (e) {
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
    } on Object catch (e) {
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
    } on Object catch (e) {
      emit(TripListingError(message: e.toString()));
    }
  }

  Future<void> _onFilterTrips(
    FilterTrips event,
    Emitter<TripListingState> emit,
  ) async {
    emit(const TripListingLoading());

    try {
      // Always scope to the requesting trucker's own trips — a Trucker
      // MUST NOT see other truckers' trips. Apply the route filter in
      // memory rather than calling getOpenTrips (which is unscoped and
      // returns every trucker's open trips).
      var trips = await _repository.getTripsByTrucker(event.truckerId);
      if (event.fromWoreda != null) {
        trips = trips.where((t) => t.fromWoreda == event.fromWoreda).toList();
      }
      if (event.toWoreda != null) {
        trips = trips.where((t) => t.toWoreda == event.toWoreda).toList();
      }
      emit(
        TripsLoaded(
          trips: trips,
          fromWoredaFilter: event.fromWoreda,
          toWoredaFilter: event.toWoreda,
        ),
      );
    } on Object catch (e) {
      emit(TripListingError(message: e.toString()));
    }
  }
}
