import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/date_locale.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/data/delivery_constants.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/device_signing_key_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/photo_manager.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/qr_verification_service.dart';
import 'package:wereda_connect/features/delivery_verification/data/verification_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/dispute_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/qr_scan_screen.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/widgets/escrow_timeline.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/domain/track_freshness.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_data.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_text.dart';
import 'package:wereda_connect/features/live_trip/presentation/map/track_map_painter.dart';
import 'package:wereda_connect/features/live_trip/presentation/screens/trip_log_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_builder.dart';
import 'package:wereda_connect/features/order/presentation/open_order_detail.dart';
import 'package:wereda_connect/features/rating/presentation/widgets/rate_counterparties_section.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/dashed_border.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// The buyer's live trip of one delivery (LT-11, specs/features/live-trip.md
/// section 4; the Buyer · Live trip, Arriving and Delivered boards): the map
/// from the farmer's pickup with the last check-in, the arrival window, the
/// four steps, the escrow line, the driver, and the link to the trip log.
/// Before pickup it says the map starts at pickup; within 5 km of the
/// drop-off it shows the checklist and Scan driver's QR; after delivery the
/// trip summary, the escrow timeline, Report a problem while the 48 h window
/// lasts, and Rate Farmer and Rate Trucker.
///
/// As on the boards (design canvas page M) there is no app bar: on the road
/// the back button, the crop pill and "Whole route" float over the map and
/// the details sit on a paper sheet with the map's legend; otherwise the
/// back button heads the page.
class LiveTripScreen extends StatelessWidget {
  const LiveTripScreen({
    required this.delivery,
    required this.currentUserId,
    this.places,
    this.clock,
    super.key,
  });

  final Delivery delivery;
  final String currentUserId;

  /// The towns to name check-ins after; the bundled list by default.
  final List<Place>? places;
  final DateTime Function()? clock;

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: AppTheme.ground,
      body: SafeArea(
        child: Column(
          children: [
            const OfflineBanner(),
            Expanded(
              child: LiveTripView(
                delivery: delivery,
                currentUserId: currentUserId,
                places: places,
                clock: clock,
                leading: const LiveTripBackButton(),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// The body of [LiveTripScreen], also embedded beside the deliveries list on
/// a wide screen (web). Only the delivery's buyer gets it: anyone else sees
/// a short "not available" line and never the map (the farmer has
/// [FarmerTripView], the trucker their own trip mode).
class LiveTripView extends StatefulWidget {
  const LiveTripView({
    required this.delivery,
    required this.currentUserId,
    this.places,
    this.clock,
    this.leading,
    super.key,
  });

  final Delivery delivery;
  final String currentUserId;
  final List<Place>? places;
  final DateTime Function()? clock;

  /// The back button of a pushed screen, drawn over the map or at the start
  /// of the header row; null when embedded beside a list.
  final Widget? leading;

  @override
  State<LiveTripView> createState() => _LiveTripViewState();
}

class _LiveTripViewState extends State<LiveTripView> {
  late Delivery _delivery;
  Order? _order;
  Payment? _escrow;

  /// The truck on the driver card, for the buyer only.
  String? _vehicle;
  bool _nearby = false;

  /// What the buyer chose with "Whole route" over the map; null until
  /// then. A phone starts on the truck and the road ahead, the wide web
  /// map on the whole route.
  bool? _wholeRouteChoice;
  bool _escrowRequested = false;
  bool _reloadRequested = false;
  List<Place> _places = const [];

  @override
  void initState() {
    super.initState();
    _delivery = widget.delivery;
    _loadAll();
  }

  @override
  void didUpdateWidget(LiveTripView oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.delivery.deliveryId != widget.delivery.deliveryId) {
      _delivery = widget.delivery;
      _order = null;
      _escrow = null;
      _vehicle = null;
      _nearby = false;
      _wholeRouteChoice = null;
      _escrowRequested = false;
      _reloadRequested = false;
      _loadAll();
    } else if (oldWidget.places != widget.places && widget.places != null) {
      _places = widget.places!;
    }
  }

  void _loadAll() {
    final given = widget.places;
    if (given != null) {
      _places = given;
    } else {
      loadPlaces().then((places) {
        if (mounted) setState(() => _places = places);
      });
    }
    final delivery = _delivery;
    loadDeliveryOrder(context, delivery).then((order) {
      if (mounted && _delivery.deliveryId == delivery.deliveryId) {
        setState(() => _order = order);
      }
    });
    if (delivery.buyerId == widget.currentUserId) {
      loadDeliveryVehicle(context, delivery).then((vehicle) {
        if (mounted && _delivery.deliveryId == delivery.deliveryId) {
          setState(() => _vehicle = vehicle);
        }
      });
    }
    loadTruckNearby(context, widget.currentUserId, delivery.deliveryId)
        .then((nearby) {
      if (mounted && _delivery.deliveryId == delivery.deliveryId) {
        setState(() => _nearby = nearby);
      }
    });
    if (_isAfterDelivery(delivery)) _loadEscrow();
  }

  void _loadEscrow() {
    if (_escrowRequested) return;
    _escrowRequested = true;
    final delivery = _delivery;
    loadDeliveryEscrow(context, delivery).then((escrow) {
      if (mounted && _delivery.deliveryId == delivery.deliveryId) {
        setState(() => _escrow = escrow);
      }
    });
  }

  /// The server says the trip ended while this device still has the
  /// delivery on the road: read the delivery again (once) for its status
  /// and delivery time.
  void _reloadDelivery() {
    if (_reloadRequested) return;
    _reloadRequested = true;
    final delivery = _delivery;
    reloadDelivery(context, delivery, widget.currentUserId).then((fresh) {
      if (fresh != null &&
          mounted &&
          _delivery.deliveryId == delivery.deliveryId) {
        setState(() => _delivery = fresh);
      }
    });
    _loadEscrow();
  }

  /// Back from the QR scan: the delivery may be confirmed now (this
  /// device writes the confirmation at once, the server follows when it
  /// syncs), so read it and the track again.
  Future<void> _afterScan(TrackView view) async {
    final delivery = _delivery;
    final fresh = await reloadDelivery(context, delivery, widget.currentUserId);
    if (fresh != null &&
        mounted &&
        _delivery.deliveryId == delivery.deliveryId) {
      setState(() => _delivery = fresh);
      if (_isAfterDelivery(fresh)) _loadEscrow();
    }
    if (mounted) await view.refresh();
  }

  static bool _isAfterDelivery(Delivery delivery) =>
      delivery.status == DeliveryStatus.delivered ||
      delivery.status == DeliveryStatus.disputed ||
      delivery.status == DeliveryStatus.cancelled;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    if (liveTripViewerOf(_delivery, widget.currentUserId) !=
        LiveTripViewer.buyer) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Text(l10n.unauthorizedAccess, textAlign: TextAlign.center),
        ),
      );
    }
    return TrackBuilder(
      deliveryId: _delivery.deliveryId,
      userId: widget.currentUserId,
      clock: widget.clock,
      builder: (context, view) {
        final track = view.track;
        if (track?.sharing == TrackSharing.ended &&
            !_isAfterDelivery(_delivery)) {
          WidgetsBinding.instance.addPostFrameCallback((_) {
            if (mounted) _reloadDelivery();
          });
        }
        // On the road the map runs edge to edge with the details on a sheet
        // over its foot (spec section 4: map hero plus bottom sheet).
        final hero = !_isAfterDelivery(_delivery) &&
            track != null &&
            track.sharing != TrackSharing.ended &&
            trackFollowsTruck(track);
        return LayoutBuilder(
          builder: (context, constraints) {
            final wide = constraints.maxWidth >= _wideWidth;
            return ColoredBox(
              // Under the sheet the page is the sheet's paper, to the
              // bottom of the screen.
              color: hero && !wide ? AppTheme.paper : AppTheme.ground,
              child: RefreshIndicator(
                onRefresh: view.refresh,
                child: ListView(
                  // Embedded beside the deliveries list, the pane's cards line
                  // up with the list's edge, as on the board.
                  padding: (hero && !wide) || (wide && widget.leading == null)
                      ? const EdgeInsets.only(bottom: 32)
                      : const EdgeInsets.fromLTRB(20, 0, 20, 32),
                  children: _content(
                    context,
                    view,
                    wide: wide,
                    twoColumns: constraints.maxWidth >= _twoColumnWidth,
                  ),
                ),
              ),
            );
          },
        );
      },
    );
  }

  /// From this width (the web pane beside the deliveries list) the map sits
  /// in a bordered card with the details and the check-ins in cards under
  /// it, as on the Buyer · Live deliveries board.
  static const _wideWidth = 560.0;

  /// From this width the two cards sit side by side (the pane beside the
  /// deliveries list at 1280 px); narrower panes stack them. The arrival
  /// window scales down to stay on one line.
  static const _twoColumnWidth = 600.0;

  List<Widget> _content(
    BuildContext context,
    TrackView view, {
    bool wide = false,
    bool twoColumns = false,
  }) {
    final l10n = AppLocalizations.of(context)!;
    final track = view.track;
    final ended =
        _isAfterDelivery(_delivery) || track?.sharing == TrackSharing.ended;
    if (ended) {
      return [
        _DeliveredSummary(
          delivery: _delivery,
          order: _order,
          escrow: _escrow,
          track: track,
          places: _places,
          userId: widget.currentUserId,
          now: view.now,
          leading: widget.leading,
        ),
      ];
    }
    final onRoad = track != null && trackFollowsTruck(track);
    final arriving = onRoad && isArriving(track, nearbyNotified: _nearby);
    final status = _statusOf(l10n, track, view.now);
    final wholeRoute = _wholeRouteChoice ?? wide;
    final focus =
        wholeRoute ? TrackMapFocus.wholeRoute : TrackMapFocus.roadAhead;
    void toggleRoute() => setState(() => _wholeRouteChoice = !wholeRoute);
    final statusPill = _StatusPill(
      label: status.label,
      quiet: status.kind == StatusKind.neutral,
      web: wide,
    );
    // The driver: on a phone a sand card with Message and Call under their
    // round buttons; on the web board a white row with square buttons.
    final driver = Container(
      padding: wide
          ? const EdgeInsets.all(12)
          : const EdgeInsets.fromLTRB(14, 12, 12, 12),
      decoration: BoxDecoration(
        color: wide ? AppTheme.surfaceWhite : AppTheme.sand,
        borderRadius: BorderRadius.circular(wide ? 16 : 24),
        border: wide ? Border.all(color: AppTheme.line) : null,
      ),
      child: PartyContactTile(
        name: _delivery.truckerName,
        roleLabel: l10n.trucker,
        partyId: _delivery.truckerId,
        viewerId: widget.currentUserId,
        order: _order,
        subtitle: _vehicle,
        role: UserRole.trucker,
        avatarSize: wide ? 42 : 46,
        look: wide ? PartyContactLook.icons : PartyContactLook.circles,
      ),
    );
    final band = onRoad ? trackFreshness(track, view.now) : null;
    final rhythm = band == null ||
        band == TrackFreshness.fresh ||
        band == TrackFreshness.normal;
    final details = <Widget>[
      if (arriving)
        _ArrivingPanel(
          delivery: _delivery,
          order: _order,
          track: track,
          now: view.now,
          viewerId: widget.currentUserId,
          onScanned: () => _afterScan(view),
        )
      else ...[
        if (onRoad)
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: ArrivalWindowBlock(
                  track: track,
                  now: view.now,
                  web: wide,
                ),
              ),
              const SizedBox(width: 12),
              statusPill,
            ],
          )
        else
          _Header(delivery: _delivery, order: _order),
        const SizedBox(height: 16),
        TripProgress(delivery: _delivery, web: wide),
        // The web board says where the money is under the driver; a late,
        // paused or scans only truck still explains itself here.
        if (!wide || !rhythm) ...[
          const SizedBox(height: 16),
          _infoNote(context, track, view.now),
        ],
      ],
      const SizedBox(height: 16),
      driver,
      if (onRoad && wide && !arriving) ...[
        const SizedBox(height: 14),
        _EscrowRow(order: _order, delivery: _delivery),
      ],
      if (onRoad && (!wide || arriving)) ...[
        const SizedBox(height: 16),
        _TripLogLink(
          count: trackCheckIns(track).length,
          onTap: () => Navigator.of(context).push(
            MaterialPageRoute<void>(
              builder: (_) => TripLogScreen(
                delivery: _delivery,
                currentUserId: widget.currentUserId,
                track: track,
                order: _order,
                places: _places,
                now: view.now,
              ),
            ),
          ),
        ),
      ],
    ];
    if (!onRoad) {
      return [
        LiveTripTopRow(leading: widget.leading, trailing: statusPill),
        ...trackReadNotice(context, view),
        _WaitingCard(loading: view.loading),
        const SizedBox(height: 16),
        ...details,
      ];
    }
    if (wide) {
      // The Web deliveries board: the map in a bordered card with the
      // one-line check-in chip and the whole route control, the arrival
      // card and the check-ins in two white cards under it.
      return [
        // A pushed screen on a wide window still needs its back button.
        if (widget.leading != null)
          LiveTripTopRow(leading: widget.leading)
        else
          const SizedBox(height: 4),
        Container(
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(22),
            border: Border.all(color: AppTheme.line),
          ),
          child: LiveTripMap(
            track: track,
            now: view.now,
            places: _places,
            height: 340,
            borderRadius: 22,
            chipTop: 16,
            chipOneLine: true,
            focus: focus,
            overlay: [
              Positioned(
                right: 16,
                top: 16,
                child: _WholeRouteButton(
                  selected: wholeRoute,
                  iconOnly: true,
                  squared: true,
                  onTap: toggleRoute,
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 20),
        if (twoColumns)
          IntrinsicHeight(
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Expanded(
                  child: _WebCard(
                    children: [...trackReadNotice(context, view), ...details],
                  ),
                ),
                const SizedBox(width: 20),
                Expanded(
                  child: _CheckInsCard(
                    delivery: _delivery,
                    track: track,
                    places: _places,
                  ),
                ),
              ],
            ),
          )
        else ...[
          _WebCard(
            children: [...trackReadNotice(context, view), ...details],
          ),
          const SizedBox(height: 20),
          _CheckInsCard(delivery: _delivery, track: track, places: _places),
        ],
      ];
    }
    final crop = _order == null
        ? l10n.liveTripFrom(partyDisplayName(_delivery.farmerName, l10n))
        : '${CropType.labelFor(_order!.cropType, l10n)} · '
            '${l10n.quantityKg(birrText(_order!.quantity))}';
    return [
      LiveTripMap(
        track: track,
        now: view.now,
        places: _places,
        height: arriving ? 330 : 440,
        borderRadius: 0,
        chipTop: 76,
        // Arriving: the last kilometres (the Truck arriving board).
        focus: arriving ? TrackMapFocus.roadAhead : focus,
        overlay: [
          Positioned(
            left: 20,
            right: 20,
            top: 20,
            child: LayoutBuilder(
              builder: (context, constraints) {
                // Back, the crop, "Whole route". When the three do not fit
                // (Amharic words, a small phone, large text) the control
                // drops its icon first and its words last, so the crop
                // stays readable.
                final scaler = MediaQuery.textScalerOf(context);
                double width(String text) {
                  final painter = TextPainter(
                    text: TextSpan(
                      text: text,
                      style: DefaultTextStyle.of(context).style.merge(
                            const TextStyle(
                              fontSize: 13,
                              fontWeight: FontWeight.w700,
                            ),
                          ),
                    ),
                    textDirection: Directionality.of(context),
                    textScaler: scaler,
                    maxLines: 1,
                  )..layout();
                  final width = painter.width;
                  painter.dispose();
                  return width;
                }

                final room =
                    constraints.maxWidth - 48 - 12 - (width(crop) + 52);
                final words = width(l10n.liveTripWholeRoute);
                final showIcon = room >= words + 52;
                final showWords = room >= words + 26;
                return Row(
                  children: [
                    // Over the map the back button floats on a shadow.
                    if (widget.leading == null)
                      const SizedBox(width: 44)
                    else
                      const LiveTripBackButton(floating: true),
                    const SizedBox(width: 6),
                    Expanded(child: Center(child: _CropPill(label: crop))),
                    const SizedBox(width: 6),
                    if (arriving)
                      const SizedBox(width: 44)
                    else
                      _WholeRouteButton(
                        selected: wholeRoute,
                        iconOnly: !showWords,
                        showIcon: showIcon || !showWords,
                        onTap: toggleRoute,
                      ),
                  ],
                );
              },
            ),
          ),
        ],
      ),
      _Sheet(
        children: [
          if (!arriving) ...[
            const LiveTripLegend(),
            const SizedBox(height: 16),
          ],
          ...trackReadNotice(context, view),
          ...details,
        ],
      ),
    ];
  }

  ({String label, StatusKind kind}) _statusOf(
    AppLocalizations l10n,
    DeliveryTrack? track,
    DateTime now,
  ) {
    if (track == null || !trackFollowsTruck(track)) {
      return (label: l10n.liveTripWaitingForPickup, kind: StatusKind.neutral);
    }
    return switch (trackFreshness(track, now)) {
      TrackFreshness.paused => (
          label: l10n.liveTripPaused,
          kind: StatusKind.neutral,
        ),
      TrackFreshness.scansOnly => (
          label: l10n.liveTripScansOnly,
          kind: StatusKind.neutral,
        ),
      TrackFreshness.late => (
          label: l10n.liveTripStatusOnTheRoad,
          kind: StatusKind.warning,
        ),
      _ => (label: l10n.liveTripStatusOnTheRoad, kind: StatusKind.transit),
    };
  }

  /// The one note under the steps: why the map looks as it does (late,
  /// paused, scans only, in a tinted box), else where the money is (a plain
  /// line with the lock, as on the board).
  Widget _infoNote(BuildContext context, DeliveryTrack? track, DateTime now) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final trucker = partyDisplayName(_delivery.truckerName, l10n);
    final band = track == null || !trackFollowsTruck(track)
        ? null
        : trackFreshness(track, now);
    final pausedUntil = track?.pausedUntil;
    switch (band) {
      case TrackFreshness.late:
        return LiveTripNote(
          icon: Icons.signal_cellular_connected_no_internet_0_bar,
          text: ClockLabel.text(l10n.liveTripLateNote(trucker)),
          kind: StatusKind.warning,
        );
      case TrackFreshness.paused when pausedUntil != null:
        return LiveTripNote(
          icon: Icons.pause_circle_outline,
          text: clock.deadline(
            pausedUntil,
            sentence: (time) => l10n.liveTripPausedNote(trucker, time),
          ),
          kind: StatusKind.neutral,
        );
      case TrackFreshness.scansOnly:
        return LiveTripNote(
          icon: Icons.qr_code_2,
          text: ClockLabel.text(l10n.liveTripScansOnlyNote(trucker)),
          kind: StatusKind.neutral,
        );
      default:
        final total = _order?.orderTotal ?? _order?.totalPrice;
        return LiveTripNote(
          icon: Icons.lock_outline,
          text: ClockLabel.text(
            total == null
                ? l10n.liveTripEscrowNoteNoAmount
                : l10n.liveTripEscrowNote(l10n.etbCurrency(birrText(total))),
          ),
          kind: StatusKind.success,
          plain: true,
        );
    }
  }
}

