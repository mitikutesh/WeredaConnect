import 'dart:math' as math;

import 'package:wereda_connect/core/models/app_notification.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_distance_service.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';

/// Who looks at a delivery's live trip (LT-11, specs/features/live-trip.md
/// sections 4 and 7). The trucker has no viewer here: their own trip mode is
/// LT-09, and these screens never give them the buyer's map.
enum LiveTripViewer { buyer, farmer }

/// The viewer [userId] is on [delivery], or null when the live trip screens
/// have nothing for them: the trucker of the delivery, anyone who is not a
/// party, and every party of a self-pickup (no truck, so no trail). The
/// server answers the track only to the delivery's parties anyway; this keeps
/// the trucker off the buyer's and farmer's screens.
LiveTripViewer? liveTripViewerOf(Delivery delivery, String userId) {
  if (userId.isEmpty || !isTruckDelivery(delivery)) return null;
  if (userId == delivery.buyerId) return LiveTripViewer.buyer;
  if (userId == delivery.farmerId) return LiveTripViewer.farmer;
  return null;
}

/// A delivery a truck carries: it has a trucker and is not a self-pickup.
bool isTruckDelivery(Delivery delivery) =>
    delivery.deliveryType != DeliveryConstants.selfPickupDeliveryType &&
    delivery.truckerId.isNotEmpty;

/// The truck picked the crop up and has not delivered it yet: the live map
/// runs (pickedUp or inTransit).
bool isOnTheRoad(Delivery delivery) =>
    isTruckDelivery(delivery) &&
    (delivery.status == DeliveryStatus.pickedUp ||
        delivery.status == DeliveryStatus.inTransit);

/// A truck delivery not picked up yet: its map starts at pickup.
bool isWaitingForPickup(Delivery delivery) =>
    isTruckDelivery(delivery) && delivery.status == DeliveryStatus.pending;

/// Whether the live trip screen has something to show for [delivery]: the
/// waiting card before pickup, the map on the road, the summary after.
bool hasLiveTrip(Delivery delivery) =>
    isTruckDelivery(delivery) && delivery.status != DeliveryStatus.cancelled;

/// Whether [track] follows the truck now (live, paused or scans only): the
/// server answers these only between pickup and delivery, so the screens
/// trust it over a delivery status this device may not have pulled yet.
bool trackFollowsTruck(DeliveryTrack? track) => switch (track?.sharing) {
      TrackSharing.live || TrackSharing.paused || TrackSharing.declined => true,
      _ => false,
    };

/// The buyer is "arriving" while the truck is within this distance of the
/// drop-off, the same line as the server's `truck_nearby` (live-trip-api.md
/// section 4).
const double arrivingWithinKm = 5;

/// The check-ins of [track] in capture order, the last check-in included
/// once.
List<TrackFix> trackCheckIns(DeliveryTrack track) {
  final fixes = [...track.trail];
  final last = track.lastFix;
  if (last != null &&
      (fixes.isEmpty || last.capturedAt.isAfter(fixes.last.capturedAt))) {
    fixes.add(last);
  }
  return fixes;
}

/// Great circle kilometres between two points.
double distanceKm(GeoPoint a, GeoPoint b) =>
    GpsDistanceService.haversineDistance(
      a.latitude,
      a.longitude,
      b.latitude,
      b.longitude,
    ) /
    1000;

/// The great circle distance from the last check-in to the drop-off point,
/// or null without either (the server may not know the drop-off point yet).
double? distanceToDropoffKm(DeliveryTrack track) {
  final last = track.lastFix ?? trackCheckIns(track).lastOrNull;
  final dropoff = track.dropoff;
  if (last == null || dropoff == null) return null;
  return distanceKm(last.point, dropoff);
}

/// Minutes until the truck reaches [stop], another order's stop that comes
/// before the viewer's drop-off (Buyer · Trip log board: "About 20 min, then
/// on to you"). Uses the pace of the server's own estimate for the viewer:
/// [DeliveryTrack.remainingKm] (great circle x 1.3) over the time to the
/// middle of [DeliveryTrack.eta], with the same 1.3 road factor for the
/// stop. Null when the stop is not nearer than the drop-off, or the track
/// has no last check-in, window or distance. Rounded up to 5 minutes.
int? minutesToStopAhead(DeliveryTrack track, TrackStop stop, DateTime now) {
  final last = track.lastFix;
  final eta = track.eta;
  final remaining = track.remainingKm;
  if (stop.done || last == null || eta == null || remaining == null) {
    return null;
  }
  if (remaining <= 0) return null;
  final middle = eta.from.add(eta.to.difference(eta.from) ~/ 2);
  final minutesToYou = middle.difference(now).inSeconds / 60;
  if (minutesToYou <= 0) return null;
  final stopKm = distanceKm(last.point, stop.point) * 1.3;
  if (stopKm >= remaining) return null;
  final minutes = stopKm / remaining * minutesToYou;
  return math.max(5, (minutes / 5).ceil() * 5);
}

