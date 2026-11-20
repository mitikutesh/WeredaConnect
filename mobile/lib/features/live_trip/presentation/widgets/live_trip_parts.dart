import 'dart:math' as math;

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
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_painter.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_builder.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_map.dart';
import 'package:wereda_connect/features/messaging/presentation/screens/conversation_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/contact_actions_sheet.dart';
import 'package:wereda_connect/shared/widgets/dashed_border.dart';
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
      background: offline ? AppTheme.ink : null,
      foreground: offline ? Colors.white : null,
    ),
    const SizedBox(height: 12),
  ];
}

/// The boards' back button: a round paper button with an arrow, at the
/// start of a header row, or [floating] over a map with a soft shadow.
/// Pops the route.
class LiveTripBackButton extends StatelessWidget {
  const LiveTripBackButton({
    this.background = AppTheme.paper,
    this.foreground = AppTheme.ink,
    this.border,
    this.floating = false,
    super.key,
  });

  final Color background;
  final Color foreground;

  /// An edge; none by default.
  final Color? border;

  /// Over a map: lifted on a soft shadow.
  final bool floating;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return LiveTripSquareAction(
      key: const Key('live_trip_back'),
      icon: Icons.arrow_back_ios_new,
      semanticLabel: l10n.back,
      background: background,
      foreground: foreground,
      border: border,
      floating: floating,
      onTap: () => Navigator.of(context).maybePop(),
    );
  }
}

/// The soft shadow under a control that floats over the map.
const List<BoxShadow> liveTripFloatShadow = [
  BoxShadow(
    color: AppTheme.shadowSoft,
    blurRadius: 16,
    offset: Offset(0, 6),
  ),
];

/// A small status pill with a dot or an icon before the label (the
/// freshness chips, the Arriving, Farmer and Release pending pills of
/// page M): a stadium 26 tall, extra bold 12.
class DotPill extends StatelessWidget {
  const DotPill({
    required this.label,
    this.background = AppTheme.ground,
    this.foreground = AppTheme.ink,
    this.dotColor,
    this.icon,
    this.border,
    this.borderWidth = 1,
    this.height = 26,
    this.radius = 999,
    this.fontSize = 12,
    this.fontWeight = FontWeight.w800,
    super.key,
  });

  /// The boards' "Release pending": orange with a 1.5 px black edge.
  const DotPill.action({required this.label, super.key})
      : background = AppTheme.orange,
        foreground = AppTheme.ink,
        dotColor = null,
        icon = null,
        border = AppTheme.ink,
        borderWidth = 1.5,
        height = 26,
        radius = 999,
        fontSize = 12,
        fontWeight = FontWeight.w800;

  final String label;
  final Color background;
  final Color foreground;