/// The header row of a page without a map hero (the Delivered and Farmer
/// boards): the back button at the start, a pill at the end.
class LiveTripTopRow extends StatelessWidget {
  const LiveTripTopRow({required this.leading, this.trailing, super.key});

  final Widget? leading;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    if (leading == null && trailing == null) return const SizedBox(height: 8);
    return Padding(
      padding: const EdgeInsets.fromLTRB(0, 20, 0, 16),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          leading ?? const SizedBox.shrink(),
          if (trailing != null) ...[
            const SizedBox(width: 12),
            // A long pill label (Afaan Oromoo) shrinks instead of
            // overflowing; it may take all the room the button leaves.
            Flexible(child: trailing!),
          ],
        ],
      ),
    );
  }
}

/// The trip's status at the end of the arrival row: "On the road" on the
/// orange tint, [quiet] (paused, scans only, waiting) on the neutral grey.
/// A stadium on a phone; the [web] board keeps its radius 8 tag.
class _StatusPill extends StatelessWidget {
  const _StatusPill({
    required this.label,
    required this.quiet,
    this.web = false,
  });

  final String label;
  final bool quiet;
  final bool web;

  @override
  Widget build(BuildContext context) {
    return DotPill(
      key: const Key('live_trip_status'),
      label: label,
      background: quiet ? AppTheme.segmentTrack : AppTheme.orangeTint,
      foreground: quiet ? AppTheme.inkSoft : AppTheme.orangeInk,
      height: web ? 28 : 26,
      radius: web ? 8 : 999,
      fontWeight: web ? FontWeight.w700 : FontWeight.w800,
    );
  }
}

