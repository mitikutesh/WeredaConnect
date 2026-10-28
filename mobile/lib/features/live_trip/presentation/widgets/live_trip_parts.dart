import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/models/track_result.dart';
import 'package:wereda_connect/features/live_trip/domain/track_freshness.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_text.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_builder.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_map.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/conversation_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/contact_actions_sheet.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Why the shown map may not be current, as a note over it: offline (with
/// the saved map's time), the server could not read it, or the trip is not
/// known. Nothing when the last read brought a new answer.
List<Widget> trackReadNotice(BuildContext context, TrackView view) {
  final l10n = AppLocalizations.of(context)!;
  final clock = clockFormatOf(context);
  final snapshot = view.snapshot;
  final ClockLabel? text = switch (view.failure) {
    TrackFailure.offline => snapshot == null
        ? ClockLabel.text(l10n.liveTripOfflineNoMap)
        : clock.deadline(
            snapshot.fetchedAt,
            sentence: l10n.liveTripOfflineNote,
          ),
    TrackFailure.unavailable ||
    TrackFailure.failed =>
      ClockLabel.text(l10n.liveTripUnavailable),
    TrackFailure.notFound => ClockLabel.text(l10n.liveTripNotFound),
    null => null,
  };
  if (text == null) return const [];
  return [
    LiveTripNote(
      icon: view.failure == TrackFailure.offline
          ? Icons.cloud_off
          : Icons.info_outline,
      text: text,
      kind: StatusKind.warning,
    ),
    const SizedBox(height: 12),
  ];
}

/// The last check-in in one line and how current it is (the freshness chip
/// of the Foundations board): "11:42 · Bishoftu" over "28 min ago", or the
/// paused, scans only and ended states. Nothing before pickup.
class CheckInChip extends StatelessWidget {
  const CheckInChip({
    required this.track,
    required this.now,
    required this.places,
    this.compact = false,
    super.key,
  });

  final DeliveryTrack track;
  final DateTime now;
  final List<Place> places;

  /// One line, for a list card.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final band = trackFreshness(track, now);
    if (band == null) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context)!;
    final text = checkInChipText(context, track, now, places, band);
    final (background, foreground) = switch (band) {
      TrackFreshness.late => (
          AppTheme.statusWarningContainer,
          AppTheme.onStatusWarningContainer
        ),
      TrackFreshness.paused || TrackFreshness.scansOnly => (
          AppTheme.statusNeutralContainer,
          AppTheme.onStatusNeutralContainer
        ),
      TrackFreshness.ended => (
          AppTheme.statusSuccessContainer,
          AppTheme.onStatusSuccessContainer
        ),
      TrackFreshness.fresh || TrackFreshness.normal => (
          AppTheme.surfaceWhite,
          AppTheme.textDark
        ),
    };
    final icon = switch (band) {
      TrackFreshness.late => Icons.signal_cellular_connected_no_internet_0_bar,
      TrackFreshness.paused => Icons.pause_circle_outline,
      TrackFreshness.scansOnly => Icons.qr_code_2,
      TrackFreshness.ended => Icons.check_circle_outline,
      TrackFreshness.fresh || TrackFreshness.normal => Icons.local_shipping,
    };
    if (compact) {
      return Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 14, color: foreground),
          const SizedBox(width: 4),
          Flexible(
            child: Text(
              text.compact,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w600,
                color: band == TrackFreshness.fresh ||
                        band == TrackFreshness.normal
                    ? AppTheme.textMuted
                    : foreground,
              ),
            ),
          ),
        ],
      );
    }
    return Semantics(
      container: true,
      label: l10n.liveTripTitle,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        decoration: BoxDecoration(
          color: background,
          borderRadius: BorderRadius.circular(10),
          border: Border.all(color: foreground.withValues(alpha: 0.18)),
          boxShadow: const [
            BoxShadow(color: Color(0x1A000000), blurRadius: 4),
          ],
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 16, color: foreground),
            const SizedBox(width: 6),
            Flexible(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    text.title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                      color: foreground,
                    ),
                  ),
                  if (text.subtitle != null)
                    Text(
                      text.subtitle!,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontSize: 12,
                        color: foreground.withValues(alpha: 0.8),
                      ),
                    ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// The chip's text: [title] and [subtitle] for the map, [compact] for a card.
