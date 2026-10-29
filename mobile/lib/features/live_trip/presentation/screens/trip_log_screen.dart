import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/utils/ethiopian_clock.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/live_trip/domain/live_trip_rules.dart';
import 'package:wereda_connect/features/live_trip/domain/models/delivery_track.dart';
import 'package:wereda_connect/features/live_trip/presentation/live_trip_text.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/live_trip_parts.dart';
import 'package:wereda_connect/features/order/presentation/widgets/order_commitment_section.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/clock_text.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';
import 'package:wereda_connect/shared/widgets/wc_card.dart';

/// The buyer's trip log (LT-11, the Buyer · Trip log board): the check-ins of
/// the buyer's window with their times on the viewer's clock and the town
/// each was nearest to, "sent late" tags, the pauses, the other stops on the
/// truck (never named), what is still ahead, the pickup proof, the price
/// breakdown, who has agreed, what to do when something goes wrong, and who
/// sees the trail.
///
/// It shows the track the live trip screen last read ([track], [now]), so
/// opening it costs no extra read.
class TripLogScreen extends StatelessWidget {
  const TripLogScreen({
    required this.delivery,
    required this.currentUserId,
    required this.track,
    required this.now,
    this.order,
    this.places = const [],
    super.key,
  });

  final Delivery delivery;
  final String currentUserId;
  final DeliveryTrack track;
  final DateTime now;
  final Order? order;
  final List<Place> places;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final o = order;
    final farmer = partyDisplayName(delivery.farmerName, l10n);
    final trucker = partyDisplayName(delivery.truckerName, l10n);
    final pickedUpAt = track.pickedUpAt ?? delivery.pickedUpAt;
    final remaining = track.remainingKm;
    final subtitle = o == null
        ? l10n.liveTripFrom(farmer)
        : '${CropType.labelFor(o.cropType, l10n)} · '
            '${l10n.quantityKg(birrText(o.quantity))} · '
            '${l10n.liveTripFrom(farmer)}';

    return Scaffold(
      appBar: AppBar(title: Text(l10n.liveTripTripLogTitle)),
      body: SafeArea(
        child: ListView(
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
          children: [
            Text(
              subtitle,
              style: theme.textTheme.bodyMedium
                  ?.copyWith(color: AppTheme.textMuted),
            ),
            const SizedBox(height: 16),
            WcCard(
              child: LiveTripStats(
                stats: [
                  (
                    value: l10n.liveTripKm(kmText(trailDistanceKm(track))),
                    label: l10n.liveTripStatDone,
                  ),
                  if (remaining != null)
                    (
                      value: l10n.liveTripKm(kmText(remaining)),
                      label: l10n.liveTripStatToGo,
                    ),
                  if (pickedUpAt != null)
                    (
                      value: liveTripDuration(l10n, now.difference(pickedUpAt)),
                      label: l10n.liveTripStatOnTheRoad,
                    ),
                ],
              ),
            ),
            const SizedBox(height: 20),
            _SectionTitle(l10n.liveTripCheckInCadence),
            const SizedBox(height: 8),
            _CheckInList(track: track, places: places),
            const SizedBox(height: 20),
            _SectionTitle(l10n.liveTripLogAhead),
            const SizedBox(height: 8),
            _AheadList(track: track, now: now),
            const SizedBox(height: 20),
            _PickupProof(delivery: delivery, track: track),
            if (o != null) ...[
              const SizedBox(height: 20),
              _PriceBreakdown(order: o, delivery: delivery),
              const SizedBox(height: 20),
              OrderCommitmentSection(
                order: o,
                userId: currentUserId,
                showAfterPickup: true,
              ),
            ],
            const SizedBox(height: 4),
            _SectionTitle(l10n.liveTripHelpTitle),
            const SizedBox(height: 8),
            Text(
              l10n.liveTripHelpBody(trucker, farmer),
              style: theme.textTheme.bodyMedium,
            ),
            const SizedBox(height: 12),
            for (final party in [
              (id: delivery.truckerId, name: delivery.truckerName),
              (id: delivery.farmerId, name: delivery.farmerName),
            ])
              if (canContactParty(o, currentUserId, party.id))
                Padding(
                  padding: const EdgeInsets.only(bottom: 8),
                  child: OutlinedButton.icon(
                    onPressed: () => openOrderChat(
                      context,
                      order: o!,
                      partyId: party.id,
                      partyName: party.name,
                    ),
                    icon: const Icon(Icons.chat_outlined),
                    label: Text(
                      l10n.liveTripMessageParty(
                        partyDisplayName(party.name, l10n),
                      ),
                    ),
                    style: OutlinedButton.styleFrom(
                      minimumSize: const Size.fromHeight(48),
                    ),
                  ),
                ),
            const SizedBox(height: 12),
            LiveTripNote(
              icon: Icons.lock_outline,
              text: ClockLabel.text(l10n.liveTripPrivacyNote(farmer)),
              kind: StatusKind.neutral,
            ),
          ],
        ),
      ),
    );
  }
}