/// "Wheat · 2,000 kg" over the map: a white pill with the crop's sprout.
class _CropPill extends StatelessWidget {
  const _CropPill({required this.label});

  final String label;

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: const BoxConstraints(minHeight: 34),
      padding: const EdgeInsets.symmetric(horizontal: 13, vertical: 4),
      decoration: const ShapeDecoration(
        color: AppTheme.surfaceWhite,
        shape: StadiumBorder(),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          const FwSprout(size: 20, color: AppTheme.ink),
          const SizedBox(width: 6),
          Flexible(
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: const TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.w700,
                color: AppTheme.ink,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// "Whole route" over the map (the Buyer live trip board): shows the whole
/// route from the pickup; black while it does, and a second tap returns to
/// the truck and the road ahead. [iconOnly] on a small phone and on the web
/// board, where it is one of the map's square controls.
class _WholeRouteButton extends StatelessWidget {
  const _WholeRouteButton({
    required this.selected,
    required this.onTap,
    this.iconOnly = false,
    this.showIcon = true,
    this.squared = false,
  });

  final bool selected;
  final VoidCallback onTap;
  final bool iconOnly;

  /// The target icon before the words; left out when they are long.
  final bool showIcon;
  final bool squared;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final background = selected
        ? AppTheme.ink
        : (squared ? AppTheme.surfaceWhite : AppTheme.paper);
    final foreground = selected ? Colors.white : AppTheme.ink;
    final ShapeBorder shape = squared
        ? RoundedRectangleBorder(borderRadius: BorderRadius.circular(12))
        : const StadiumBorder();
    return Semantics(
      button: true,
      selected: selected,
      label: l10n.liveTripWholeRoute,
      hint: selected ? l10n.liveTripNearTruck : null,
      excludeSemantics: true,
      child: FwTapTarget(
        onTap: onTap,
        child: DecoratedBox(
          decoration: ShapeDecoration(
            shape: shape,
            shadows: liveTripFloatShadow,
          ),
          child: Material(
            key: const Key('live_trip_whole_route'),
            color: background,
            shape: shape,
            clipBehavior: Clip.antiAlias,
            child: InkWell(
              onTap: onTap,
              child: Container(
                constraints: BoxConstraints(
                  minHeight: 44,
                  minWidth: squared ? 40 : 44,
                ),
                padding: EdgeInsets.fromLTRB(
                  iconOnly ? 10 : 12,
                  4,
                  iconOnly ? 10 : 14,
                  4,
                ),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    if (showIcon || iconOnly)
                      Icon(Icons.my_location, size: 20, color: foreground),
                    if (!iconOnly) ...[
                      if (showIcon) const SizedBox(width: 6),
                      Text(
                        l10n.liveTripWholeRoute,
                        style: TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w700,
                          color: foreground,
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// A card of the web deliveries board: white, a hairline edge, radius 22,
/// padding 20.
class _WebCard extends StatelessWidget {
  const _WebCard({required this.children, super.key});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: AppTheme.surfaceWhite,
        borderRadius: BorderRadius.circular(22),
        border: Border.all(color: AppTheme.line),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: children,
      ),
    );
  }
}

/// Under the driver on the web board: the lock and "91,200 ETB in escrow"
/// at the start, the "Order Details" link in the role colour at the end.
class _EscrowRow extends StatelessWidget {
  const _EscrowRow({required this.order, required this.delivery});

  final Order? order;
  final Delivery delivery;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final total = order?.orderTotal ?? order?.totalPrice;
    final orderId = delivery.orderId;
    return Row(
      children: [
        Icon(Icons.lock_outline, size: 16, color: context.roleColors.primary),
        const SizedBox(width: 8),
        Expanded(
          child: Text(
            total == null
                ? l10n.liveTripInEscrow
                : l10n.liveTripEscrowAmount(l10n.etbCurrency(birrText(total))),
            key: const Key('live_trip_escrow_row'),
            style: const TextStyle(fontSize: 13, color: AppTheme.inkSoft),
          ),
        ),
        if (orderId != null && orderId.isNotEmpty)
          TextButton(
            key: const Key('live_trip_order_details'),
            onPressed: () => openOrderDetail(context, orderId),
            child: Text(l10n.orderDetails),
          ),
      ],
    );
  }
}

/// The check-ins beside the details on a wide screen, with who sees the
/// truck (the Web deliveries board).
class _CheckInsCard extends StatelessWidget {
  const _CheckInsCard({
    required this.delivery,
    required this.track,
    required this.places,
  });

  final Delivery delivery;
  final DeliveryTrack track;
  final List<Place> places;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return _WebCard(
      key: const Key('live_trip_check_ins_card'),
      children: [
        const CheckInsTitle(web: true),
        const SizedBox(height: 10),
        TripCheckInList(track: track, places: places, compact: true),
        const SizedBox(height: 10),
        Container(
          padding: const EdgeInsets.only(top: 10),
          decoration: const BoxDecoration(
            border: Border(top: BorderSide(color: AppTheme.line)),
          ),
          child: Text(
            l10n.liveTripPrivacyNote(
              partyDisplayName(delivery.farmerName, l10n),
            ),
            style: const TextStyle(
              fontSize: 12,
              height: 1.5,
              color: AppTheme.slate,
            ),
          ),
        ),
      ],
    );
  }
}

/// The details on the road: a paper sheet with a handle, drawn over the
/// foot of the map (the Buyer live trip and Truck arriving boards).
class _Sheet extends StatelessWidget {
  const _Sheet({required this.children});

  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    return Transform.translate(
      offset: const Offset(0, -24),
      child: Container(
        padding: const EdgeInsets.fromLTRB(20, 12, 20, 0),
        decoration: const BoxDecoration(
          color: AppTheme.paper,
          borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
          boxShadow: [
            BoxShadow(
              color: Color(0x241A1516),
              blurRadius: 30,
              offset: Offset(0, -14),
            ),
          ],
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Center(
              child: Container(
                width: 40,
                height: 4,
                margin: const EdgeInsets.only(bottom: 10),
                decoration: BoxDecoration(
                  color: AppTheme.sketch,
                  borderRadius: BorderRadius.circular(999),
                ),
              ),
            ),
            ...children,
          ],
        ),
      ),
    );
  }
}

/// Before pickup (or while the first answer loads): no map yet, said
/// inside a dashed edge, as everything that waits is.
class _WaitingCard extends StatelessWidget {
  const _WaitingCard({required this.loading});

  final bool loading;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return DashedBorderBox(
      child: Row(
        children: [
          if (loading)
            const SizedBox(
              width: 28,
              height: 28,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          else
            const Icon(Icons.map_outlined, size: 28, color: AppTheme.slate),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.liveTripWaitingForPickup,
                  style: const TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.ink,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  l10n.liveTripMapStartsAtPickup,
                  style: const TextStyle(
                    fontSize: 14,
                    height: 1.35,
                    color: AppTheme.ink,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// "Wheat · 2,000 kg" (from the order once read; the farmer's name until
/// then) and the farmer, before pickup.
class _Header extends StatelessWidget {
  const _Header({required this.delivery, required this.order});

  final Delivery delivery;
  final Order? order;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final o = order;
    final from = l10n.liveTripFrom(partyDisplayName(delivery.farmerName, l10n));
    final title = o == null
        ? from
        : '${CropType.labelFor(o.cropType, l10n)} · '
            '${l10n.quantityKg(birrText(o.quantity))}';
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          title,
          style: const TextStyle(
            fontSize: 19,
            fontWeight: FontWeight.w800,
            letterSpacing: -0.4,
            color: AppTheme.ink,
          ),
        ),
        if (o != null)
          Text(
            from,
            style: const TextStyle(fontSize: 14, color: AppTheme.slate),
          ),
      ],
    );
  }
}

/// "Trip log and order" with the check-in count: an outlined pill 50 tall
/// (the board).
class _TripLogLink extends StatelessWidget {
  const _TripLogLink({required this.count, required this.onTap});

  final int count;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Semantics(
      button: true,
      child: Material(
        color: Colors.transparent,
        shape: const StadiumBorder(
          side: BorderSide(color: AppTheme.fieldBorder),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          key: const Key('live_trip_log_link'),
          onTap: onTap,
          child: Container(
            constraints: const BoxConstraints(minHeight: 50),
            padding: const EdgeInsets.fromLTRB(18, 6, 14, 6),
            child: Row(
              children: [
                const Icon(
                  Icons.receipt_long_outlined,
                  size: 18,
                  color: AppTheme.ink,
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(
                    l10n.liveTripTripLogLink,
                    maxLines: 2,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontSize: 14,
                      fontWeight: FontWeight.w800,
                      color: AppTheme.ink,
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                Text(
                  l10n.liveTripCheckIns(count),
                  style: const TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w500,
                    color: AppTheme.slate,
                  ),
                ),
                const SizedBox(width: 4),
                const Icon(
                  Icons.chevron_right,
                  size: 16,
                  color: AppTheme.slate,
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The truck is within 5 km (the Truck arriving board): the Arriving pill,
/// how far, the checklist in a chalk box, the scan note, and Scan driver's
/// QR (the existing buyer delivery scan) beside Call.
class _ArrivingPanel extends StatefulWidget {
  const _ArrivingPanel({
    required this.delivery,
    required this.order,
    required this.track,
    required this.now,
    required this.viewerId,
    required this.onScanned,
  });

  final Delivery delivery;
  final Order? order;
  final DeliveryTrack track;
  final DateTime now;
  final String viewerId;
  final Future<void> Function() onScanned;

  @override
  State<_ArrivingPanel> createState() => _ArrivingPanelState();
}

class _ArrivingPanelState extends State<_ArrivingPanel> {
  final _checked = <int>{};

  Future<void> _scan() async {
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => QrScanScreen(
          delivery: widget.delivery,
          verificationType: VerificationType.delivery,
          qrService: QrVerificationService(
            deviceKeyService: DeviceSigningKeyService(),
          ),
          gpsService: GpsCaptureService(),
          photoManager: PhotoManager(),
        ),
      ),
    );
    if (mounted) await widget.onScanned();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final track = widget.track;
    final trucker = partyDisplayName(widget.delivery.truckerName, l10n);
    final farmer = partyDisplayName(widget.delivery.farmerName, l10n);
    final eta = track.eta;
    final minutes = eta?.from.difference(widget.now).inMinutes;
    final heading = minutes == null || minutes <= 0
        ? l10n.liveTripArrivingNear(trucker)
        : l10n.liveTripArrivingMinutes(trucker, minutes);
    final age = trackFixAge(track, widget.now) ?? Duration.zero;
    final km = distanceToDropoffKm(track);
    final checkedIn = age < const Duration(minutes: 1)
        ? l10n.liveTripCheckedInJustNow
        : l10n.liveTripCheckedInAgo(liveTripDuration(l10n, age));
    final items = [
      l10n.liveTripCheckCount,
      l10n.liveTripCheckQuality,
      l10n.liveTripCheckWeigh,
    ];
    final order = widget.order;
    final canCall = order != null &&
        canContactParty(order, widget.viewerId, widget.delivery.truckerId);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Align(
          alignment: AlignmentDirectional.centerStart,
          child: DotPill(
            label: l10n.liveTripStatusArriving,
            background: context.roleColors.tint,
            foreground: context.roleColors.primary,
            dotColor: context.roleColors.primary,
          ),
        ),
        const SizedBox(height: 6),
        Text(
          heading,
          style: const TextStyle(
            fontSize: 28,
            fontWeight: FontWeight.w800,
            letterSpacing: -1,
            height: 1.05,
            color: AppTheme.ink,
          ),
        ),
        const SizedBox(height: 6),
        Text(
          km == null
              ? checkedIn
              : '$checkedIn · ${l10n.liveTripKmFromDropoff(kmText(km))}',
          style: const TextStyle(
            fontSize: 14,
            fontWeight: FontWeight.w500,
            color: AppTheme.slate,
          ),
        ),
        const SizedBox(height: 16),
        Container(
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 6),
          decoration: BoxDecoration(
            color: AppTheme.sand,
            borderRadius: BorderRadius.circular(24),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                l10n.liveTripBeforeYouScan.toUpperCase(),
                style: FwText.sectionLabel.copyWith(color: AppTheme.inkSoft),
              ),
              for (var i = 0; i < items.length; i++)
                InkWell(
                  onTap: () => setState(
                    () => _checked.contains(i)
                        ? _checked.remove(i)
                        : _checked.add(i),
                  ),
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(minHeight: 44),
                    child: Row(
                      children: [
                        SizedBox(
                          width: 24,
                          height: 24,
                          child: Checkbox(
                            value: _checked.contains(i),
                            onChanged: (value) => setState(
                              () => value == true
                                  ? _checked.add(i)
                                  : _checked.remove(i),
                            ),
                            activeColor: context.roleColors.primary,
                            materialTapTargetSize:
                                MaterialTapTargetSize.shrinkWrap,
                            visualDensity: VisualDensity.compact,
                          ),
                        ),
                        const SizedBox(width: 10),
                        Expanded(
                          child: Text(
                            items[i],
                            style: const TextStyle(
                              fontSize: 14,
                              fontWeight: FontWeight.w500,
                              color: AppTheme.ink,
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
            ],
          ),
        ),
        const SizedBox(height: 16),
        LiveTripNote(
          icon: Icons.lock_outline,
          text: ClockLabel.text(l10n.liveTripScanConfirmsNote(farmer, trucker)),
          kind: StatusKind.success,
          plain: true,
        ),
        const SizedBox(height: 16),
        // The one orange action, with a black Call beside it once the
        // order's contact rules allow it (never before).
        Row(
          children: [
            Expanded(
              child: FilledButton.icon(
                key: const Key('live_trip_scan_qr'),
                onPressed: _scan,
                icon: const Icon(Icons.qr_code_scanner, size: 22),
                label: Text(
                  l10n.liveTripScanDriversQr,
                  textAlign: TextAlign.center,
                ),
              ),
            ),
            if (canCall) ...[
              const SizedBox(width: 10),
              FilledButton.icon(
                key: const Key('live_trip_arriving_call'),
                onPressed: () => showPartyContact(
                  context,
                  order: order,
                  partyId: widget.delivery.truckerId,
                  name: widget.delivery.truckerName,
                  roleLabel: l10n.trucker,
                ),
                icon: const Icon(Icons.phone_outlined, size: 22),
                label: Text(l10n.liveTripCall),
                style: FilledButton.styleFrom(
                  backgroundColor: AppTheme.ink,
                  foregroundColor: Colors.white,
                  minimumSize: const Size(0, 54),
                  padding: const EdgeInsets.fromLTRB(16, 0, 20, 0),
                  textStyle: const TextStyle(
                    fontFamily: AppTheme.fontFamily,
                    fontFamilyFallback: AppTheme.ethiopicFontFallback,
                    fontSize: 15,
                    fontWeight: FontWeight.w800,
                  ),
                ),
              ),
            ],
          ],
        ),
      ],
    );
  }
}

/// After delivery (sharing ended; the Buyer · Delivered board): the header
/// row with the back button and the "Sharing ended" pill, the green check
/// and "Delivered at", the trip in numbers with the drop-off proof, the
/// escrow card (or timeline), Report a problem while the window lasts, and
/// the ratings in their own card.
class _DeliveredSummary extends StatelessWidget {
  const _DeliveredSummary({
    required this.delivery,
    required this.order,
    required this.escrow,
    required this.track,
    required this.places,
    required this.userId,
    required this.now,
    this.leading,
  });

  final Delivery delivery;
  final Order? order;
  final Payment? escrow;
  final DeliveryTrack? track;
  final List<Place> places;
  final String userId;
  final DateTime now;
  final Widget? leading;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final confirmedAt = delivery.confirmedAt;
    final delivered = delivery.status == DeliveryStatus.delivered;
    final summary = track?.summary;
    final pickedUpAt = track?.pickedUpAt ?? delivery.pickedUpAt;
    final o = order;
    final windowEnd = confirmedAt?.add(DeliveryConstants.disputeWindow);
    final canReport = delivered && windowEnd != null && now.isBefore(windowEnd);
    final duration = summary != null
        ? Duration(minutes: summary.durationMin)
        : pickedUpAt != null && confirmedAt != null
            ? confirmedAt.difference(pickedUpAt)
            : null;
    final report = !canReport
        ? null
        : OutlinedButton(
            key: const Key('live_trip_report_problem'),
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) =>
                    DisputeScreen(delivery: delivery, userId: userId),
              ),
            ),
            style: OutlinedButton.styleFrom(
              minimumSize: const Size.fromHeight(48),
              textStyle: const TextStyle(
                fontFamily: AppTheme.fontFamily,
                fontFamilyFallback: AppTheme.ethiopicFontFallback,
                fontSize: 14,
                fontWeight: FontWeight.w800,
              ),
            ),
            // A Row of its own, so a long deadline (am, om) wraps instead of
            // overflowing the button.
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                const Icon(Icons.report_problem_outlined, size: 18),
                const SizedBox(width: 8),
                Flexible(
                  child: ClockText(
                    clock.deadline(
                      windowEnd,
                      date: formatMonthDay,
                      sentence: l10n.liveTripReportProblemUntil,
                    ),
                    textAlign: TextAlign.center,
                  ),
                ),
              ],
            ),
          );
    final subtitle = o == null
        ? l10n.liveTripFrom(partyDisplayName(delivery.farmerName, l10n))
        : '${CropType.labelFor(o.cropType, l10n)} · '
            '${l10n.quantityKg(birrText(o.quantity))} · '
            '${l10n.liveTripFrom(partyDisplayName(delivery.farmerName, l10n))}';
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        LiveTripTopRow(
          leading: leading,
          trailing: DotPill(
            label: l10n.liveTripSharingEnded,
            background: AppTheme.sand,
            foreground: AppTheme.inkSoft,
            icon: Icons.check,
          ),
        ),
        Row(
          children: [
            // Buyer delivered board: a check in the role colour beside
            // the title.
            if (delivered) ...[
              Container(
                key: const Key('live_trip_delivered_check'),
                width: 52,
                height: 52,
                decoration: BoxDecoration(
                  color: context.roleColors.primary,
                  borderRadius: BorderRadius.circular(14),
                ),
                child: const Icon(Icons.check, color: Colors.white, size: 28),
              ),
              const SizedBox(width: 14),
            ],
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  ClockText(
                    delivered && confirmedAt != null
                        ? clock.deadline(
                            confirmedAt,
                            sentence: l10n.liveTripDeliveredAt,
                          )
                        : ClockLabel.text(
                            delivery.status == DeliveryStatus.disputed
                                ? l10n.deliveryStatusDisputed
                                : delivery.status == DeliveryStatus.cancelled
                                    ? l10n.deliveryStatusCancelled
                                    : l10n.liveTripEnded,
                          ),
                    style: const TextStyle(
                      fontSize: 30,
                      fontWeight: FontWeight.w800,
                      letterSpacing: -1,
                      height: 1.05,
                      color: AppTheme.ink,
                    ),
                  ),
                  const SizedBox(height: 2),
                  Text(
                    subtitle,
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w500,
                      color: AppTheme.slate,
                    ),
                  ),
                ],
              ),
            ),
          ],
        ),
        const SizedBox(height: 18),
        FwCard.sand(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              RouteRibbon(
                stops: ribbonStopsOf(
                  context,
                  delivery: delivery,
                  track: track,
                  places: places,
                  dropoffLabel: l10n.liveTripYou,
                ),
                reached: 1,
                ended: true,
              ),
              if (summary != null || duration != null) ...[
                const SizedBox(height: 10),
                Container(
                  padding: const EdgeInsets.only(top: 10),
                  decoration: const BoxDecoration(
                    border: Border(
                      top: BorderSide(color: AppTheme.sketch),
                    ),
                  ),
                  child: LiveTripStats(
                    stats: [
                      if (summary != null)
                        (
                          value: l10n.liveTripKm(kmText(summary.distanceKm)),
                          label: l10n.liveTripStatDriven,
                        ),
                      if (duration != null)
                        (
                          value: liveTripDuration(l10n, duration),
                          label: l10n.liveTripStatOnTheRoad,
                        ),
                      if (summary != null)
                        (
                          value: '${summary.fixes}',
                          label: l10n.liveTripStatCheckIns,
                        ),
                    ],
                  ),
                ),
              ],
              if (confirmedAt != null) ...[
                const SizedBox(height: 12),
                Text(
                  '${l10n.liveTripDropoffProof}: '
                  '${l10n.liveTripQrScannedAt(clock.time(confirmedAt))} · '
                  '${delivery.gpsDropoff != null ? l10n.liveTripGpsRecorded : '${l10n.deliveryGpsLabel}: ${l10n.deliveryGpsNotCaptured}'}',
                  style: const TextStyle(
                    fontSize: 14,
                    height: 1.35,
                    color: AppTheme.inkSoft,
                  ),
                ),
              ],
            ],
          ),
        ),
        // Buyer delivered board: while the money waits out the 48 h
        // window, one card says who confirmed, when the window ends and who
        // is paid what. Any other escrow state keeps the full timeline.
        if (escrow != null &&
            escrow!.status == PaymentStatus.escrowed &&
            delivered &&
            confirmedAt != null) ...[
          const SizedBox(height: 18),
          _EscrowCard(
            escrow: escrow!,
            order: o,
            delivery: delivery,
            confirmedAt: confirmedAt,
            report: report,
          ),
        ] else ...[
          if (escrow != null) ...[
            const SizedBox(height: 20),
            EscrowTimeline(payment: escrow!),
          ],
          if (report != null) ...[
            const SizedBox(height: 12),
            report,
          ],
        ],
        if (delivered && (delivery.orderId?.isNotEmpty ?? false)) ...[
          const SizedBox(height: 18),
          Row(
            children: [
              const Icon(Icons.star_outline, size: 16, color: AppTheme.slate),
              const SizedBox(width: 8),
              Expanded(child: FwSectionLabel(l10n.liveTripHowDidItGo)),
            ],
          ),
          const SizedBox(height: 10),
          RateCounterpartiesSection(
            orderId: delivery.orderId!,
            buyerId: userId,
            farmerId: delivery.farmerId,
            farmerName: delivery.farmerName,
            truckerId: delivery.truckerId,
            truckerName: delivery.truckerName,
          ),
        ],
      ],
    );
  }
}

/// The money after delivery (Buyer · Delivered board): the amount held, the
/// buyer's confirmation, the end of the 48 h window, who is paid what, and
/// Report a problem while the window lasts.
class _EscrowCard extends StatelessWidget {
  const _EscrowCard({
    required this.escrow,
    required this.order,
    required this.delivery,
    required this.confirmedAt,
    this.report,
  });

