import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/trip.dart';

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

/// State after a trip has been started (matched → inTransit).
class TripStarted extends TripListingState {
  final Trip trip;

  const TripStarted({required this.trip});

  @override
  List<Object?> get props => [trip];
}

/// State after a trip has been marked as delivered (inTransit → delivered).
class TripDelivered extends TripListingState {
  final Trip trip;

  const TripDelivered({required this.trip});

  @override
  List<Object?> get props => [trip];
}

/// Error state when an operation fails. [code] drives the localized, user-facing
/// message; [message] is the raw detail, kept for logging — do NOT show it
/// directly to users.
class TripListingError extends TripListingState {
  final String message;
  final AppErrorCode code;

  const TripListingError({
    required this.message,
    this.code = AppErrorCode.unknown,
  });

  @override
  List<Object?> get props => [message, code];
}