  /// An 8 px dot before the label; nothing when null and [icon] is null.
  final Color? dotColor;
  final IconData? icon;
  final Color? border;
  final double borderWidth;
  final double height;
  final double radius;
  final double fontSize;
  final FontWeight fontWeight;

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: BoxConstraints(minHeight: height),
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 2),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(radius),
        border: border == null
            ? null
            : Border.all(color: border!, width: borderWidth),
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
                fontWeight: fontWeight,
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
    this.oneLine = false,
    super.key,
  });

  final DeliveryTrack track;
  final DateTime now;
  final List<Place> places;

  /// One line, for a list card.
  final bool compact;

  /// The map chip on one line ("11:42 · Bishoftu · 28 min ago", the web
  /// board) instead of the time over the age.
  final bool oneLine;

  @override
  Widget build(BuildContext context) {
    final band = trackFreshness(track, now);
    if (band == null) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context)!;
    final text = checkInChipText(context, track, now, places, band);
    final rhythm =
        band == TrackFreshness.fresh || band == TrackFreshness.normal;
    if (compact) {
      // The Foundations board's freshness chips: fresh on the warm grey
      // with a black dot, the hourly rhythm white with an edge and a grey
      // dot, late on the orange tint, paused and scans only neutral, ended
      // grey.
      final (background, foreground) = switch (band) {
        TrackFreshness.late => (AppTheme.orangeTint, AppTheme.orangeInk),
        TrackFreshness.paused || TrackFreshness.scansOnly => (
            AppTheme.segmentTrack,
            AppTheme.inkSoft
          ),
        TrackFreshness.ended => (AppTheme.dropoffSlateTint, AppTheme.slate),
        TrackFreshness.fresh => (AppTheme.segmentTrack, AppTheme.ink),
        TrackFreshness.normal => (AppTheme.surfaceWhite, AppTheme.ink),
      };
      final icon = switch (band) {
        TrackFreshness.late =>
          Icons.signal_cellular_connected_no_internet_0_bar,
        TrackFreshness.paused => Icons.pause_circle_outline,
        TrackFreshness.scansOnly => Icons.qr_code_2,
        TrackFreshness.ended => Icons.check_circle_outline,
        TrackFreshness.fresh || TrackFreshness.normal => null,
      };
      return DotPill(
        label: text.compact,
        background: background,
        foreground: foreground,
        border: band == TrackFreshness.normal ? AppTheme.line : null,
        dotColor: rhythm
            ? (band == TrackFreshness.fresh ? AppTheme.ink : AppTheme.slate)
            : null,
        icon: icon,
        fontWeight: FontWeight.w700,
      );
    }
    // Over the map (the Buyer live trip board): a white card with the time
    // and town over the age, on the orange tint when the check-in is late.
    final late = band == TrackFreshness.late;
    final foreground = late ? AppTheme.orangeInk : AppTheme.ink;
    return Semantics(
      container: true,
      label: l10n.liveTripTitle,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
        decoration: BoxDecoration(
          color: late ? AppTheme.orangeTint : AppTheme.surfaceWhite,
          borderRadius: BorderRadius.circular(14),
          boxShadow: liveTripFloatShadow,
        ),
        child: oneLine
            ? Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Container(
                    width: 8,
                    height: 8,
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      color: late ? AppTheme.orangeInk : AppTheme.slate,
                    ),
                  ),
                  const SizedBox(width: 7),
                  Flexible(
                    child: Text(
                      text.subtitle == null
                          ? text.title
                          : '${text.title} · ${text.subtitle}',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w700,
                        color: foreground,
                      ),
                    ),
                  ),
                ],
              )
            : Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    text.title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: TextStyle(
                      fontSize: 13,
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
                        fontSize: 13,
                        fontWeight: FontWeight.w500,
                        color: foreground,
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

/// The map with the last check-in chip beside the truck (the Buyer live
/// trip board), or at the top left when the chip sits there by design
/// ([chipBesideTruck] off, the web board) or has no room beside the truck.
/// Draws nothing for a track without coordinates: callers show the "map
/// starts at pickup" card or the summary instead.
class LiveTripMap extends StatelessWidget {
  const LiveTripMap({
    required this.track,
    required this.now,
    required this.places,
    this.height = 260,
    this.borderRadius = 16,
    this.showChip = true,
    this.chipTop = 12,
    this.chipBottom = 0,
    this.chipOneLine = false,
    this.chipBesideTruck = true,
    this.focus = TrackMapFocus.wholeRoute,
    this.zoom = 1,
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

  /// Where the map's free area starts: the chip never sits above it, so it
  /// clears the controls floating over the map's top. Also the top of the
  /// chip's fallback place at the top left.
  final double chipTop;

  /// How much of the map's foot something covers (the phone's sheet): the
  /// chip beside the truck keeps clear of it.
  final double chipBottom;

  /// The chip on one line (the web board).
  final bool chipOneLine;

  /// The chip next to the truck's square; off keeps it at the top left.
  final bool chipBesideTruck;

  /// What the map frames.
  final TrackMapFocus focus;

  /// How many times the framed view is magnified.
  final double zoom;

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
        child: LayoutBuilder(
          builder: (context, constraints) {
            final truck = !showChip || !chipBesideTruck
                ? null
                : TrackMapFrame.of(
                    track: track,
                    now: now,
                    size: constraints.biggest,
                    focus: focus,
                    zoom: zoom,
                  )?.truck;
            return Stack(
              children: [
                Positioned.fill(
                  child: TrackMap(
                    track: track,
                    now: now,
                    places: places,
                    focus: focus,
                    zoom: zoom,
                    semanticLabel: place == null
                        ? l10n.liveTripMapSemanticsNoPlace
                        : l10n.liveTripMapSemantics(place),
                  ),
                ),
                if (showChip)
                  Positioned.fill(
                    child: CustomSingleChildLayout(
                      delegate: CheckInChipPlacement(
                        truck: truck,
                        top: chipTop,
                        bottom: chipBottom,
                        textDirection: Directionality.of(context),
                      ),
                      child: CheckInChip(
                        track: track,
                        now: now,
                        places: places,
                        oneLine: chipOneLine,
                      ),
                    ),
                  ),
                ...overlay,
              ],
            );
          },
        ),
      ),
    );
  }
}

