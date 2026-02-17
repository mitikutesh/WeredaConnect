import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/database/schemas/trip.dart';

/// Base class for trip listing states.
abstract class TripListingState extends Equatable {
  const TripListingState();

  @override
  List<Object?> get props => [];
}

/// Initial state before any data is loaded.
class TripListingInitial extends TripListingState {
  const TripListingInitial();
}

/// Loading state during data operations.
class TripListingLoading extends TripListingState {
  const TripListingLoading();
}

/// State when trips have been successfully loaded.
class TripsLoaded extends TripListingState {
  final List<Trip> trips;
  final String? fromWoredaFilter;
  final String? toWoredaFilter;

  const TripsLoaded({
    required this.trips,
    this.fromWoredaFilter,
    this.toWoredaFilter,
  });

  @override
  List<Object?> get props => [trips, fromWoredaFilter, toWoredaFilter];
}

/// State after a trip has been successfully created.
class TripCreated extends TripListingState {
  final Trip trip;

  const TripCreated({required this.trip});

  @override
  List<Object?> get props => [trip];
}

/// Error state when an operation fails.
class TripListingError extends TripListingState {
  final String message;

  const TripListingError({required this.message});

  @override
  List<Object?> get props => [message];
}