typedef CheckInChipText = ({String title, String? subtitle, String compact});

/// The words of a [CheckInChip] in [band] (every time through the clock
/// helper, LT-03).
CheckInChipText checkInChipText(
  BuildContext context,
  DeliveryTrack track,
  DateTime now,
  List<Place> places,
  TrackFreshness band,
) {
  final l10n = AppLocalizations.of(context)!;
  final clock = clockFormatOf(context);
  final languageCode = Localizations.localeOf(context).languageCode;
  final last = trackCheckIns(track).lastOrNull;
  final place = placeNameNear(places, last?.point, languageCode);
  final at = last == null
      ? null
      : place == null
          ? clock.time(last.capturedAt)
          : '${clock.time(last.capturedAt)} · $place';
  final age = trackFixAge(track, now) ?? Duration.zero;
  switch (band) {
    case TrackFreshness.fresh:
    case TrackFreshness.normal:
      return (
        title: at ?? l10n.liveTripStatusOnTheRoad,
        subtitle: checkInAge(l10n, age),
        compact: age < const Duration(minutes: 1)
            ? l10n.liveTripCheckedInJustNow
            : l10n.liveTripCheckedInAgo(liveTripDuration(l10n, age)),
      );
    case TrackFreshness.late:
      final late = l10n.liveTripNoCheckInFor(liveTripDuration(l10n, age));
      return (
        title: at ?? l10n.liveTripStatusOnTheRoad,
        subtitle: late,
        compact: late,
      );
    case TrackFreshness.paused:
      final until = track.pausedUntil;
      final untilText =
          until == null ? null : l10n.liveTripUntil(clock.time(until));
      return (
        title: place == null
            ? l10n.liveTripPaused
            : '${l10n.liveTripPaused} · $place',
        subtitle: untilText,
        compact: untilText == null
            ? l10n.liveTripPaused
            : '${l10n.liveTripPaused} · $untilText',
      );
    case TrackFreshness.scansOnly:
      return (
        title: at == null
            ? l10n.liveTripScansOnly
            : '${l10n.liveTripScansOnly} · $at',
        subtitle: l10n.liveTripScansOnlyChip,
        compact: l10n.liveTripScansOnly,
      );
    case TrackFreshness.ended:
      return (
        title: l10n.liveTripSharingEnded,
        subtitle: null,
        compact: l10n.liveTripSharingEnded,
      );
  }
}

/// The map with the last check-in chip over its top left corner. Draws
/// nothing for a track without coordinates: callers show the "map starts at
/// pickup" card or the summary instead.
class LiveTripMap extends StatelessWidget {
  const LiveTripMap({
    required this.track,
    required this.now,
    required this.places,
    this.height = 260,
    this.borderRadius = 16,
    super.key,
  });

