import 'package:equatable/equatable.dart';

/// Base class for trip listing events.
abstract class TripListingEvent extends Equatable {
  const TripListingEvent();

  @override
  List<Object?> get props => [];
}

/// Event to load trips for a trucker.
class LoadTrips extends TripListingEvent {
  final String truckerId;

  const LoadTrips({required this.truckerId});

  @override
  List<Object?> get props => [truckerId];
}

/// Event to create a new trip.
class CreateTrip extends TripListingEvent {
  final String truckerId;
  final String truckerName;
  final String truckerPhone;
  final String? vehiclePlate;
  final String? vehicleDescription;
  final String fromWoreda;
  final String toWoreda;
  final DateTime departureDate;
  final double capacity;
  final double pricePerQuintal;

  const CreateTrip({
    required this.truckerId,
    required this.truckerName,
    required this.truckerPhone,
    required this.fromWoreda,
    required this.toWoreda,
    required this.departureDate,
    required this.capacity,
    required this.pricePerQuintal,
    this.vehiclePlate,
    this.vehicleDescription,
  });

  @override
  List<Object?> get props => [
        truckerId,
        truckerName,
        truckerPhone,
        vehiclePlate,
        vehicleDescription,
        fromWoreda,
        toWoreda,
        departureDate,
        capacity,
        pricePerQuintal,
      ];
}

/// Event to cancel a trip.
class CancelTrip extends TripListingEvent {
  final String tripId;
  final String truckerId;

  const CancelTrip({
    required this.tripId,
    required this.truckerId,
  });

  @override
  List<Object?> get props => [tripId, truckerId];
}

/// Event to start a trip (matched → inTransit).
class StartTrip extends TripListingEvent {
  final String tripId;
  final String truckerId;

  const StartTrip({
    required this.tripId,
    required this.truckerId,
  });

  @override
  List<Object?> get props => [tripId, truckerId];
}

/// Event to mark a trip as delivered (inTransit → delivered).
class MarkTripDelivered extends TripListingEvent {
  final String tripId;
  final String truckerId;

  const MarkTripDelivered({
    required this.tripId,
    required this.truckerId,
  });

  @override
  List<Object?> get props => [tripId, truckerId];
}

/// Event to load all open trips (browse mode for farmers/buyers).
class LoadOpenTrips extends TripListingEvent {
  final String? fromWoreda;
  final String? toWoreda;

  const LoadOpenTrips({this.fromWoreda, this.toWoreda});

  @override
  List<Object?> get props => [fromWoreda, toWoreda];
}

/// Event to filter trips by route.
class FilterTrips extends TripListingEvent {
  final String? fromWoreda;
  final String? toWoreda;
  final String truckerId;

  const FilterTrips({
    required this.truckerId,
    this.fromWoreda,
    this.toWoreda,
  });

  @override
  List<Object?> get props => [fromWoreda, toWoreda, truckerId];
}
