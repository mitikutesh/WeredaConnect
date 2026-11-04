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

/// The trip owner's pickup scans (T-2): a 56 dp "Scan pickup QR" for each
/// of the trucker's deliveries on [trip] that still waits for its pickup,
/// through the same scan as trip mode ([openPickupScan]). Reads the
/// deliveries through the app's delivery repository (Isar on mobile, so it
/// works offline; the API on web); shows nothing while they cannot be read
/// or none waits.
class TripPickupScans extends StatefulWidget {
  const TripPickupScans({required this.trip, this.onScanPickup, super.key});

  final Trip trip;

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
    if (_waiting.isEmpty) return const SizedBox.shrink();
    final l10n = AppLocalizations.of(context)!;
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (final (i, delivery) in _waiting.indexed) ...[
            if (i > 0) const SizedBox(height: 8),
            FilledButton.icon(
              key: Key('trip_scan_pickup_${delivery.deliveryId}'),
              onPressed: () => _scan(delivery),
              icon: const Icon(Icons.qr_code_scanner),
              label: Text(
                l10n.tripScanPickupQrFor(
                  partyDisplayName(delivery.farmerName, l10n),
                ),
              ),
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(56),
              ),
            ),
          ],
        ],
      ),
    );
  }
}