  final DeliveryTrack track;
  final DateTime now;
  final List<Place> places;
  final double height;
  final double borderRadius;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final languageCode = Localizations.localeOf(context).languageCode;
    final last = trackCheckIns(track).lastOrNull;
    final place = placeNameNear(places, last?.point, languageCode);
    return ClipRRect(
      borderRadius: BorderRadius.circular(borderRadius),
      child: SizedBox(
        height: height,
        width: double.infinity,
        child: Stack(
          children: [
            Positioned.fill(
              child: TrackMap(
                track: track,
                now: now,
                places: places,
                semanticLabel: place == null
                    ? l10n.liveTripMapSemanticsNoPlace
                    : l10n.liveTripMapSemantics(place),
              ),
            ),
            Positioned(
              left: 12,
              top: 12,
              right: 12,
              child: Align(
                alignment: Alignment.topLeft,
                child: CheckInChip(track: track, now: now, places: places),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// One stop on a [RouteRibbon].
typedef RibbonStop = ({String label, String? time});

/// The route in one line (the route ribbon of the Foundations board): the
/// pickup, where the truck is, the drop-off, with a filled line up to the
/// stop the truck has reached.
class RouteRibbon extends StatelessWidget {
  const RouteRibbon({
    required this.stops,
    required this.reached,
    this.ended = false,
    super.key,
  });

  final List<RibbonStop> stops;

  /// The index of the last stop reached.
  final int reached;

  /// The trip ended: drawn in the muted colour.
  final bool ended;

  @override
  Widget build(BuildContext context) {
    final done = ended ? AppTheme.textMuted : AppTheme.primary;
    const ahead = AppTheme.borderLight;
    Widget dot(int i) => Container(
          width: 10,
          height: 10,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: i <= reached ? done : AppTheme.surfaceWhite,
            border: Border.all(color: i <= reached ? done : ahead, width: 2),
          ),
        );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            for (var i = 0; i < stops.length; i++) ...[
              if (i > 0)
                Expanded(
                  child: Container(
                    height: 3,
                    color: i <= reached ? done : ahead,
                  ),
                ),
              dot(i),
            ],
          ],
        ),
        const SizedBox(height: 4),
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            for (var i = 0; i < stops.length; i++)
              Expanded(
                child: Text(
                  stops[i].time == null
                      ? stops[i].label
                      : '${stops[i].label} ${stops[i].time}',
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  textAlign: i == 0
                      ? TextAlign.start
                      : i == stops.length - 1
                          ? TextAlign.end
                          : TextAlign.center,
                  style: TextStyle(
                    fontSize: 12,
                    fontWeight:
                        i == reached ? FontWeight.w700 : FontWeight.w500,
                    color:
                        i == reached ? AppTheme.textDark : AppTheme.textMuted,
                  ),
                ),
              ),
          ],
        ),
      ],
    );
  }
}

/// The ribbon stops of [delivery] for a viewer: the pickup's town and time,
/// the last check-in's town and time while on the road, and the drop-off
/// ([dropoffLabel], the buyer's landmark when there is one).
List<RibbonStop> ribbonStopsOf(
  BuildContext context, {
  required Delivery delivery,
  required DeliveryTrack? track,
  required List<Place> places,
  required String dropoffLabel,
}) {
  final l10n = AppLocalizations.of(context)!;
  final clock = clockFormatOf(context);
  final languageCode = Localizations.localeOf(context).languageCode;
  final gpsPickup = delivery.gpsPickup;
  final pickupPoint = track?.pickup ??
      (gpsPickup == null
          ? null
          : GeoPoint(gpsPickup.latitude, gpsPickup.longitude));
  final pickedUpAt = track?.pickedUpAt ?? delivery.pickedUpAt;
  final pickup = (
    label:
        placeNameNear(places, pickupPoint, languageCode) ?? l10n.liveTripPickup,
    time: pickedUpAt == null ? null : clock.time(pickedUpAt),
  );
  final note = track?.dropoffNote?.trim() ?? '';
  final confirmedAt = delivery.confirmedAt;
  final dropoff = (
    label: note.isNotEmpty ? note : dropoffLabel,
    time: track?.sharing == TrackSharing.ended && confirmedAt != null
        ? clock.time(confirmedAt)
        : null,
  );
  final last = track == null ? null : trackCheckIns(track).lastOrNull;
  if (track == null ||
      track.sharing == TrackSharing.ended ||
      last == null ||
      (pickedUpAt != null && !last.capturedAt.isAfter(pickedUpAt))) {
    return [pickup, dropoff];
  }
  return [
    pickup,
    (
      label: placeNameNear(places, last.point, languageCode) ??
          l10n.liveTripStatusOnTheRoad,
      time: clock.time(last.capturedAt),
    ),
    dropoff,
  ];
}

/// The four steps of a delivery (Agreed, Picked up, On the road, Delivered),
/// with the pickup time under the second.
class TripProgress extends StatelessWidget {
  const TripProgress({required this.delivery, super.key});

