import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/models/user.dart';
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
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// Why the shown map may not be current, as a note over it: offline (with
/// the saved map's time, the boards' dark status box), the server could not
/// read it, or the trip is not known. Nothing when the last read brought a
/// new answer.
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
  final offline = view.failure == TrackFailure.offline;
  return [
    LiveTripNote(
      icon: offline ? Icons.cloud_off : Icons.info_outline,
      text: text,
      kind: StatusKind.warning,
      background: offline ? AppTheme.soilDark : null,
      foreground: offline ? Colors.white : null,
    ),
    const SizedBox(height: 12),
  ];
}

/// The boards' back button: a white square with an arrow, over a map or at
/// the start of a header row. Pops the route.
class LiveTripBackButton extends StatelessWidget {
  const LiveTripBackButton({
    this.background = AppTheme.surfaceWhite,
    this.foreground = AppTheme.soilDark,
    this.border,
    super.key,
  });

  final Color background;
  final Color foreground;

  /// The edge; the whisper border by default, none on a dark header.
  final Color? border;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return LiveTripSquareAction(
      key: const Key('live_trip_back'),
      icon: Icons.arrow_back_ios_new,
      semanticLabel: l10n.back,
      background: background,
      foreground: foreground,
      border: border ?? AppTheme.whisperBorder,
      onTap: () => Navigator.of(context).maybePop(),
    );
  }
}

/// A small status pill with a dot or an icon before the label (the
/// Foundations board's freshness chips, the Arriving and Farmer pills):
/// 28 tall, radius 8, bold 12.
class DotPill extends StatelessWidget {
  const DotPill({
    required this.label,
    this.background = AppTheme.chalkBackground,
    this.foreground = AppTheme.soilDark,
    this.dotColor,
    this.icon,
    this.border,
    this.height = 28,
    this.radius = 8,
    this.fontSize = 12,
    super.key,
  });

  final String label;
  final Color background;
  final Color foreground;

  /// An 8 px dot before the label; nothing when null and [icon] is null.
  final Color? dotColor;
  final IconData? icon;
  final Color? border;
  final double height;
  final double radius;
  final double fontSize;

