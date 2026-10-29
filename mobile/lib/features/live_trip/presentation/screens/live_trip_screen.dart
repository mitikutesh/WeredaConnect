import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/payment.dart';
import 'package:wereda_connect/core/models/place.dart';
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
import 'package:wereda_connect/features/live_trip/presentation/screens/trip_log_screen.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/track_builder.dart';
import 'package:wereda_connect/features/rating/presentation/widgets/rate_counterparties_section.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';
import 'package:wereda_connect/shared/widgets/wc_card.dart';

/// The buyer's live trip of one delivery (LT-11, specs/features/live-trip.md
/// section 4; the Buyer · Live trip, Arriving and Delivered boards): the map
/// from the farmer's pickup with the last check-in, the arrival window, the
/// four steps, the escrow line, the driver, and the link to the trip log.
/// Before pickup it says the map starts at pickup; within 5 km of the
/// drop-off it shows the checklist and Scan driver's QR; after delivery the
/// trip summary, the escrow timeline, Report a problem while the 48 h window
/// lasts, and Rate Farmer and Rate Trucker.
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
    final l10n = AppLocalizations.of(context)!;
    return Scaffold(
      appBar: AppBar(
        title: Text(l10n.liveTripTitle),
        // The live trip boards: a light page, no colored bar.
        backgroundColor: AppTheme.chalkBackground,
        foregroundColor: AppTheme.soilDark,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
      ),
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
    super.key,
  });

  final Delivery delivery;
  final String currentUserId;
  final List<Place>? places;
  final DateTime Function()? clock;

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
        return RefreshIndicator(
          onRefresh: view.refresh,
          child: ListView(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
            children: _content(context, view),
          ),
        );
      },
    );
  }

  List<Widget> _content(BuildContext context, TrackView view) {
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
        ),
      ];
    }
    final onRoad = track != null && trackFollowsTruck(track);
    final arriving = onRoad && isArriving(track, nearbyNotified: _nearby);
    return [
      ...trackReadNotice(context, view),
      if (onRoad)
        LiveTripMap(
          track: track,
          now: view.now,
          places: _places,
          height: arriving ? 200 : 280,
        )
      else
        _WaitingCard(loading: view.loading),
      const SizedBox(height: 16),
      _Header(
        delivery: _delivery,
        order: _order,
        status: arriving
            ? (label: l10n.liveTripStatusArriving, kind: StatusKind.info)
            : _statusOf(l10n, track, view.now),
      ),
      const SizedBox(height: 12),
      if (arriving)
        _ArrivingPanel(
          delivery: _delivery,
          order: _order,
          track: track,
          now: view.now,
          onScanned: () => _afterScan(view),
        )
      else ...[
        if (onRoad) ...[
          ArrivalWindowBlock(track: track, now: view.now),
          const SizedBox(height: 16),
        ],
        TripProgress(delivery: _delivery),
        const SizedBox(height: 16),
        _infoNote(context, track, view.now),
      ],
      const SizedBox(height: 16),
      WcCard(
        child: PartyContactTile(
          name: _delivery.truckerName,
          roleLabel: AppLocalizations.of(context)!.trucker,
          partyId: _delivery.truckerId,
          viewerId: widget.currentUserId,
          order: _order,
          subtitle: _vehicle,
        ),
      ),
      if (onRoad) ...[
        const SizedBox(height: 12),
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
  /// paused, scans only), else where the money is.
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
          icon: Icons.shield_outlined,
          text: ClockLabel.text(
            total == null
                ? l10n.liveTripEscrowNoteNoAmount
                : l10n.liveTripEscrowNote(l10n.etbCurrency(birrText(total))),
          ),
          kind: StatusKind.success,
        );
    }
  }
}

/// Before pickup (or while the first answer loads): no map yet.
class _WaitingCard extends StatelessWidget {
  const _WaitingCard({required this.loading});

