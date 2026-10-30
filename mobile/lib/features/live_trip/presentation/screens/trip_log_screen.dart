import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/place.dart';
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
    final parties = [
      for (final party in [
        (id: delivery.truckerId, name: delivery.truckerName),
        (id: delivery.farmerId, name: delivery.farmerName),
      ])
        if (canContactParty(o, currentUserId, party.id)) party,
    ];

    // The board: the back button beside the title, then one card per
    // section, 14 apart.
    return Scaffold(
      backgroundColor: AppTheme.chalkBackground,
      body: SafeArea(
        child: ListView(
          padding: const EdgeInsets.fromLTRB(20, 20, 20, 32),
          children: [
            Row(
              children: [
                const LiveTripBackButton(),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Semantics(
                        header: true,
                        child: Text(
                          l10n.liveTripTripLogTitle,
                          style: const TextStyle(
                            fontSize: 22,
                            fontWeight: FontWeight.w800,
                            letterSpacing: -0.4,
                            color: AppTheme.soilDark,
                          ),
                        ),
                      ),
                      Text(
                        subtitle,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          fontSize: 13,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
            const SizedBox(height: 20),
            LiveTripStats(
              cards: true,
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
            const SizedBox(height: 14),
            WcCard(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const CheckInsTitle(),
                  const SizedBox(height: 10),
                  TripCheckInList(
                    track: track,
                    places: places,
                    now: now,
                    withAhead: true,
                  ),
                ],
              ),
            ),
            const SizedBox(height: 14),
            _PickupProof(delivery: delivery, track: track),
            if (o != null) ...[
              const SizedBox(height: 14),
              _PriceBreakdown(order: o, delivery: delivery),
              const SizedBox(height: 14),
              OrderCommitmentSection(
                order: o,
                userId: currentUserId,
                showAfterPickup: true,
              ),
            ],
            const SizedBox(height: 14),
            WcCard(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  _CardTitle(l10n.liveTripHelpTitle),
                  const SizedBox(height: 10),
                  Text(
                    l10n.liveTripHelpBody(trucker, farmer),
                    style: const TextStyle(
                      fontSize: 13,
                      height: 1.5,
                      color: AppTheme.unselectedNav,
                    ),
                  ),
                  if (parties.isNotEmpty) ...[
                    const SizedBox(height: 12),
                    // The board's two buttons side by side, each with the
                    // party's first name.
                    Row(
                      children: [
                        for (final (i, party) in parties.indexed) ...[
                          if (i > 0) const SizedBox(width: 8),
                          Expanded(
                            child: OutlinedButton.icon(
                              onPressed: () => openOrderChat(
                                context,
                                order: o!,
                                partyId: party.id,
                                partyName: party.name,
                              ),
                              icon: const Icon(Icons.chat_outlined, size: 18),
                              label: Text(
                                l10n.liveTripMessageParty(
                                  firstNameOf(
                                    partyDisplayName(party.name, l10n),
                                  ),
                                ),
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                              ),
                              style: OutlinedButton.styleFrom(
                                foregroundColor: AppTheme.soilDark,
                                side: const BorderSide(
                                  color: Color(0xFFD6D3C8),
                                ),
                                minimumSize: const Size.fromHeight(44),
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 10,
                                ),
                                textStyle: const TextStyle(
                                  fontFamily: AppTheme.fontFamily,
                                  fontFamilyFallback:
                                      AppTheme.ethiopicFontFallback,
                                  fontSize: 14,
                                  fontWeight: FontWeight.w700,
                                ),
                                shape: RoundedRectangleBorder(
                                  borderRadius: BorderRadius.circular(12),
                                ),
                              ),
                            ),
                          ),
                        ],
                      ],
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(height: 14),
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 4),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Padding(
                    padding: EdgeInsets.only(top: 1),
                    child: Icon(
                      Icons.visibility_outlined,
                      size: 16,
                      color: AppTheme.mutedSteel,
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      l10n.liveTripPrivacyNote(farmer),
                      style: const TextStyle(
                        fontSize: 12,
                        height: 1.5,
                        color: AppTheme.mutedSteel,
                      ),
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

/// A card's title (15, extra bold).
class _CardTitle extends StatelessWidget {
  const _CardTitle(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Semantics(
        header: true,
        child: Text(
          text,
          style: const TextStyle(
            fontSize: 15,
            fontWeight: FontWeight.w800,
            color: AppTheme.soilDark,
          ),
        ),
      );
}

/// "Check-ins" with "about every hour" at the end of the line (the trip
/// log and the wide live trip's check-ins card).
class CheckInsTitle extends StatelessWidget {
  const CheckInsTitle({super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Row(
      crossAxisAlignment: CrossAxisAlignment.end,
      children: [
        Expanded(child: _CardTitle(l10n.liveTripCheckInsTitle)),
        const SizedBox(width: 8),
        Text(
          l10n.liveTripAboutEveryHour,
          style: const TextStyle(fontSize: 12, color: AppTheme.mutedSteel),
        ),
      ],
    );
  }
}

/// How a row's marker looks on the timeline (the board): the pickup scan is
/// a green square, hourly check-ins green rings, other stops grey rings,
/// the latest check-in a gold square, the buyer's drop-off a blue disc.
enum _Marker { scan, ring, greyRing, latest, dropoff }

/// The check-ins and pauses, oldest first, then the other stops the truck
/// has done (unnamed) and, [withAhead], the stops still to come and the
/// buyer's own drop-off, one connected timeline (the Buyer · Trip log
/// board). [compact] lists them as one line each (the wide live trip's
/// check-ins card).
class TripCheckInList extends StatelessWidget {
  const TripCheckInList({
    required this.track,
    required this.places,
    this.now,
    this.withAhead = false,
    this.compact = false,
    super.key,
  });

  final DeliveryTrack track;
  final List<Place> places;

  /// The clock, for the minutes to a stop ahead; needed with [withAhead].
  final DateTime? now;
  final bool withAhead;
  final bool compact;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final clock = clockFormatOf(context);
    final languageCode = Localizations.localeOf(context).languageCode;
    final entries = tripLogEntries(track);
    final rows = <_LogRowData>[
      for (final entry in entries)
        _LogRowData(
          time: entry.kind == TripLogKind.paused && entry.until != null
              ? clock.range(entry.at, entry.until!)
              : clock.time(entry.at),
          place: entry.kind == TripLogKind.paused
              ? null
              : placeNameNear(places, entry.fix?.point, languageCode) ??
                  l10n.liveTripStatusOnTheRoad,
          description: tripLogDescription(l10n, entry),
          tag: entry.latest ? l10n.liveTripLogLatest : null,
          sentLate: entry.fix?.sentLate ?? false
              ? (entry.fix?.receivedAt == null
                  ? l10n.liveTripLogSentLate
                  : l10n.liveTripLogSentLateUntil(
                      clock.time(entry.fix!.receivedAt!),
                    ))
              : null,
          sentLateShort:
              entry.fix?.sentLate ?? false ? l10n.liveTripLogSentLate : null,
          latest: entry.latest,
          muted: entry.kind == TripLogKind.otherStopScan ||
              entry.kind == TripLogKind.paused,
          marker: entry.latest
              ? _Marker.latest
              : switch (entry.kind) {
                  TripLogKind.pickup => _Marker.scan,
                  TripLogKind.otherStopScan ||
                  TripLogKind.paused =>
                    _Marker.greyRing,
                  TripLogKind.checkIn => _Marker.ring,
                },
          done: true,
        ),
      for (final _ in track.otherStops.where((s) => s.done))
        _LogRowData(
          time: '',
          place: null,
          description: l10n.liveTripLogOtherStop,
          tag: l10n.liveTripLogPassed,
          muted: true,
          marker: _Marker.greyRing,
          done: true,
        ),
    ];
    if (withAhead) {
      final eta = track.eta;
      final note = track.dropoffNote?.trim() ?? '';
      final ahead = track.otherStops.where((s) => !s.done).toList();
      for (final (i, stop) in ahead.indexed) {
        // A stop before the buyer's says roughly when, as on the board;
        // still unnamed (spec section 7).
        final minutes =
            now == null ? null : minutesToStopAhead(track, stop, now!);
        rows.add(
          _LogRowData(
            time: i == 0 ? l10n.liveTripLogNext : l10n.liveTripLogThen,
            place: minutes == null ? null : l10n.liveTripLogOtherStop,
            description: minutes == null
                ? l10n.liveTripLogOtherStop
                : l10n.liveTripLogStopAhead(minutes),
            muted: true,
            marker: _Marker.greyRing,
          ),
        );
      }
      rows.add(
        _LogRowData(
          time: ahead.isEmpty ? l10n.liveTripLogNext : l10n.liveTripLogThen,
          place: note.isEmpty ? l10n.liveTripYou : note,
          description: eta == null
              ? l10n.liveTripLogYourDropoff
              : '${clock.range(eta.from, eta.to)} · ${l10n.liveTripLogYourDropoff}',
          marker: _Marker.dropoff,
        ),
      );
    }
    if (compact) {
      // Newest first; only rows with a time (the board's one-line list).
      return Column(
        children: [
          for (final row in rows.reversed)
            if (row.time.isNotEmpty) _CompactLogRow(row: row),
        ],
      );
    }
    // The line runs solid along the check-ins done and dashed from the
    // latest one on.
    var passedLatest = false;
    return Column(
      children: [
        for (final (i, row) in rows.indexed)
          Builder(
            builder: (_) {
              final lineAbove = i == 0
                  ? null
                  : passedLatest
                      ? _Line.dashed
                      : _Line.solid;
              if (row.latest) passedLatest = true;
              final lineBelow = i == rows.length - 1
                  ? null
                  : passedLatest
                      ? _Line.dashed
                      : _Line.solid;
              return _LogRow(
                row: row,
                lineAbove: lineAbove,
                lineBelow: lineBelow,
              );
            },
          ),
      ],
    );
  }
}

class _LogRowData {
  const _LogRowData({
    required this.time,
    required this.place,
    required this.description,
    required this.marker,
    this.tag,
    this.sentLate,
    this.sentLateShort,
    this.latest = false,
    this.muted = false,
    this.done = false,
  });

  final String time;
  final String? place;
  final String description;
  final _Marker marker;

  /// Appended to the place ("Bishoftu · latest"), or shown as a pill when
  /// there is no place.
  final String? tag;

  /// The "sent late" pill.
  final String? sentLate;

  /// The pill without the time, for the one-line rows beside the details.
  final String? sentLateShort;
  final bool latest;
  final bool muted;

  /// Whether the truck has been here (the time column in ink, not grey).
  final bool done;
}

enum _Line { solid, dashed }

/// One timeline row: the time column, the marker on the line, the place
/// and what happened.
class _LogRow extends StatelessWidget {
  const _LogRow({
    required this.row,
    required this.lineAbove,
    required this.lineBelow,
  });

  final _LogRowData row;
  final _Line? lineAbove;
  final _Line? lineBelow;

  @override
  Widget build(BuildContext context) {
    final titleColor = row.muted ? AppTheme.unselectedNav : AppTheme.soilDark;
    final title = row.place == null
        ? null
        : row.tag == null
            ? row.place!
            : '${row.place} · ${row.tag}';
    return IntrinsicHeight(
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          SizedBox(
            width: 46,
            child: Padding(
              padding: const EdgeInsets.only(top: 2),
              child: Text(
                row.time,
                style: TextStyle(
                  fontSize: 13,
                  fontWeight: row.latest
                      ? FontWeight.w800
                      : row.done
                          ? FontWeight.w700
                          : FontWeight.w600,
                  color: row.done ? AppTheme.soilDark : AppTheme.mutedSteel,
                ),
              ),
            ),
          ),
          const SizedBox(width: 10),
          SizedBox(
            width: 18,
            child: Column(
              children: [
                SizedBox(
                  height: 2,
                  child: lineAbove == null ? null : _LineBox(lineAbove!),
                ),
                _MarkerBox(row.marker),
                Expanded(
                  child: lineBelow == null
                      ? const SizedBox.shrink()
                      : _LineBox(lineBelow!),
                ),
              ],
            ),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (title != null)
                    Text(
                      title,
                      style: TextStyle(
                        fontSize: 14,
                        fontWeight:
                            row.latest ? FontWeight.w800 : FontWeight.w700,
                        color: titleColor,
                      ),
                    ),
                  Text(
                    row.description,
                    style: const TextStyle(
                      fontSize: 12,
                      height: 1.4,
                      color: AppTheme.mutedSteel,
                    ),
                  ),
                  if (row.sentLate != null ||
                      (title == null && row.tag != null))
                    Padding(
                      padding: const EdgeInsets.only(top: 4),
                      child: Wrap(
                        spacing: 6,
                        runSpacing: 4,
                        children: [
                          if (row.sentLate != null)
                            DotPill(
                              label: row.sentLate!,
                              background: AppTheme.actionCream,
                              foreground: AppTheme.money,
                              height: 22,
                              radius: 6,
                              fontSize: 11,
                            ),
                          if (title == null && row.tag != null)
                            DotPill(
                              label: row.tag!,
                              background: AppTheme.segmentTrack,
                              foreground: AppTheme.unselectedNav,
                              height: 22,
                              radius: 6,
                              fontSize: 11,
                            ),
                        ],
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

/// One line per check-in, newest first (the wide live trip's card).
class _CompactLogRow extends StatelessWidget {
  const _CompactLogRow({required this.row});

  final _LogRowData row;

  @override
  Widget build(BuildContext context) {
    final text = row.place == null
        ? row.description
        : row.tag == null
            ? row.place!
            : '${row.place} · ${row.tag}';
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4.5),
      child: Row(
        children: [
          // At least the width of "11:42", wider for a pause's range
          // ("00:37-00:52"), never wrapped.
          ConstrainedBox(
            constraints: const BoxConstraints(minWidth: 42),
            child: Text(
              row.time,
              softWrap: false,
              style: TextStyle(
                fontSize: 13,
                fontWeight: row.latest ? FontWeight.w800 : FontWeight.w700,
                color: row.done ? AppTheme.soilDark : AppTheme.mutedSteel,
              ),
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              text,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 13,
                fontWeight: row.latest ? FontWeight.w700 : FontWeight.w500,
                color: row.muted ? AppTheme.unselectedNav : AppTheme.soilDark,
              ),
            ),
          ),
          if (row.sentLate != null) ...[
            const SizedBox(width: 8),
            // Up to two thirds of the line beside the town, never past
            // the card.
            Flexible(
              flex: 2,
              child: DotPill(
                label: row.sentLateShort ?? row.sentLate!,
                background: AppTheme.actionCream,
                foreground: AppTheme.money,
                height: 22,
                radius: 6,
                fontSize: 11,
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _LineBox extends StatelessWidget {
  const _LineBox(this.line);

  final _Line line;

  @override
  Widget build(BuildContext context) {
    if (line == _Line.solid) {
      return Center(child: Container(width: 2, color: AppTheme.fieldGreen));
    }
    return const Center(
      child: CustomPaint(
        size: Size(2, double.infinity),
        painter: _DashPainter(),
      ),
    );
  }
}

/// A vertical dashed line (the road still ahead on the board).
class _DashPainter extends CustomPainter {
  const _DashPainter();

  @override
  void paint(Canvas canvas, Size size) {
    final paint = Paint()
      ..color = const Color(0xFFC9C6BB)
      ..strokeWidth = 2;
    for (var y = 0.0; y < size.height; y += 8) {
      canvas.drawLine(
        Offset(size.width / 2, y),
        Offset(size.width / 2, (y + 4).clamp(0, size.height)),
        paint,
      );
    }
  }

  @override
  bool shouldRepaint(covariant CustomPainter oldDelegate) => false;
}

class _MarkerBox extends StatelessWidget {
  const _MarkerBox(this.marker);

  final _Marker marker;

  @override
  Widget build(BuildContext context) {
    return switch (marker) {
      _Marker.scan => Container(
          width: 16,
          height: 16,
          decoration: BoxDecoration(
            color: AppTheme.fieldGreen,
            borderRadius: BorderRadius.circular(4),
          ),
        ),
      _Marker.latest => Container(
          width: 18,
          height: 18,
          decoration: BoxDecoration(
            color: AppTheme.accentSoft,
            borderRadius: BorderRadius.circular(6),
          ),
        ),
      _Marker.dropoff => Container(
          width: 16,
          height: 16,
          alignment: Alignment.center,
          decoration: const BoxDecoration(
            shape: BoxShape.circle,
            color: AppTheme.dropoffBlue,
          ),
          child: Container(
            width: 6,
            height: 6,
            decoration: const BoxDecoration(
              shape: BoxShape.circle,
              color: Colors.white,
            ),
          ),
        ),
      _Marker.ring || _Marker.greyRing => Padding(
          padding: const EdgeInsets.symmetric(vertical: 2),
          child: Container(
            width: 12,
            height: 12,
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: Colors.white,
              border: Border.all(
                color: marker == _Marker.ring
                    ? AppTheme.fieldGreen
                    : const Color(0xFF7A827D),
                width: 3,
              ),
            ),
          ),
        ),
    };
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
    final pickedUpAt = track.pickedUpAt ?? delivery.pickedUpAt;
    if (pickedUpAt == null) return const SizedBox.shrink();
    final gps = delivery.gpsPickup != null || track.pickup != null
        ? l10n.liveTripGpsRecorded
        : '${l10n.deliveryGpsLabel}: ${l10n.deliveryGpsNotCaptured}';
    return WcCard(
      child: Row(
        children: [
          Container(
            width: 56,
            height: 56,
            decoration: BoxDecoration(
              color: AppTheme.fieldGreenTint,
              borderRadius: BorderRadius.circular(14),
            ),
            child: const Icon(
              Icons.verified_outlined,
              color: AppTheme.fieldGreen,
              size: 26,
            ),
          ),
          const SizedBox(width: 14),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                _CardTitle(l10n.liveTripPickupProof),
                const SizedBox(height: 4),
                Text(
                  '${l10n.liveTripQrScannedAt(clock.time(pickedUpAt))} · $gps',
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.unselectedNav,
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

/// The order's price rows in one card with the escrow chip, the total and
/// who is paid what (the board).
class _PriceBreakdown extends StatelessWidget {
  const _PriceBreakdown({required this.order, required this.delivery});

  final Order order;
  final Delivery delivery;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    String etb(double amount) => l10n.etbCurrency(birrText(amount));
    final crop = order.cropCost ?? order.totalPrice;
    final transport = order.transportCost;
    final farmer = partyDisplayName(delivery.farmerName, l10n);
    final trucker = partyDisplayName(delivery.truckerName, l10n);
    // "Crop cost · 2,000 kg × 42 ETB" when the order has a unit price.
    final cropLabel = order.pricePerUnit > 0
        ? '${l10n.cropCost} · ${l10n.quantityKg(birrText(order.quantity))} × '
            '${etb(order.pricePerUnit)}'
        : l10n.cropCost;
    Widget row(String label, double amount) => Padding(
          padding: const EdgeInsets.symmetric(vertical: 5),
          child: Row(
            children: [
              Expanded(
                child: Text(
                  label,
                  style: const TextStyle(
                    fontSize: 14,
                    color: AppTheme.unselectedNav,
                  ),
                ),
              ),
              const SizedBox(width: 12),
              Text(
                etb(amount),
                style: const TextStyle(
                  fontSize: 14,
                  fontWeight: FontWeight.w700,
                  color: AppTheme.soilDark,
                ),
              ),
            ],
          ),
        );
    return WcCard(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Expanded(child: _CardTitle(l10n.priceBreakdown)),
              StatusChip(
                label: l10n.liveTripInEscrow,
                kind: StatusKind.success,
                icon: Icons.shield_outlined,
              ),
            ],
          ),
          const SizedBox(height: 6),
          row(cropLabel, crop),
          if (transport != null && transport > 0)
            row(l10n.transportCost, transport),
          row(l10n.platformCommission, order.commissionAmount ?? 0),
          Container(
            margin: const EdgeInsets.only(top: 5),
            padding: const EdgeInsets.only(top: 10),
            decoration: const BoxDecoration(
              border: Border(top: BorderSide(color: AppTheme.whisperBorder)),
            ),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    l10n.orderTotal,
                    style: const TextStyle(
                      fontSize: 16,
                      fontWeight: FontWeight.w800,
                      color: AppTheme.soilDark,
                    ),
                  ),
                ),
                Text(
                  etb(order.orderTotal ?? order.totalPrice),
                  style: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.soilDark,
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: 10),
          Text(
            transport != null && transport > 0
                ? l10n.liveTripPayoutNote(
                    farmer,
                    etb(crop),
                    trucker,
                    etb(transport),
                  )
                : l10n.liveTripPayoutNoteFarmerOnly(farmer, etb(crop)),
            style: const TextStyle(
              fontSize: 12,
              height: 1.5,
              color: AppTheme.mutedSteel,
            ),
          ),
        ],
      ),
    );
  }
}