  @override
  Widget build(BuildContext context) {
    return Container(
      height: height,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(radius),
        border: border == null ? null : Border.all(color: border!),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, size: 14, color: foreground),
            const SizedBox(width: 6),
          ] else if (dotColor != null) ...[
            Container(
              width: 8,
              height: 8,
              decoration: BoxDecoration(
                shape: BoxShape.circle,
                color: dotColor,
              ),
            ),
            const SizedBox(width: 7),
          ],
          Flexible(
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: fontSize,
                fontWeight: FontWeight.w700,
                color: foreground,
              ),
            ),
          ),
        ],
      ),
    );
  }
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
    // The Foundations board's freshness colours: fresh on a green tint, the
    // hourly rhythm white, late on cream, paused and scans only on grey,
    // ended on the drop-off blue tint.
    final (background, foreground) = switch (band) {
      TrackFreshness.late => (AppTheme.actionCream, AppTheme.actionInk),
      TrackFreshness.paused || TrackFreshness.scansOnly => (
          AppTheme.segmentTrack,
          AppTheme.unselectedNav
        ),
      TrackFreshness.ended => (AppTheme.dropoffBlueTint, AppTheme.dropoffBlue),
      TrackFreshness.fresh => (AppTheme.fieldGreenTint, AppTheme.fieldGreen),
      TrackFreshness.normal => (AppTheme.surfaceWhite, AppTheme.soilDark),
    };
    final icon = switch (band) {
      TrackFreshness.late => Icons.signal_cellular_connected_no_internet_0_bar,
      TrackFreshness.paused => Icons.pause_circle_outline,
      TrackFreshness.scansOnly => Icons.qr_code_2,
      TrackFreshness.ended => Icons.check_circle_outline,
      TrackFreshness.fresh || TrackFreshness.normal => Icons.local_shipping,
    };
    final rhythm =
        band == TrackFreshness.fresh || band == TrackFreshness.normal;
    if (compact) {
      // The list card's pill: a dot for the rhythm, the band's icon else.
      return DotPill(
        label: text.compact,
        background: rhythm ? AppTheme.chalkBackground : background,
        foreground: foreground,
        border: rhythm ? AppTheme.whisperBorder : null,
        dotColor: rhythm
            ? (band == TrackFreshness.fresh
                ? AppTheme.fieldGreen
                : AppTheme.mutedSteel)
            : null,
        icon: rhythm ? null : icon,
        height: 26,
      );
    }
    return Semantics(
      container: true,
      label: l10n.liveTripTitle,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
        decoration: BoxDecoration(
          color: background,
          borderRadius: BorderRadius.circular(12),
          boxShadow: const [
            BoxShadow(
              color: Color(0x1F15201A),
              blurRadius: 14,
              offset: Offset(0, 4),
            ),
          ],
        ),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 16, color: foreground),
            const SizedBox(width: 7),
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
                      fontSize: 12,
                      fontWeight: FontWeight.w800,
                      color: foreground,
                    ),
                  ),
                  if (text.subtitle != null)
                    Text(
                      text.subtitle!,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontSize: 11,
                        fontWeight: FontWeight.w600,
                        color: foreground.withValues(alpha: 0.85),
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
    this.showChip = true,
    this.chipTop = 12,
    this.overlay = const [],
    super.key,
  });

  final DeliveryTrack track;
  final DateTime now;
  final List<Place> places;
  final double height;
  final double borderRadius;

  /// The check-in chip over the map; off when the screen shows it under
  /// the map instead (the farmer's card).
  final bool showChip;

  /// Where the chip starts, so it clears the controls floating over the map.
  final double chipTop;

  /// Widgets drawn over the map (the boards' floating back button and crop
  /// pill), positioned by the caller.
  final List<Widget> overlay;

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
            if (showChip)
              Positioned(
                left: 12,
                top: chipTop,
                right: 12,
                child: Align(
                  alignment: Alignment.topLeft,
                  child: CheckInChip(track: track, now: now, places: places),
                ),
              ),
            ...overlay,
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
    // The boards: done steps green, the current one gold with brown text,
    // the ones ahead on the whisper border colour.
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (var i = 0; i < steps.length; i++)
          Expanded(
            child: Padding(
              padding: EdgeInsets.only(right: i < steps.length - 1 ? 6 : 0),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Container(
                    height: 4,
                    decoration: BoxDecoration(
                      color: i < reached
                          ? AppTheme.fieldGreen
                          : i == reached
                              ? AppTheme.accentSoft
                              : AppTheme.whisperBorder,
                      borderRadius: BorderRadius.circular(999),
                    ),
                  ),
                  const SizedBox(height: 8),
                  Text(
                    steps[i],
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 11,
                      fontWeight:
                          i <= reached ? FontWeight.w700 : FontWeight.w600,
                      color: i == reached
                          ? AppTheme.money
                          : i < reached
                              ? AppTheme.soilDark
                              : AppTheme.mutedSteel,
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

/// A short note: late, paused and offline in a tinted box (radius 14, the
/// boards), or [plain] as an icon and a line of text without a box (the
/// escrow line, the scan note).
class LiveTripNote extends StatelessWidget {
  const LiveTripNote({
    required this.icon,
    required this.text,
    this.kind = StatusKind.info,
    this.plain = false,
    this.background,
    this.foreground,
    super.key,
  });

  final IconData icon;
  final ClockLabel text;
  final StatusKind kind;

  /// No box: the icon in the kind's colour, the text in the secondary ink.
  final bool plain;

  /// Override the kind's colours (the offline note is dark with white text).
  final Color? background;
  final Color? foreground;

  @override
  Widget build(BuildContext context) {
    final iconColor = foreground ??
        (plain
            ? (kind == StatusKind.success
                ? AppTheme.fieldGreen
                : kind.foreground)
            : kind.foreground);
    final textColor =
        foreground ?? (plain ? AppTheme.unselectedNav : kind.foreground);
    final row = Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.only(top: 1),
          child: Icon(icon, size: 18, color: iconColor),
        ),
        const SizedBox(width: 10),
        Expanded(
          child: ClockText(
            text,
            style: TextStyle(
              fontSize: 13,
              height: 1.45,
              fontWeight: plain ? FontWeight.w500 : FontWeight.w600,
              color: textColor,
            ),
          ),
        ),
      ],
    );
    if (plain) return row;
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: background ?? kind.background,
        borderRadius: BorderRadius.circular(14),
      ),
      child: row,
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
    this.compact = false,
    super.key,
  });

  final DeliveryTrack track;
  final DateTime now;

  /// Wraps the window in a sentence (the farmer's "Arrives {time}").
  final String Function(String time)? prefix;

  /// One small muted line (the farmer's card, beside the check-in chip):
  /// the sentence only, no label and no distance.
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final eta = track.eta;
    final note = track.dropoffNote?.trim() ?? '';
    final band = trackFreshness(track, now);
    final remaining = track.remainingKm;
    final sub = band == TrackFreshness.late
        ? l10n.liveTripWiderWindow
        : remaining == null
            ? null
            : l10n.liveTripKmToGo(kmText(remaining));
    const small = TextStyle(
      fontSize: 12,
      fontWeight: FontWeight.w500,
      color: AppTheme.mutedSteel,
    );
    if (eta == null) {
      return Text(
        l10n.liveTripNoEta,
        style: compact
            ? small
            : const TextStyle(fontSize: 14, color: AppTheme.mutedSteel),
      );
    }
    final window = clock.window(eta.from, eta.to);
    final sentence = prefix;
    final label = sentence == null
        ? window
        : ClockLabel(
            before: _before(sentence),
            time: window.time,
            time24: window.time24,
            after: _after(sentence),
          );
    if (compact) return ClockText(label, style: small);
    // The boards' arrival block: a 13 px label, the window at 30 px, the
    // distance at 12 px.
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (sentence == null)
          Text(
            note.isEmpty
                ? l10n.liveTripArrivalWindow
                : l10n.liveTripArrivingAt(note),
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w500,
              color: AppTheme.mutedSteel,
            ),
          ),
        // One line: a narrow pane shrinks the window instead of breaking
        // the range in two.
        FittedBox(
          fit: BoxFit.scaleDown,
          alignment: AlignmentDirectional.centerStart,
          child: ClockText(
            label,
            style: const TextStyle(
              fontSize: 30,
              fontWeight: FontWeight.w800,
              letterSpacing: -0.9,
              height: 1.2,
              color: AppTheme.soilDark,
            ),
          ),
        ),
        if (sub != null) Text(sub, style: small),
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

