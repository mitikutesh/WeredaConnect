import 'package:wereda_connect/core/models/trip.dart';
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