  final bool loading;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    return WcCard(
      color: AppTheme.statusNeutralContainer,
      child: Row(
        children: [
          if (loading)
            const SizedBox(
              width: 28,
              height: 28,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          else
            const Icon(Icons.map_outlined, size: 28, color: AppTheme.textMuted),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.liveTripWaitingForPickup,
                  style: theme.textTheme.titleSmall
                      ?.copyWith(fontWeight: FontWeight.w700),
                ),
                const SizedBox(height: 2),
                Text(
                  l10n.liveTripMapStartsAtPickup,
                  style: theme.textTheme.bodyMedium
                      ?.copyWith(color: AppTheme.textMuted),
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
/// then) and the status chip.
class _Header extends StatelessWidget {
  const _Header({
    required this.delivery,
    required this.order,
    required this.status,
  });

  final Delivery delivery;
  final Order? order;
  final ({String label, StatusKind kind}) status;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final o = order;
    final title = o == null
        ? l10n.liveTripFrom(partyDisplayName(delivery.farmerName, l10n))
        : '${CropType.labelFor(o.cropType, l10n)} · '
            '${l10n.quantityKg(birrText(o.quantity))}';
    return Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                title,
                style: theme.textTheme.titleMedium
                    ?.copyWith(fontWeight: FontWeight.w700),
              ),
              if (o != null)
                Text(
                  l10n.liveTripFrom(
                    partyDisplayName(delivery.farmerName, l10n),
                  ),
                  style: theme.textTheme.bodySmall
                      ?.copyWith(color: AppTheme.textMuted),
                ),
            ],
          ),
        ),
        StatusChip(label: status.label, kind: status.kind),
      ],
    );
  }
}

class _TripLogLink extends StatelessWidget {
  const _TripLogLink({required this.count, required this.onTap});

  final int count;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return WcCard(
      key: const Key('live_trip_log_link'),
      onTap: onTap,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      child: Row(
        children: [
          const Icon(Icons.receipt_long_outlined, color: AppTheme.primary),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              l10n.liveTripTripLogLink,
              style: const TextStyle(fontWeight: FontWeight.w600),
            ),
          ),
          Text(
            l10n.liveTripCheckIns(count),
            style: const TextStyle(color: AppTheme.textMuted, fontSize: 13),
          ),
          const Icon(Icons.chevron_right, color: AppTheme.textMuted),
        ],
      ),
    );
  }
}

/// The truck is within 5 km: how far, the checklist, and Scan driver's QR
/// (the existing buyer delivery scan).
class _ArrivingPanel extends StatefulWidget {
  const _ArrivingPanel({
    required this.delivery,
    required this.order,
    required this.track,
    required this.now,
    required this.onScanned,
  });

  final Delivery delivery;
  final Order? order;
  final DeliveryTrack track;
  final DateTime now;
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
    final theme = Theme.of(context);
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
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(
          heading,
          style: theme.textTheme.titleLarge?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        Text(
          km == null
              ? checkedIn
              : '$checkedIn · ${l10n.liveTripKmFromDropoff(kmText(km))}',
          style:
              theme.textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
        ),
        const SizedBox(height: 16),
        Text(
          l10n.liveTripBeforeYouScan,
          style: theme.textTheme.titleSmall?.copyWith(
            fontWeight: FontWeight.w700,
          ),
        ),
        for (var i = 0; i < items.length; i++)
          CheckboxListTile(
            value: _checked.contains(i),
            onChanged: (value) => setState(
              () => value == true ? _checked.add(i) : _checked.remove(i),
            ),
            title: Text(items[i]),
            dense: true,
            contentPadding: EdgeInsets.zero,
            controlAffinity: ListTileControlAffinity.leading,
          ),
        const SizedBox(height: 8),
        LiveTripNote(
          icon: Icons.shield_outlined,
          text: ClockLabel.text(l10n.liveTripScanConfirmsNote(farmer, trucker)),
          kind: StatusKind.success,
        ),
        const SizedBox(height: 16),
        FilledButton.icon(
          key: const Key('live_trip_scan_qr'),
          onPressed: _scan,
          icon: const Icon(Icons.qr_code_scanner),
          label: Text(l10n.liveTripScanDriversQr),
          style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(52)),
        ),
      ],
    );
  }
}