/// The kilometres driven along the check-ins (straight legs between them,
/// so a little less than the road).
double trailDistanceKm(DeliveryTrack track) {
  final fixes = trackCheckIns(track);
  var total = 0.0;
  for (var i = 1; i < fixes.length; i++) {
    total += distanceKm(fixes[i - 1].point, fixes[i].point);
  }
  return total;
}

/// The share of the way driven, 0 at pickup to 1 at the drop-off (the thin
/// bar of the web deliveries board's list card): the kilometres along the
/// check-ins over those plus the kilometres left, the server's
/// [DeliveryTrack.remainingKm] or else the straight line to the drop-off.
/// Null when neither distance left is known.
double? tripProgressShare(DeliveryTrack track) {
  final remaining = track.remainingKm ?? distanceToDropoffKm(track);
  if (remaining == null) return null;
  final driven = trailDistanceKm(track);
  final total = driven + remaining;
  if (total <= 0) return null;
  return (driven / total).clamp(0.0, 1.0);
}

/// Whether the truck is arriving (spec section 6.2 and the Arriving board):
/// within [arrivingWithinKm] of the drop-off by the track, or the buyer got
/// the `truck_nearby` inbox entry ([nearbyNotified]). Never before pickup or
/// after delivery.
bool isArriving(DeliveryTrack track, {bool nearbyNotified = false}) {
  switch (track.sharing) {
    case TrackSharing.notStarted:
    case TrackSharing.ended:
    case TrackSharing.unknown:
      return false;
    case TrackSharing.live:
    case TrackSharing.paused:
    case TrackSharing.declined:
      break;
  }
  if (nearbyNotified) return true;
  final km = distanceToDropoffKm(track);
  if (km != null) return km <= arrivingWithinKm;
  final left = track.remainingKm;
  return left != null && left <= arrivingWithinKm;
}

/// Whether [entries] (the viewer's inbox) hold the `truck_nearby` entry of
/// [deliveryId] (notifications-api.md 1.3.0: entity `delivery`).
bool hasTruckNearbyEntry(
  Iterable<AppNotification> entries,
  String deliveryId,
) =>
    entries.any((e) => e.type == 'truck_nearby' && e.entityId == deliveryId);

/// What a row of the trip log is.
enum TripLogKind {
  /// The viewer's pickup: the first check-in of the window, a QR scan.
  pickup,

  /// A check-in on the way (`source` says which kind).
  checkIn,

  /// A QR scan after the pickup: another order's handoff on this truck,
  /// never named.
  otherStopScan,

  /// The driver paused sharing.
  paused,
}

/// One row of the trip log, in time order.
class TripLogEntry {
  const TripLogEntry({
    required this.kind,
    required this.at,
    this.fix,
    this.until,
    this.latest = false,
  });

  final TripLogKind kind;

  /// When it happened (UTC): the capture time, or a pause's start.
  final DateTime at;

  /// The check-in, for every kind but [TripLogKind.paused].
  final TrackFix? fix;

  /// A pause's end.
  final DateTime? until;

  /// The newest check-in.
  final bool latest;
}

/// The trip log of [track]: every check-in and pause of the viewer's window,
/// oldest first. The first check-in is the pickup when it is a QR scan.
List<TripLogEntry> tripLogEntries(DeliveryTrack track) {
  final fixes = trackCheckIns(track);
  final entries = <TripLogEntry>[
    for (var i = 0; i < fixes.length; i++)
      TripLogEntry(
        kind: fixes[i].source != 'scan'
            ? TripLogKind.checkIn
            : i == 0
                ? TripLogKind.pickup
                : TripLogKind.otherStopScan,
        at: fixes[i].capturedAt,
        fix: fixes[i],
        latest: i == fixes.length - 1,
      ),
    for (final pause in track.pauses)
      TripLogEntry(
        kind: TripLogKind.paused,
        at: pause.from,
        until: pause.until,
      ),
  ];
  // A stable sort, so a pause at the same minute as a check-in follows it.
  final indexed = entries.asMap().entries.toList()
    ..sort((a, b) {
      final byTime = a.value.at.compareTo(b.value.at);
      return byTime != 0 ? byTime : a.key.compareTo(b.key);
    });
  return [for (final e in indexed) e.value];
}