  final Delivery delivery;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final reached = switch (delivery.status) {
      DeliveryStatus.pending => 0,
      DeliveryStatus.pickedUp => 1,
      DeliveryStatus.inTransit => 2,
      DeliveryStatus.delivered || DeliveryStatus.disputed => 3,
      DeliveryStatus.cancelled => -1,
    };
    final pickedUpAt = delivery.pickedUpAt;
    final steps = [
      l10n.liveTripStepAgreed,
      pickedUpAt == null
          ? l10n.liveTripStepPickedUp
          : '${l10n.liveTripStepPickedUp} ${clock.time(pickedUpAt)}',
      l10n.liveTripStatusOnTheRoad,
      l10n.deliveryStatusDelivered,
    ];
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (var i = 0; i < steps.length; i++)
          Expanded(
            child: Padding(
              padding: EdgeInsets.only(right: i < steps.length - 1 ? 4 : 0),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Container(
                    height: 4,
                    decoration: BoxDecoration(
                      color: i <= reached
                          ? AppTheme.primary
                          : AppTheme.borderLight,
                      borderRadius: BorderRadius.circular(2),
                    ),
                  ),
                  const SizedBox(height: 4),
                  Text(
                    steps[i],
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 11,
                      fontWeight:
                          i == reached ? FontWeight.w700 : FontWeight.w500,
                      color:
                          i <= reached ? AppTheme.textDark : AppTheme.textMuted,
                    ),
                  ),
                ],
              ),
            ),
          ),
      ],
    );
  }
}

/// A short note in a tinted box: the escrow line, late, paused, offline.
class LiveTripNote extends StatelessWidget {
  const LiveTripNote({
    required this.icon,
    required this.text,
    this.kind = StatusKind.info,
    super.key,
  });

  final IconData icon;
  final ClockLabel text;
  final StatusKind kind;

  @override
  Widget build(BuildContext context) {
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: kind.background,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, size: 18, color: kind.foreground),
          const SizedBox(width: 8),
          Expanded(
            child: ClockText(
              text,
              style: TextStyle(fontSize: 13, color: kind.foreground),
            ),
          ),
        ],
      ),
    );
  }
}

/// The arrival window block: "Arriving at Merkato", the window on the
/// viewer's clock (with the small 24-hour range on the Ethiopian clock), and
/// how far is left. Says so when there is no window yet.
class ArrivalWindowBlock extends StatelessWidget {
  const ArrivalWindowBlock({
    required this.track,
    required this.now,
    this.prefix,
    super.key,
  });

  final DeliveryTrack track;
  final DateTime now;

  /// Wraps the window in a sentence (the farmer's "Arrives {time}").
  final String Function(String time)? prefix;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final theme = Theme.of(context);
    final eta = track.eta;
    final note = track.dropoffNote?.trim() ?? '';
    final band = trackFreshness(track, now);
    final remaining = track.remainingKm;
    final sub = band == TrackFreshness.late
        ? l10n.liveTripWiderWindow
        : remaining == null
            ? null
            : l10n.liveTripKmToGo(kmText(remaining));
    if (eta == null) {
      return Text(
        l10n.liveTripNoEta,
        style: theme.textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
      );
    }
    final window = clock.window(eta.from, eta.to);
    final sentence = prefix;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (sentence == null)
          Text(
            note.isEmpty
                ? l10n.liveTripArrivalWindow
                : l10n.liveTripArrivingAt(note),
            style:
                theme.textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
          ),
        ClockText(
          sentence == null
              ? window
              : ClockLabel(
                  before: _before(sentence),
                  time: window.time,
                  time24: window.time24,
                  after: _after(sentence),
                ),
          style: theme.textTheme.headlineSmall?.copyWith(
            fontWeight: FontWeight.w700,
            color: AppTheme.textDark,
          ),
        ),
        if (sub != null)
          Text(
            sub,
            style:
                theme.textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
          ),
      ],
    );
  }

  static const _slot = '\u{FFFC}';
  static String _before(String Function(String) sentence) {
    final whole = sentence(_slot);
    final at = whole.indexOf(_slot);
    return at < 0 ? '' : whole.substring(0, at);
  }

  static String _after(String Function(String) sentence) {
    final whole = sentence(_slot);
    final at = whole.indexOf(_slot);
    return at < 0 ? '' : whole.substring(at + _slot.length);
  }
}

