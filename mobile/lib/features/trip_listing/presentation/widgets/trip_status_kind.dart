import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_departure.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Maps trip lifecycle states onto the shared six-kind status vocabulary.
///
/// The single source for trip status visuals and labels, used by the trip
/// card, its accent bar, and the detail screen so they can never disagree.
StatusKind tripStatusKind(TripStatus status) {
  return switch (status) {
    TripStatus.open => StatusKind.success,
    TripStatus.matched => StatusKind.info,
    TripStatus.inTransit => StatusKind.transit,
    TripStatus.delivered => StatusKind.success,
    TripStatus.cancelled => StatusKind.error,
  };
}

/// Localized display label for a trip status, shared by the trip card, the
/// detail screen badge and the Home recent-activity feed.
String tripStatusLabel(TripStatus status, AppLocalizations l10n) {
  return switch (status) {
    TripStatus.open => l10n.tripStatusOpen,
    TripStatus.matched => l10n.tripStatusMatched,
    TripStatus.inTransit => l10n.tripStatusInTransit,
    TripStatus.delivered => l10n.tripStatusDelivered,
    TripStatus.cancelled => l10n.tripStatusCancelled,
  };
}

/// What the buyer is told when a truck cannot take the order (B-2). A
/// closed or full truck reads like the server's refusal of the order
/// (`ORDER_TRIP_NOT_ELIGIBLE`, `TRIP_CAPACITY_EXCEEDED`); one that left says so.
String tripBookingProblemMessage(
  TripBookingProblem problem,
  AppLocalizations l10n,
) {
  return switch (problem) {
    TripBookingProblem.closed => l10n.errorTripNotEligible,
    TripBookingProblem.left => l10n.tripLeftChooseAnother,
    TripBookingProblem.full => l10n.errorTripCapacityExceeded,
  };
}