/// Places the check-in chip on the map: beside the truck's square (after
/// it, else before it, else under it, else over it), wherever it fits
/// whole inside the map's edge margin and below [top] (the floating
/// controls); at the top left under [top] when it fits nowhere or there is
/// no [truck].
class CheckInChipPlacement extends SingleChildLayoutDelegate {
  const CheckInChipPlacement({
    required this.truck,
    required this.top,
    this.bottom = 0,
    this.textDirection = TextDirection.ltr,
    this.truckSize = 30,
  });

  /// The centre of the truck's square on the map; null keeps the chip at
  /// the top left.
  final Offset? truck;

  /// Where the map's free area starts.
  final double top;

  /// How much of the map's foot is covered: the free area ends above it.
  final double bottom;
  final TextDirection textDirection;

  /// The side of the truck's square ([TrackMapStyle.truckSize]).
  final double truckSize;

  /// The space kept between the chip and the map's edges.
  static const double margin = 12;

  /// The space between the truck's square and the chip (the board).
  static const double gap = 8;

  @override
  BoxConstraints getConstraintsForChild(BoxConstraints constraints) =>
      BoxConstraints(
        maxWidth: math.max(constraints.maxWidth - 2 * margin, 0),
        maxHeight: math.max(constraints.maxHeight - top - margin, 0),
      );

  @override
  Offset getPositionForChild(Size size, Size childSize) =>
      place(size, childSize);

  /// The chip's top left corner on a map of [size].
  @visibleForTesting
  Offset place(Size size, Size childSize) {
    final fallback = Offset(
      textDirection == TextDirection.rtl
          ? size.width - margin - childSize.width
          : margin,
      top,
    );
    final truck = this.truck;
    if (truck == null) return fallback;
    final free = Rect.fromLTRB(
      margin,
      top,
      size.width - margin,
      size.height - bottom - margin,
    );
    final reach = truckSize / 2 + gap;
    final besideTop = truck.dy - childSize.height / 2;
    final after = Offset(truck.dx + reach, besideTop);
    final before = Offset(truck.dx - reach - childSize.width, besideTop);
    final centred = truck.dx - childSize.width / 2;
    final rtl = textDirection == TextDirection.rtl;
    for (final at in [
      if (rtl) before else after,
      if (rtl) after else before,
      Offset(centred, truck.dy + reach),
      Offset(centred, truck.dy - reach - childSize.height),
    ]) {
      final rect = at & childSize;
      if (rect.left >= free.left &&
          rect.top >= free.top &&
          rect.right <= free.right &&
          rect.bottom <= free.bottom) {
        return at;
      }
    }
    return fallback;
  }

  @override
  bool shouldRelayout(CheckInChipPlacement oldDelegate) =>
      oldDelegate.truck != truck ||
      oldDelegate.top != top ||
      oldDelegate.bottom != bottom ||
      oldDelegate.textDirection != textDirection ||
      oldDelegate.truckSize != truckSize;
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
    final done = ended ? AppTheme.ink : context.roleColors.primary;
    const ahead = AppTheme.track;
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
                  style: const TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w700,
                    color: AppTheme.inkSoft,
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
/// with the pickup time under the second. On a phone (page M) the steps
/// done are black, the current one orange with a black edge and extra bold,
/// the ones ahead on the empty track. [web] is the web deliveries board:
/// smaller labels, the done steps in the role colour and the current label
/// in the orange ink.
class TripProgress extends StatelessWidget {
  const TripProgress({required this.delivery, this.web = false, super.key});