/// Whether [viewerId] may reach [partyId] on [order] (ESC-48, as on the
/// order screen): once the order's contact is open, and the trucker only
/// once committed. False without the order.
bool canContactParty(Order? order, String viewerId, String partyId) {
  if (order == null || partyId.isEmpty || partyId == viewerId) return false;
  if (!isOrderContactOpen(order)) return false;
  if (partyId == order.truckerId &&
      partyId != order.buyerId &&
      partyId != order.farmerId) {
    return truckerResponseOf(order) == CommitmentResponse.accepted;
  }
  return true;
}

/// Opens the order chat with [partyId].
void openOrderChat(
  BuildContext context, {
  required Order order,
  required String partyId,
  required String partyName,
}) {
  Navigator.of(context).push(
    MaterialPageRoute<void>(
      builder: (_) => ConversationScreen(
        contextType: 'order',
        contextId: order.orderId,
        counterpartId: partyId,
        counterpartName: partyName,
      ),
    ),
  );
}

/// A party of the order with Message and Call (the driver card and the
/// contact rows). The buttons show only once the order's contact is open
/// (ESC-48), and for the trucker only once committed, as on the order
/// screen.
class PartyContactTile extends StatelessWidget {
  const PartyContactTile({
    required this.name,
    required this.roleLabel,
    required this.partyId,
    required this.viewerId,
    this.order,
    this.subtitle,
    super.key,
  });

  final String name;
  final String roleLabel;
  final String partyId;
  final String viewerId;

  /// The order, for the contact rules and the phone; null hides the
  /// buttons (not on this device yet).
  final Order? order;
  final String? subtitle;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final display = partyDisplayName(name, l10n);
    final initials = display
        .split(RegExp(r'\s+'))
        .where((w) => w.isNotEmpty)
        .take(2)
        .map((w) => w.characters.first.toUpperCase())
        .join();
    final contact = canContactParty(order, viewerId, partyId);
    void open() {
      final o = order!;
      final navigator = Navigator.of(context);
      showContactActionsSheet(
        context,
        name: display.isEmpty ? roleLabel : display,
        roleLabel: roleLabel,
        phone: orderPartyPhone(o, partyId),
        onMessage: () => navigator.push(
          MaterialPageRoute<void>(
            builder: (_) => ConversationScreen(
              contextType: 'order',
              contextId: o.orderId,
              counterpartId: partyId,
              counterpartName: name,
            ),
          ),
        ),
      );
    }

    return Row(
      children: [
        CircleAvatar(
          radius: 20,
          backgroundColor: AppTheme.primary.withValues(alpha: 0.12),
          child: Text(
            initials,
            style: const TextStyle(
              fontWeight: FontWeight.w700,
              color: AppTheme.primaryDeep,
            ),
          ),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                display,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: theme.textTheme.bodyLarge
                    ?.copyWith(fontWeight: FontWeight.w600),
              ),
              Text(
                subtitle ?? roleLabel,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: theme.textTheme.bodySmall
                    ?.copyWith(color: AppTheme.textMuted),
              ),
            ],
          ),
        ),
        if (contact) ...[
          IconButton.outlined(
            tooltip: l10n.sendMessage,
            onPressed: open,
            icon: const Icon(Icons.chat_outlined),
          ),
          const SizedBox(width: 8),
          IconButton.outlined(
            tooltip: l10n.call,
            onPressed: open,
            icon: const Icon(Icons.phone_outlined),
          ),
        ],
      ],
    );
  }
}

/// Three figures side by side (distance, time, check-ins).
class LiveTripStats extends StatelessWidget {
  const LiveTripStats({required this.stats, super.key});

  final List<({String value, String label})> stats;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      children: [
        for (final stat in stats)
          Expanded(
            child: Column(
              children: [
                Text(
                  stat.value,
                  style: theme.textTheme.titleLarge
                      ?.copyWith(fontWeight: FontWeight.w700),
                ),
                Text(
                  stat.label,
                  style: theme.textTheme.bodySmall
                      ?.copyWith(color: AppTheme.textMuted),
                ),
              ],
            ),
          ),
      ],
    );
  }
}