class _SectionTitle extends StatelessWidget {
  const _SectionTitle(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Text(
        text,
        style: Theme.of(context)
            .textTheme
            .titleMedium
            ?.copyWith(fontWeight: FontWeight.w700),
      );
}

/// The check-ins and pauses, oldest first, then the other stops the truck
/// has done (unnamed).
class _CheckInList extends StatelessWidget {
  const _CheckInList({required this.track, required this.places});

  final DeliveryTrack track;
  final List<Place> places;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final languageCode = Localizations.localeOf(context).languageCode;
    final entries = tripLogEntries(track);
    return Column(
      children: [
        for (final entry in entries)
          _LogRow(
            time: entry.kind == TripLogKind.paused && entry.until != null
                ? clock.range(entry.at, entry.until!)
                : clock.time(entry.at),
            place: entry.kind == TripLogKind.paused
                ? null
                : placeNameNear(places, entry.fix?.point, languageCode) ??
                    l10n.liveTripStatusOnTheRoad,
            description: tripLogDescription(l10n, entry),
            tags: [
              if (entry.latest) l10n.liveTripLogLatest,
              if (entry.fix?.sentLate ?? false) l10n.liveTripLogSentLate,
            ],
            emphasis: entry.latest,
            muted: entry.kind == TripLogKind.otherStopScan ||
                entry.kind == TripLogKind.paused,
          ),
        for (final _ in track.otherStops.where((s) => s.done))
          _LogRow(
            time: '',
            place: null,
            description: l10n.liveTripLogOtherStop,
            tags: [l10n.liveTripLogPassed],
            muted: true,
          ),
      ],
    );
  }
}

/// What is still to come: the other stops not done yet (unnamed), then the
/// buyer's own drop-off with the arrival window.
class _AheadList extends StatelessWidget {
  const _AheadList({required this.track, required this.now});

  final DeliveryTrack track;
  final DateTime now;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final eta = track.eta;
    final note = track.dropoffNote?.trim() ?? '';
    return Column(
      children: [
        for (final stop in track.otherStops.where((s) => !s.done))
          // A stop before the buyer's says roughly when, as on the board;
          // still unnamed (spec section 7).
          if (minutesToStopAhead(track, stop, now) case final minutes?)
            _LogRow(
              time: '',
              place: l10n.liveTripLogOtherStop,
              description: l10n.liveTripLogStopAhead(minutes),
              tags: const [],
              muted: true,
            )
          else
            _LogRow(
              time: '',
              place: null,
              description: l10n.liveTripLogOtherStop,
              tags: const [],
              muted: true,
            ),
        _LogRow(
          time: eta == null ? '' : clock.range(eta.from, eta.to),
          place: note.isEmpty ? l10n.liveTripYou : note,
          description: l10n.liveTripLogYourDropoff,
          tags: const [],
          emphasis: true,
        ),
      ],
    );
  }
}

class _LogRow extends StatelessWidget {
  const _LogRow({
    required this.time,
    required this.place,
    required this.description,
    required this.tags,
    this.emphasis = false,
    this.muted = false,
  });