  final Delivery delivery;
  final bool web;

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
    final done = web ? context.roleColors.primary : AppTheme.ink;
    return Semantics(
      container: true,
      child: Row(
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
                      key: i == reached
                          ? const Key('trip_progress_current')
                          : null,
                      height: i == reached ? 6 : 4,
                      margin: EdgeInsets.symmetric(
                        vertical: i == reached ? 0 : 1,
                      ),
                      decoration: BoxDecoration(
                        color: i < reached
                            ? done
                            : i == reached
                                ? AppTheme.orange
                                : AppTheme.track,
                        border: i == reached
                            ? Border.all(color: AppTheme.ink, width: 1.5)
                            : null,
                        borderRadius: BorderRadius.circular(999),
                      ),
                    ),
                    const SizedBox(height: 7),
                    Text(
                      steps[i],
                      maxLines: 3,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontSize: web ? 11 : 13,
                        height: 1.25,
                        fontWeight: i == reached
                            ? (web ? FontWeight.w700 : FontWeight.w800)
                            : i < reached
                                ? FontWeight.w700
                                : (web ? FontWeight.w600 : FontWeight.w500),
                        color: i == reached && web
                            ? AppTheme.orangeInk
                            : i <= reached
                                ? AppTheme.ink
                                : AppTheme.slate,
                      ),
                    ),
                  ],
                ),
              ),
            ),
        ],
      ),
    );
  }
}

/// A short note: late on the orange tint, paused and scans only inside a
/// dashed edge, offline on black (radius 24, the boards), or [plain] as an
/// icon and a line of text without a box (the escrow line, the scan note).
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

  /// No box: the icon in the role colour, the text in the secondary ink.
  final bool plain;

  /// Override the kind's colours (the offline note is dark with white text).
  final Color? background;
  final Color? foreground;

  @override
  Widget build(BuildContext context) {
    final dashed = !plain && background == null && kind == StatusKind.neutral;
    final iconColor = foreground ??
        (plain
            ? (kind == StatusKind.success
                ? context.roleColors.primary
                : kind.foreground)
            : dashed
                ? AppTheme.inkSoft
                : kind.foreground);
    final textColor =
        foreground ?? (plain || dashed ? AppTheme.inkSoft : kind.foreground);
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
              fontSize: 14,
              height: 1.4,
              fontWeight: FontWeight.w500,
              color: textColor,
            ),
          ),
        ),
      ],
    );
    if (plain) return row;
    const padding = EdgeInsets.symmetric(horizontal: 16, vertical: 12);
    if (dashed) {
      return DashedBorderBox(padding: padding, child: row);
    }
    return Container(
      width: double.infinity,
      padding: padding,
      decoration: BoxDecoration(
        color: background ?? kind.background,
        borderRadius: BorderRadius.circular(24),
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
    this.web = false,
    super.key,
  });

  final DeliveryTrack track;
  final DateTime now;

  /// Wraps the window in a sentence (the farmer's "Arrives {time}").
  final String Function(String time)? prefix;

  /// The web deliveries board's block: the window at 30 px in black.
  final bool web;

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
    // The farmer's line under the map: "Arrives 13:20-14:00" in the role
    // colour.
    final small = TextStyle(
      fontSize: 13,
      fontWeight: FontWeight.w800,
      color: context.roleColors.primary,
    );
    final subStyle = TextStyle(
      fontSize: web ? 12 : 14,
      height: 1.35,
      color: AppTheme.slate,
    );
    if (eta == null) {
      return Text(
        l10n.liveTripNoEta,
        style: compact
            ? small
            : const TextStyle(fontSize: 14, color: AppTheme.slate),
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
    // The boards' arrival block: a 13 px label, the window at 36 px in the
    // role colour, the distance at 14 px.
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
              color: AppTheme.slate,
            ),
          ),
        // One line: a narrow pane shrinks the window instead of breaking
        // the range in two.
        FittedBox(
          fit: BoxFit.scaleDown,
          alignment: AlignmentDirectional.centerStart,
          child: ClockText(
            label,
            style: TextStyle(
              fontSize: web ? 30 : 36,
              fontWeight: FontWeight.w800,
              letterSpacing: web ? -0.9 : -1.2,
              height: 1.1,
              color: web ? AppTheme.ink : context.roleColors.primary,
            ),
          ),
        ),
        if (sub != null) ...[
          const SizedBox(height: 2),
          Text(sub, style: subStyle),
        ],
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