/// The Message and Call sheet for [partyId] of [order] (the shared contact
/// actions sheet, with the party's phone from the order).
void showPartyContact(
  BuildContext context, {
  required Order order,
  required String partyId,
  required String name,
  required String roleLabel,
}) {
  final l10n = AppLocalizations.of(context)!;
  final display = partyDisplayName(name, l10n);
  final navigator = Navigator.of(context);
  showContactActionsSheet(
    context,
    name: display.isEmpty ? roleLabel : display,
    roleLabel: roleLabel,
    phone: orderPartyPhone(order, partyId),
    onMessage: () => navigator.push(
      MaterialPageRoute<void>(
        builder: (_) => ConversationScreen(
          contextType: 'order',
          contextId: order.orderId,
          counterpartId: partyId,
          counterpartName: name,
        ),
      ),
    ),
  );
}

/// A square button with a filled ground (the boards' green Call button, the
/// arriving board's call beside Scan): [size] by [size], radius 14 (16 at 56).
class LiveTripSquareAction extends StatelessWidget {
  const LiveTripSquareAction({
    required this.icon,
    required this.semanticLabel,
    required this.onTap,
    this.background = AppTheme.fieldGreen,
    this.foreground = Colors.white,
    this.border,
    this.size = 44,
    super.key,
  });

  final IconData icon;
  final String semanticLabel;
  final VoidCallback onTap;
  final Color background;
  final Color foreground;
  final Color? border;
  final double size;

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: true,
      label: semanticLabel,
      child: Material(
        color: background,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(size >= 56 ? 16 : 14),
          side: border == null ? BorderSide.none : BorderSide(color: border!),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          child: SizedBox(
            width: size,
            height: size,
            child: Icon(icon, size: 20, color: foreground),
          ),
        ),
      ),
    );
  }
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

/// The avatar colours of a party by role (the boards): the farmer on the
/// green tint, the trucker brown on cream, the buyer on the drop-off blue
/// tint.
({Color background, Color foreground}) partyAvatarColors(UserRole? role) =>
    switch (role) {
      UserRole.trucker => (
          background: AppTheme.actionCream,
          foreground: AppTheme.money
        ),
      UserRole.buyer => (
          background: AppTheme.dropoffBlueTint,
          foreground: AppTheme.dropoffBlue
        ),
      UserRole.farmer || null => (
          background: AppTheme.fieldGreenTint,
          foreground: AppTheme.fieldGreen
        ),
    };

