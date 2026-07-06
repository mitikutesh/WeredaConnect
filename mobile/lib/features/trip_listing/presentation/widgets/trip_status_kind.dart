import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Maps trip lifecycle states onto the shared six-kind status vocabulary.
///
/// The single source for trip status visuals — used by the trip card,
/// its accent bar, and the detail screen so they can never disagree.
StatusKind tripStatusKind(TripStatus status) {
  return switch (status) {
    TripStatus.open => StatusKind.success,
    TripStatus.matched => StatusKind.info,
    TripStatus.inTransit => StatusKind.transit,
    TripStatus.delivered => StatusKind.success,
    TripStatus.cancelled => StatusKind.error,
  };
}