/// A round icon button with a filled ground (the boards' back button over
/// a map, the Call button): [size] across in a 48 px hit area. (The name
/// is from the earlier boards, where it was a square.) [squared] keeps the
/// web board's radius 14 square.
class LiveTripSquareAction extends StatelessWidget {
  const LiveTripSquareAction({
    required this.icon,
    required this.semanticLabel,
    required this.onTap,
    this.background = AppTheme.ink,
    this.foreground = Colors.white,
    this.border,
    this.size = 44,
    this.floating = false,
    this.squared = false,
    super.key,
  });

  final IconData icon;
  final String semanticLabel;
  final VoidCallback onTap;
  final Color background;
  final Color foreground;
  final Color? border;
  final double size;

  /// Over a map: lifted on a soft shadow.
  final bool floating;
  final bool squared;

  @override
  Widget build(BuildContext context) {
    final side = border == null ? BorderSide.none : BorderSide(color: border!);
    final ShapeBorder shape = squared
        ? RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(14),
            side: side,
          )
        : CircleBorder(side: side);
    final button = Material(
      color: background,
      shape: shape,
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: SizedBox(
          width: size,
          height: size,
          child: Icon(icon, size: 20, color: foreground),
        ),
      ),
    );
    return Semantics(
      button: true,
      label: semanticLabel,
      child: FwTapTarget(
        onTap: onTap,
        child: floating
            ? DecoratedBox(
                decoration: ShapeDecoration(
                  shape: shape,
                  shadows: liveTripFloatShadow,
                ),
                child: button,
              )
            : button,
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

/// The avatar colours of a party: that party's own role colour on its
/// tint (a farmer is plum, a trucker blue, a buyer green in every app).
({Color background, Color foreground}) partyAvatarColors(UserRole? role) {
  final colors = RoleColors.forRole(role ?? UserRole.farmer);
  return (background: colors.tint, foreground: colors.primary);
}

/// The initials of a display name, two letters at most.
String partyInitials(String display) => display
    .split(RegExp(r'\s+'))
    .where((w) => w.isNotEmpty)
    .take(2)
    .map((w) => w.characters.first.toUpperCase())
    .join();

/// How a [PartyContactTile] draws its avatar and its two buttons.
enum PartyContactLook {
  /// The buyer's driver card: a round avatar, round 44 px buttons with
  /// "Message" and "Call" under them.
  circles,

  /// The farmer's contact rows: a rounded square avatar, boxed buttons
  /// with the icon over the word.
  boxes,

  /// The web deliveries board: icon only squares, Call in the role colour.
  icons,
}

/// A party of the order with Message and Call (the driver card and the
/// contact rows of the boards). The buttons show only once the order's
/// contact is open (ESC-48), and for the trucker only once committed, as
/// on the order screen: before that neither a phone number nor a call
/// action is ever shown. Message opens the order chat; Call opens the
/// shared contact sheet with the party's phone.
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
    this.look = PartyContactLook.circles,
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
  final PartyContactLook look;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final display = partyDisplayName(name, l10n);
    final first = firstNameOf(display);
    final colors = partyAvatarColors(role);
    final contact = canContactParty(order, viewerId, partyId);
    final web = look == PartyContactLook.icons;
    void call() => showPartyContact(
          context,
          order: order!,
          partyId: partyId,
          name: name,
          roleLabel: roleLabel,
        );
    void message() => openOrderChat(
          context,
          order: order!,
          partyId: partyId,
          partyName: name,
        );

    return Row(
      children: [
        Container(
          width: avatarSize,
          height: avatarSize,
          alignment: Alignment.center,
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(
              look == PartyContactLook.boxes ? 14 : 999,
            ),
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
        SizedBox(width: look == PartyContactLook.boxes ? 10 : 12),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                display,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontSize: web ? 14 : 15,
                  fontWeight: web ? FontWeight.w700 : FontWeight.w800,
                  letterSpacing: -0.15,
                  color: AppTheme.ink,
                ),
              ),
              // Two lines, so a truck and its plate ("Isuzu FSR 5 t ·
              // 3-AA-12345") stay whole in a narrow column.
              Text(
                subtitle ?? roleLabel,
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  fontSize: web ? 12 : 14,
                  height: 1.35,
                  color: web ? AppTheme.slate : AppTheme.inkSoft,
                ),
              ),
            ],
          ),
        ),
        if (contact) ...[
          const SizedBox(width: 8),
          _ContactButton(
            key: Key('party_message_$partyId'),
            look: look,
            icon: Icons.chat_bubble_outline,
            label: l10n.messageContact,
            semanticLabel: l10n.detailMessageParty(first),
            filled: false,
            onTap: message,
          ),
          SizedBox(width: look == PartyContactLook.circles ? 4 : 8),
          _ContactButton(
            key: Key('party_call_$partyId'),
            look: look,
            icon: Icons.phone_outlined,
            label: l10n.liveTripCall,
            semanticLabel: l10n.detailCallParty(first),
            filled: true,
            onTap: call,
          ),
        ],
      ],
    );
  }
}