  final String time;
  final String? place;
  final String description;
  final List<String> tags;
  final bool emphasis;
  final bool muted;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final color = muted ? AppTheme.textMuted : AppTheme.textDark;
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 96,
            child: Text(
              time,
              style: theme.textTheme.bodyMedium?.copyWith(
                fontWeight: emphasis ? FontWeight.w700 : FontWeight.w500,
                color: color,
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.only(top: 4, right: 10),
            child: Icon(
              muted ? Icons.radio_button_unchecked : Icons.circle,
              size: 10,
              color: muted ? AppTheme.textMuted : AppTheme.primary,
            ),
          ),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (place != null)
                  Text(
                    place!,
                    style: theme.textTheme.bodyLarge?.copyWith(
                      fontWeight: FontWeight.w600,
                      color: color,
                    ),
                  ),
                Text(
                  description,
                  style: theme.textTheme.bodySmall
                      ?.copyWith(color: AppTheme.textMuted),
                ),
                if (tags.isNotEmpty) ...[
                  const SizedBox(height: 4),
                  Wrap(
                    spacing: 6,
                    runSpacing: 4,
                    children: [
                      for (final tag in tags)
                        StatusChip(
                          label: tag,
                          kind: tag == tags.first && emphasis
                              ? StatusKind.transit
                              : StatusKind.warning,
                        ),
                    ],
                  ),
                ],
              ],
            ),
          ),
        ],
      ),
    );
  }
}

/// When and how the pickup was proved: the QR scan time and whether the
/// phone recorded where.
class _PickupProof extends StatelessWidget {
  const _PickupProof({required this.delivery, required this.track});

  final Delivery delivery;
  final DeliveryTrack track;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final theme = Theme.of(context);
    final pickedUpAt = track.pickedUpAt ?? delivery.pickedUpAt;
    if (pickedUpAt == null) return const SizedBox.shrink();
    final gps = delivery.gpsPickup != null || track.pickup != null
        ? l10n.liveTripGpsRecorded
        : '${l10n.deliveryGpsLabel}: ${l10n.deliveryGpsNotCaptured}';
    return WcCard(
      child: Row(
        children: [
          const Icon(Icons.verified_outlined, color: AppTheme.primary),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  l10n.liveTripPickupProof,
                  style: theme.textTheme.titleSmall
                      ?.copyWith(fontWeight: FontWeight.w700),
                ),
                Text(
                  '${l10n.liveTripQrScannedAt(clock.time(pickedUpAt))} · $gps',
                  style: theme.textTheme.bodySmall
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

/// The order's price rows, the escrow chip, and who is paid what.
class _PriceBreakdown extends StatelessWidget {
  const _PriceBreakdown({required this.order, required this.delivery});

  final Order order;
  final Delivery delivery;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    String etb(double amount) => l10n.etbCurrency(birrText(amount));
    final crop = order.cropCost ?? order.totalPrice;
    final transport = order.transportCost;
    final farmer = partyDisplayName(delivery.farmerName, l10n);
    final trucker = partyDisplayName(delivery.truckerName, l10n);
    Widget row(String label, double amount, {bool bold = false}) => Padding(
          padding: const EdgeInsets.symmetric(vertical: 4),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  label,
                  style: bold
                      ? theme.textTheme.titleSmall
                          ?.copyWith(fontWeight: FontWeight.w700)
                      : theme.textTheme.bodyMedium,
                ),
              ),
              Text(
                etb(amount),
                style: bold
                    ? theme.textTheme.titleSmall
                        ?.copyWith(fontWeight: FontWeight.w700)
                    : theme.textTheme.bodyMedium,
              ),
            ],
          ),
        );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(child: _SectionTitle(l10n.priceBreakdown)),
            StatusChip(
              label: l10n.liveTripInEscrow,
              kind: StatusKind.success,
              icon: Icons.shield_outlined,
            ),
          ],
        ),
        const SizedBox(height: 8),
        WcCard(
          child: Column(
            children: [
              row(l10n.cropCost, crop),
              if (transport != null && transport > 0)
                row(l10n.transportCost, transport),
              row(l10n.platformCommission, order.commissionAmount ?? 0),
              const Divider(height: 16),
              row(
                l10n.orderTotal,
                order.orderTotal ?? order.totalPrice,
                bold: true,
              ),
            ],
          ),
        ),
        const SizedBox(height: 8),
        Text(
          transport != null && transport > 0
              ? l10n.liveTripPayoutNote(
                  farmer,
                  etb(crop),
                  trucker,
                  etb(transport),
                )
              : l10n.liveTripPayoutNoteFarmerOnly(farmer, etb(crop)),
          style: theme.textTheme.bodySmall?.copyWith(color: AppTheme.textMuted),
        ),
      ],
    );
  }
}