/// The initials of a display name, two letters at most.
String partyInitials(String display) => display
    .split(RegExp(r'\s+'))
    .where((w) => w.isNotEmpty)
    .take(2)
    .map((w) => w.characters.first.toUpperCase())
    .join();

/// A party of the order with Message and Call (the driver card and the
/// contact rows of the boards: a tinted avatar, the name, a white square
/// Message button and a green square Call button). The buttons show only
/// once the order's contact is open (ESC-48), and for the trucker only once
/// committed, as on the order screen.
class PartyContactTile extends StatelessWidget {
  const PartyContactTile({
    required this.name,
    required this.roleLabel,
    required this.partyId,
    required this.viewerId,
    this.order,
    this.subtitle,
    this.role,
    this.avatarSize = 46,
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

  /// The party's role, for the avatar colours.
  final UserRole? role;
  final double avatarSize;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final display = partyDisplayName(name, l10n);
    final colors = partyAvatarColors(role);
    final contact = canContactParty(order, viewerId, partyId);
    void open() => showPartyContact(
          context,
          order: order!,
          partyId: partyId,
          name: name,
          roleLabel: roleLabel,
        );

    return Row(
      children: [
        Container(
          width: avatarSize,
          height: avatarSize,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: colors.background,
          ),
          child: Text(
            partyInitials(display),
            style: TextStyle(
              fontSize: avatarSize >= 44 ? 16 : 13,
              fontWeight: FontWeight.w800,
              color: colors.foreground,
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
                style: TextStyle(
                  fontSize: avatarSize >= 44 ? 15 : 14,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.soilDark,
                ),
              ),
              Text(
                subtitle ?? roleLabel,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: const TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w500,
                  color: AppTheme.mutedSteel,
                ),
              ),
            ],
          ),
        ),
        if (contact) ...[
          const SizedBox(width: 8),
          FwSquareButton(
            semanticLabel: l10n.sendMessage,
            onTap: open,
            child: const Icon(
              Icons.chat_bubble_outline,
              size: 20,
              color: AppTheme.soilDark,
            ),
          ),
          const SizedBox(width: 8),
          LiveTripSquareAction(
            icon: Icons.phone_outlined,
            semanticLabel: l10n.call,
            onTap: open,
          ),
        ],
      ],
    );
  }
}

/// Three figures side by side (distance, time, check-ins): plain columns,
/// or as [cards] (the trip log board: the first one filled green).
class LiveTripStats extends StatelessWidget {
  const LiveTripStats({required this.stats, this.cards = false, super.key});

  final List<({String value, String label})> stats;
  final bool cards;

  @override
  Widget build(BuildContext context) {
    Widget figure(
      ({String value, String label}) stat, {
      required Color valueColor,
      required Color labelColor,
      double valueSize = 18,
    }) =>
        Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            FittedBox(
              fit: BoxFit.scaleDown,
              alignment: AlignmentDirectional.centerStart,
              child: Text(
                stat.value,
                maxLines: 1,
                style: TextStyle(
                  fontSize: valueSize,
                  fontWeight: FontWeight.w800,
                  color: valueColor,
                ),
              ),
            ),
            Text(
              stat.label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 12,
                fontWeight: FontWeight.w500,
                color: labelColor,
              ),
            ),
          ],
        );
    if (!cards) {
      return Row(
        children: [
          for (final stat in stats)
            Expanded(
              child: figure(
                stat,
                valueColor: AppTheme.soilDark,
                labelColor: AppTheme.mutedSteel,
              ),
            ),
        ],
      );
    }
    return Row(
      children: [
        for (final (i, stat) in stats.indexed) ...[
          if (i > 0) const SizedBox(width: 8),
          Expanded(
            child: Container(
              padding: const EdgeInsets.all(12),
              decoration: BoxDecoration(
                color: i == 0 ? AppTheme.fieldGreen : AppTheme.surfaceWhite,
                borderRadius: BorderRadius.circular(16),
                border:
                    i == 0 ? null : Border.all(color: AppTheme.whisperBorder),
              ),
              child: figure(
                stat,
                valueSize: 20,
                valueColor: i == 0 ? Colors.white : AppTheme.soilDark,
                labelColor:
                    i == 0 ? const Color(0xFFCFE3D6) : AppTheme.mutedSteel,
              ),
            ),
          ),
        ],
      ],
    );
  }
}