/// Message (outlined) or Call (filled black) of a [PartyContactTile].
class _ContactButton extends StatelessWidget {
  const _ContactButton({
    required this.look,
    required this.icon,
    required this.label,
    required this.semanticLabel,
    required this.filled,
    required this.onTap,
    super.key,
  });

  final PartyContactLook look;
  final IconData icon;
  final String label;
  final String semanticLabel;
  final bool filled;
  final VoidCallback onTap;

  static const _labelStyle = TextStyle(
    fontSize: 13,
    fontWeight: FontWeight.w700,
    height: 1.15,
    color: AppTheme.ink,
  );

  @override
  Widget build(BuildContext context) {
    switch (look) {
      case PartyContactLook.icons:
        return LiveTripSquareAction(
          icon: icon,
          semanticLabel: semanticLabel,
          onTap: onTap,
          squared: true,
          background:
              filled ? context.roleColors.primary : AppTheme.surfaceWhite,
          foreground: filled ? Colors.white : AppTheme.ink,
          border: filled ? null : AppTheme.line,
        );
      case PartyContactLook.circles:
        return Semantics(
          button: true,
          label: semanticLabel,
          excludeSemantics: true,
          child: InkWell(
            onTap: onTap,
            customBorder: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(16),
            ),
            child: ConstrainedBox(
              constraints: const BoxConstraints(minWidth: 56, minHeight: 48),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 2),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Container(
                      width: 44,
                      height: 44,
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        color: filled ? AppTheme.ink : null,
                        border: filled
                            ? null
                            : Border.all(color: AppTheme.fieldBorder),
                      ),
                      child: Icon(
                        icon,
                        size: 20,
                        color: filled ? Colors.white : AppTheme.ink,
                      ),
                    ),
                    const SizedBox(height: 3),
                    Text(label, maxLines: 1, style: _labelStyle),
                  ],
                ),
              ),
            ),
          ),
        );
      case PartyContactLook.boxes:
        return Semantics(
          button: true,
          label: semanticLabel,
          excludeSemantics: true,
          child: Material(
            color: filled ? AppTheme.ink : Colors.transparent,
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(16),
              side: filled
                  ? BorderSide.none
                  : const BorderSide(color: AppTheme.fieldBorder),
            ),
            clipBehavior: Clip.antiAlias,
            child: InkWell(
              onTap: onTap,
              child: ConstrainedBox(
                constraints: const BoxConstraints(minWidth: 52, minHeight: 50),
                child: Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 8,
                    vertical: 5,
                  ),
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Icon(
                        icon,
                        size: 18,
                        color: filled ? Colors.white : AppTheme.ink,
                      ),
                      const SizedBox(height: 1),
                      Text(
                        label,
                        maxLines: 1,
                        style: _labelStyle.copyWith(
                          color: filled ? Colors.white : AppTheme.ink,
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        );
    }
  }
}