  final Payment escrow;
  final Order? order;
  final Delivery delivery;
  final DateTime confirmedAt;
  final Widget? report;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    const date = formatMonthDay;
    final windowEnd = confirmedAt.add(DeliveryConstants.disputeWindow);
    // The escrow's own split; an older escrow without it, the order's.
    final transport = escrow.truckerPayout ?? order?.transportCost ?? 0;
    final payouts = [
      l10n.liveTripPartyGets(
        partyDisplayName(delivery.farmerName, l10n),
        l10n.etbCurrency(birrText(escrow.farmerPayout)),
      ),
      if (transport > 0 && delivery.truckerId.isNotEmpty)
        l10n.liveTripPartyGets(
          partyDisplayName(delivery.truckerName, l10n),
          l10n.etbCurrency(birrText(transport)),
        ),
    ].join(' · ');
    Widget step(
      Widget dot,
      String label,
      ClockLabel? when, {
      bool bold = false,
      Color labelColor = AppTheme.ink,
    }) =>
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 5),
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: dot,
              ),
              const SizedBox(width: 10),
              // The label gets the larger share; long clock words (am, om)
              // wrap in theirs instead of pushing out.
              Expanded(
                flex: 5,
                child: Text(
                  label,
                  style: TextStyle(
                    fontSize: 14,
                    fontWeight: bold ? FontWeight.w800 : FontWeight.w400,
                    color: labelColor,
                  ),
                ),
              ),
              if (when != null) ...[
                const SizedBox(width: 8),
                Flexible(
                  flex: 3,
                  child: ClockText(
                    when,
                    textAlign: TextAlign.end,
                    style: TextStyle(
                      fontSize: 14,
                      color: bold ? context.roleColors.primary : AppTheme.slate,
                      fontWeight: bold ? FontWeight.w800 : FontWeight.w400,
                    ),
                  ),
                ),
              ],
            ],
          ),
        );
    Widget filled(Color color, {bool edge = false}) => Container(
          width: 10,
          height: 10,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: color,
            border: edge ? Border.all(color: AppTheme.ink, width: 1.5) : null,
          ),
        );
    final hollow = Container(
      width: 10,
      height: 10,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        border: Border.all(color: AppTheme.fieldBorder, width: 2),
      ),
    );
    return FwCard(
      key: const Key('live_trip_escrow_card'),
      raised: true,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // A wrap, so a long chip label (Afaan Oromoo) moves under the
          // amount on a small phone.
          Wrap(
            spacing: 12,
            runSpacing: 6,
            crossAxisAlignment: WrapCrossAlignment.center,
            alignment: WrapAlignment.spaceBetween,
            children: [
              Text(
                l10n.liveTripEscrowAmount(
                  l10n.etbCurrency(birrText(escrow.amount)),
                ),
                style: const TextStyle(
                  fontSize: 19,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -0.4,
                  color: AppTheme.ink,
                ),
              ),
              DotPill.action(label: l10n.liveTripReleasePending),
            ],
          ),
          const SizedBox(height: 10),
          // The board: say first that the buyer has nothing left to do.
          ClockText(
            clock.deadline(
              windowEnd,
              date: date,
              sentence: l10n.liveTripNothingToDoWindow,
            ),
            key: const Key('live_trip_nothing_to_do'),
            style: const TextStyle(
              fontSize: 14,
              height: 1.4,
              fontWeight: FontWeight.w700,
              color: AppTheme.ink,
            ),
          ),
          const SizedBox(height: 8),
          step(
            filled(context.roleColors.primary),
            l10n.liveTripYouConfirmedDelivery,
            ClockLabel.text(clock.dateTime(confirmedAt, date: date)),
          ),
          step(
            filled(AppTheme.orange, edge: true),
            l10n.liveTripCheckWindowEnds,
            clock.deadline(windowEnd, date: date),
            bold: true,
          ),
          step(
            hollow,
            payouts,
            ClockLabel.text(l10n.liveTripLogThen),
            labelColor: AppTheme.inkSoft,
          ),
          if (report != null) ...[
            const SizedBox(height: 14),
            report!,
          ],
        ],
      ),
    );
  }
}
