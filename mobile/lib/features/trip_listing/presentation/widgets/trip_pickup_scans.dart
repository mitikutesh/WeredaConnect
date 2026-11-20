import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/handoff_routes.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// The trip owner's pickup scans (T-2) as the Trip detail board's black
/// note (TripDetailM): [note] ("On the trip day, scan each farmer's QR at
/// pickup...") with an Open link for each of the trucker's deliveries on
/// [trip] that still waits for its pickup, through the same scan as trip
/// mode ([openPickupScan]). One waiting pickup puts Open at the end of the
/// note; several get a row each, named after the farmer. Without [note]
/// (the trip is on the road) only the waiting pickups show. Reads the
/// deliveries through the app's delivery repository (Isar on mobile, so it
/// works offline; the API on web); shows nothing while they cannot be read
/// or none waits.
class TripPickupScans extends StatefulWidget {
  const TripPickupScans({
    required this.trip,
    this.note,
    this.onScanPickup,
    super.key,
  });

  final Trip trip;

  /// What to do at pickup, shown before the first pickup; null once the
  /// trip is on the road.
  final String? note;

  /// Defaults to [openPickupScan]; tests replace it.
  final Future<void> Function(BuildContext context, Delivery delivery)?
      onScanPickup;

  @override
  State<TripPickupScans> createState() => _TripPickupScansState();
}

class _TripPickupScansState extends State<TripPickupScans> {
  List<Delivery> _waiting = const [];

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    final auth = context.read<AuthBloc>().state;
    if (auth is! AuthAuthenticated || auth.userId != widget.trip.truckerId) {
      return;
    }
    final DeliveryBloc bloc;
    try {
      bloc = context.read<DeliveryBloc>();
    } on ProviderNotFoundException {
      return;
    }
    try {
      final deliveries = await bloc.repository
          .getDeliveriesByTrip(widget.trip.tripId, userId: auth.userId);
      if (!mounted) return;
      setState(() {
        _waiting = [
          for (final d in deliveries)
            if (d.truckerId == auth.userId &&
                d.status == DeliveryStatus.pending)
              d,
        ];
      });
    } on Object catch (e) {
      debugPrint('[TripPickupScans] deliveries unreadable: $e');
    }
  }

  Future<void> _scan(Delivery delivery) async {
    await (widget.onScanPickup ?? openPickupScan)(context, delivery);
    if (mounted) await _load();
  }

  @override
  Widget build(BuildContext context) {
    final note = widget.note;
    if (_waiting.isEmpty && note == null) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context)!;
    // One pickup waits: the board's row, the note with Open at its end.
    final single = _waiting.length == 1 ? _waiting.first : null;
    return Padding(
      padding: const EdgeInsets.only(top: 8),
      child: Material(
        key: const Key('trip_pickup_note'),
        color: AppTheme.ink,
        borderRadius: BorderRadius.circular(24),
        clipBehavior: Clip.antiAlias,
        child: Padding(
          padding: const EdgeInsetsDirectional.fromSTEB(16, 6, 6, 6),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              if (single != null || note != null)
                Row(
                  children: [
                    const Icon(
                      Icons.qr_code_scanner,
                      size: 20,
                      color: Colors.white,
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Padding(
                        padding: const EdgeInsets.symmetric(vertical: 10),
                        child: Text(
                          note ??
                              l10n.tripScanPickupQrFor(
                                partyDisplayName(single!.farmerName, l10n),
                              ),
                          style: _noteStyle,
                        ),
                      ),
                    ),
                    if (single != null)
                      _OpenLink(
                        key: Key('trip_scan_pickup_${single.deliveryId}'),
                        label: l10n.tripOpenLink,
                        semanticLabel: l10n.tripScanPickupQrFor(
                          partyDisplayName(single.farmerName, l10n),
                        ),
                        onTap: () => _scan(single),
                      )
                    else
                      const SizedBox(width: 10),
                  ],
                ),
              // Several pickups wait: one row per farmer.
              if (_waiting.length > 1)
                for (final delivery in _waiting)
                  _ScanRow(
                    key: Key('trip_scan_pickup_${delivery.deliveryId}'),
                    label: l10n.tripScanPickupQrFor(
                      partyDisplayName(delivery.farmerName, l10n),
                    ),
                    indent: note != null,
                    onTap: () => _scan(delivery),
                  ),
            ],
          ),
        ),
      ),
    );
  }
}

const TextStyle _noteStyle = TextStyle(
  fontSize: 13,
  fontWeight: FontWeight.w700,
  height: 1.35,
  color: Colors.white,
);

/// "Open ›" at the end of the black note: white, bold, 56 tall to tap
/// (T-2: a scan is opened with the truck running).
class _OpenLink extends StatelessWidget {
  const _OpenLink({
    required this.label,
    required this.semanticLabel,
    required this.onTap,
    super.key,
  });

  final String label;
  final String semanticLabel;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: true,
      label: semanticLabel,
      excludeSemantics: true,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(18),
        child: ConstrainedBox(
          constraints: const BoxConstraints(minWidth: 48, minHeight: 56),
          child: Padding(
            padding: const EdgeInsetsDirectional.only(start: 10, end: 4),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(label, style: _noteStyle),
                const SizedBox(width: 2),
                const Icon(Icons.chevron_right, size: 16, color: Colors.white),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// One waiting pickup when there are several: the farmer's scan, with the
/// chevron of a link.
class _ScanRow extends StatelessWidget {
  const _ScanRow({
    required this.label,
    required this.onTap,
    required this.indent,
    super.key,
  });

  final String label;
  final VoidCallback onTap;

  /// Under the note: the label lines up with the note's text. Without the
  /// note the row carries the scan glyph itself.
  final bool indent;

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: true,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(18),
        child: ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 56),
          child: Padding(
            padding: const EdgeInsetsDirectional.only(end: 4),
            child: Row(
              children: [
                if (indent)
                  const SizedBox(width: 32)
                else ...[
                  const Icon(
                    Icons.qr_code_scanner,
                    size: 20,
                    color: Colors.white,
                  ),
                  const SizedBox(width: 12),
                ],
                Expanded(child: Text(label, style: _noteStyle)),
                const Icon(Icons.chevron_right, size: 16, color: Colors.white),
              ],
            ),
          ),
        ),
      ),
    );
  }
}