/// After delivery (sharing ended): the trip in numbers, the drop-off proof,
/// the escrow timeline, Report a problem while the window lasts, and the
/// ratings.
class _DeliveredSummary extends StatelessWidget {
  const _DeliveredSummary({
    required this.delivery,
    required this.order,
    required this.escrow,
    required this.track,
    required this.places,
    required this.userId,
    required this.now,
  });

  final Delivery delivery;
  final Order? order;
  final Payment? escrow;
  final DeliveryTrack? track;
  final List<Place> places;
  final String userId;
  final DateTime now;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final theme = Theme.of(context);
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
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Align(
          alignment: Alignment.centerLeft,
          child: StatusChip(
            label: l10n.liveTripSharingEnded,
            kind: StatusKind.success,
            icon: Icons.location_off_outlined,
          ),
        ),
        const SizedBox(height: 12),
        ClockText(
          delivered && confirmedAt != null
              ? clock.deadline(confirmedAt, sentence: l10n.liveTripDeliveredAt)
              : ClockLabel.text(
                  delivery.status == DeliveryStatus.disputed
                      ? l10n.deliveryStatusDisputed
                      : delivery.status == DeliveryStatus.cancelled
                          ? l10n.deliveryStatusCancelled
                          : l10n.liveTripEnded,
                ),
          style: theme.textTheme.headlineSmall
              ?.copyWith(fontWeight: FontWeight.w700),
        ),
        Text(
          o == null
              ? l10n.liveTripFrom(partyDisplayName(delivery.farmerName, l10n))
              : '${CropType.labelFor(o.cropType, l10n)} · '
                  '${l10n.quantityKg(birrText(o.quantity))} · '
                  '${l10n.liveTripFrom(partyDisplayName(delivery.farmerName, l10n))}',
          style:
              theme.textTheme.bodyMedium?.copyWith(color: AppTheme.textMuted),
        ),
        const SizedBox(height: 16),
        WcCard(
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
                const SizedBox(height: 16),
                LiveTripStats(
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
              ],
            ],
          ),
        ),
        if (confirmedAt != null) ...[
          const SizedBox(height: 12),
          Text(
            '${l10n.liveTripDropoffProof}: '
            '${l10n.liveTripQrScannedAt(clock.time(confirmedAt))} · '
            '${delivery.gpsDropoff != null ? l10n.liveTripGpsRecorded : '${l10n.deliveryGpsLabel}: ${l10n.deliveryGpsNotCaptured}'}',
            style:
                theme.textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
          ),
        ],
        if (escrow != null) ...[
          const SizedBox(height: 20),
          EscrowTimeline(payment: escrow!),
        ],
        if (canReport) ...[
          const SizedBox(height: 12),
          OutlinedButton.icon(
            key: const Key('live_trip_report_problem'),
            onPressed: () => Navigator.of(context).push(
              MaterialPageRoute<void>(
                builder: (_) =>
                    DisputeScreen(delivery: delivery, userId: userId),
              ),
            ),
            icon: const Icon(Icons.report_problem_outlined),
            label: ClockText(
              clock.deadline(
                windowEnd,
                date: DateFormat.MMMd(),
                sentence: l10n.liveTripReportProblemUntil,
              ),
            ),
            style: OutlinedButton.styleFrom(
              foregroundColor: AppTheme.statusError,
              side: const BorderSide(color: AppTheme.statusError),
              minimumSize: const Size.fromHeight(48),
            ),
          ),
        ],
        if (delivered && (delivery.orderId?.isNotEmpty ?? false)) ...[
          const SizedBox(height: 20),
          Text(
            l10n.liveTripHowDidItGo,
            style: theme.textTheme.titleMedium
                ?.copyWith(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 12),
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