/// Three figures side by side (distance, time, check-ins): plain columns
/// on a sand card, or as [cards] (the trip log board: the first one black,
/// the others sand).
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
      double valueSize = 19,
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
                  letterSpacing: -0.4,
                  color: valueColor,
                ),
              ),
            ),
            Text(
              stat.label,
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 14,
                height: 1.35,
                color: labelColor,
              ),
            ),
          ],
        );
    if (!cards) {
      return Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          for (final (i, stat) in stats.indexed) ...[
            if (i > 0) const SizedBox(width: 8),
            Expanded(
              child: figure(
                stat,
                valueColor: AppTheme.ink,
                labelColor: AppTheme.inkSoft,
              ),
            ),
          ],
        ],
      );
    }
    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (final (i, stat) in stats.indexed) ...[
            if (i > 0) const SizedBox(width: 8),
            Expanded(
              child: Container(
                padding:
                    const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
                decoration: BoxDecoration(
                  color: i == 0 ? AppTheme.ink : AppTheme.sand,
                  borderRadius: BorderRadius.circular(24),
                ),
                child: figure(
                  stat,
                  valueSize: 20,
                  valueColor: i == 0 ? Colors.white : AppTheme.ink,
                  labelColor: i == 0 ? AppTheme.sand : AppTheme.inkSoft,
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

/// The map's legend (the Buyer live trip board): the solid trail is what
/// was driven, the dashes are the road ahead, the dashed orange ring is
/// where the truck is about now. The trail sample takes the role colour,
/// as the trail does on the map.
class LiveTripLegend extends StatelessWidget {
  const LiveTripLegend({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final style = TrackMapStyle.forRole(context.roleColors);
    Widget item(_LegendMark mark, String label) => Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            CustomPaint(
              size: Size(mark == _LegendMark.truck ? 16 : 22, 16),
              painter: _LegendPainter(mark, style),
            ),
            const SizedBox(width: 6),
            Flexible(
              child: Text(
                label,
                style: const TextStyle(fontSize: 13, color: AppTheme.inkSoft),
              ),
            ),
          ],
        );
    return Semantics(
      container: true,
      label: l10n.liveTripLegendLabel,
      child: Wrap(
        key: const Key('live_trip_legend'),
        spacing: 14,
        runSpacing: 4,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          item(_LegendMark.driven, l10n.liveTripLegendDriven),
          item(_LegendMark.ahead, l10n.liveTripLegendAhead),
          item(_LegendMark.truck, l10n.liveTripLegendTruck),
        ],
      ),
    );
  }
}

enum _LegendMark { driven, ahead, truck }

class _LegendPainter extends CustomPainter {
  const _LegendPainter(this.mark, this.style);

  final _LegendMark mark;
  final TrackMapStyle style;

  @override
  void paint(Canvas canvas, Size size) {
    final y = size.height / 2;
    switch (mark) {
      case _LegendMark.driven:
        canvas.drawLine(
          Offset(2, y),
          Offset(size.width - 2, y),
          Paint()
            ..color = style.trail
            ..strokeWidth = 4
            ..strokeCap = StrokeCap.round,
        );
      case _LegendMark.ahead:
        final paint = Paint()
          ..color = style.ahead.withValues(alpha: 0.55)
          ..strokeWidth = 2.5;
        for (var x = 1.0; x < size.width - 1; x += 9) {
          canvas.drawLine(
            Offset(x, y),
            Offset((x + 5).clamp(0, size.width - 1), y),
            paint,
          );
        }
      case _LegendMark.truck:
        final center = Offset(size.width / 2, y);
        canvas.drawCircle(
          center,
          6.5,
          Paint()..color = style.halo.withValues(alpha: 0.16),
        );
        final edge = Paint()
          ..color = style.haloEdge
          ..style = PaintingStyle.stroke
          ..strokeWidth = 1.5;
        final rect = Rect.fromCircle(center: center, radius: 6.5);
        for (var i = 0; i < 7; i++) {
          canvas.drawArc(rect, i * 0.8976, 0.45, false, edge);
        }
    }
  }

  @override
  bool shouldRepaint(_LegendPainter oldDelegate) =>
      oldDelegate.mark != mark || oldDelegate.style != style;
}
