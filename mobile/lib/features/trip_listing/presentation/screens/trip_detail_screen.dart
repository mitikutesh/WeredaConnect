import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_bookings_section.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/contact_locked_note.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// One trip, as the Trucker · Trip and bookings board draws it: the dark
/// header names the trip (day, price, route) and shows how much of the
/// truck is booked; the owner then sees the booked orders with the one to
/// answer first, the pickup reminder, and Start trip or Cancel trip pinned
/// at the foot. A buyer browsing the trip sees the trucker's name and when
/// contact opens instead (ESC-48).
class TripDetailScreen extends StatelessWidget {
  final Trip trip;

  const TripDetailScreen({required this.trip, super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final authState = context.read<AuthBloc>().state;
    final isOwner =
        authState is AuthAuthenticated && authState.userId == trip.truckerId;

    return BlocListener<TripListingBloc, TripListingState>(
      listener: (context, state) {
        if (state is TripsLoaded) {
          // Trip was cancelled, go back to list
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.tripCancelledSuccess)),
          );
          Navigator.of(context).pop();
        } else if (state is TripStarted) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.tripStartedSuccess)),
          );
          Navigator.of(context).pop();
        } else if (state is TripDelivered) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.tripDeliveredSuccess)),
          );
          Navigator.of(context).pop();
        } else if (state is TripListingError) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(localizeErrorCode(state.code, l10n)),
              backgroundColor: AppTheme.statusError,
            ),
          );
        }
      },
      child: Scaffold(
        body: Column(
          children: [
            _RouteHeader(trip: trip),
            const OfflineBanner(),
            Expanded(
              child: SingleChildScrollView(
                child: FwContentWidth(
                  child: Padding(
                    padding: const EdgeInsets.fromLTRB(20, 20, 20, 24),
                    child: isOwner
                        ? _OwnerBody(trip: trip)
                        : _VisitorBody(trip: trip),
                  ),
                ),
              ),
            ),
          ],
        ),
        bottomNavigationBar: isOwner ? _OwnerActions(trip: trip) : null,
      ),
    );
  }
}

/// The owner's page: the booked orders and, before pickup, what to do with
/// each farmer's QR on the day.
class _OwnerBody extends StatelessWidget {
  final Trip trip;

  const _OwnerBody({required this.trip});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final beforePickup =
        trip.status == TripStatus.open || trip.status == TripStatus.matched;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        TripBookingsSection(trip: trip),
        if (beforePickup && tripHasBookings(trip))
          FwNoteBox(
            icon: Icons.qr_code_scanner,
            child: Text(l10n.tripScanAtPickupNote),
          ),
      ],
    );
  }
}

/// A buyer's view of someone else's trip: who drives, and that the phone
/// opens on the order once the farmer accepted it (ESC-48), never before.
class _VisitorBody extends StatelessWidget {
  final Trip trip;

  const _VisitorBody({required this.trip});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        FwCard(
          padding: const EdgeInsets.all(14),
          child: Row(
            children: [
              Container(
                width: 44,
                height: 44,
                decoration: const BoxDecoration(
                  color: AppTheme.actionCream,
                  shape: BoxShape.circle,
                ),
                child: const Icon(
                  Icons.local_shipping_outlined,
                  color: AppTheme.money,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      trip.truckerName,
                      style: const TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w700,
                        color: AppTheme.soilDark,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      l10n.trucker,
                      style: const TextStyle(
                        fontSize: 12,
                        color: AppTheme.mutedSteel,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 12),
        ContactLockedNote(text: l10n.contactAfterEscrowNote),
      ],
    );
  }
}

/// The owner's actions pinned at the foot: Start trip once orders are
/// booked, Mark delivered on the road, Cancel trip before pickup with what
/// a cancel does to the booked orders.
class _OwnerActions extends StatelessWidget {
  final Trip trip;

  const _OwnerActions({required this.trip});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    // ESC-15: a trip stays open while it has room, so one with bookings may
    // start too (matched = full).
    final canStart = trip.status == TripStatus.matched ||
        (trip.status == TripStatus.open && tripHasBookings(trip));
    final canDeliver = trip.status == TripStatus.inTransit;
    final canCancel =
        trip.status == TripStatus.open || trip.status == TripStatus.matched;
    if (!canStart && !canDeliver && !canCancel) {
      return const SizedBox.shrink();
    }
    return BlocBuilder<TripListingBloc, TripListingState>(
      builder: (context, state) {
        final isLoading = state is TripListingLoading;
        return FwBottomActions(
          children: [
            if (canStart)
              _PrimaryAction(
                label: l10n.startTrip,
                icon: Icons.play_arrow,
                busy: isLoading,
                onPressed: () => _confirm(
                  context,
                  title: l10n.startTrip,
                  body: l10n.startTripConfirmation,
                  confirmLabel: l10n.startTrip,
                  confirmColor: AppTheme.statusSuccess,
                  eventFactory: (truckerId) =>
                      StartTrip(tripId: trip.tripId, truckerId: truckerId),
                ),
              ),
            if (canDeliver)
              _PrimaryAction(
                label: l10n.markDelivered,
                icon: Icons.check_circle_outline,
                busy: isLoading,
                onPressed: () => _confirm(
                  context,
                  title: l10n.markDelivered,
                  body: l10n.markDeliveredConfirmation,
                  confirmLabel: l10n.markDelivered,
                  confirmColor: AppTheme.statusSuccess,
                  eventFactory: (truckerId) => MarkTripDelivered(
                    tripId: trip.tripId,
                    truckerId: truckerId,
                  ),
                ),
              ),
            if (canCancel) ...[
              if (canStart) const SizedBox(height: 8),
              OutlinedButton(
                onPressed: isLoading
                    ? null
                    : () => _confirm(
                          context,
                          title: l10n.cancelTrip,
                          body: l10n.cancelTripConfirmation,
                          confirmLabel: l10n.cancelTrip,
                          confirmColor: AppTheme.statusError,
                          eventFactory: (truckerId) => CancelTrip(
                            tripId: trip.tripId,
                            truckerId: truckerId,
                          ),
                        ),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(48),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(14),
                  ),
                  textStyle: const TextStyle(
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                  ),
                ),
                child: isLoading && !canStart && !canDeliver
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : Text(l10n.cancelTrip),
              ),
              if (tripHasBookings(trip))
                FwActionNote(l10n.tripCancelBookedNote),
            ],
          ],
        );
      },
    );
  }

  void _confirm(
    BuildContext context, {
    required String title,
    required String body,
    required String confirmLabel,
    required Color confirmColor,
    required TripListingEvent Function(String truckerId) eventFactory,
  }) {
    final l10n = AppLocalizations.of(context)!;
    showDialog<void>(
      context: context,
      builder: (dialogContext) {
        return AlertDialog(
          title: Text(title),
          content: Text(body),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(dialogContext).pop(),
              child: Text(l10n.cancel),
            ),
            TextButton(
              onPressed: () {
                Navigator.of(dialogContext).pop();
                final authState = context.read<AuthBloc>().state;
                if (authState is AuthAuthenticated) {
                  context
                      .read<TripListingBloc>()
                      .add(eventFactory(authState.userId));
                }
              },
              style: TextButton.styleFrom(foregroundColor: confirmColor),
              child: Text(confirmLabel),
            ),
          ],
        );
      },
    );
  }
}

/// The board's primary button (56 tall, radius 16) with a spinner while the
/// trip's change is on its way.
class _PrimaryAction extends StatelessWidget {
  final String label;
  final IconData icon;
  final bool busy;
  final VoidCallback onPressed;

  const _PrimaryAction({
    required this.label,
    required this.icon,
    required this.busy,
    required this.onPressed,
  });

  @override
  Widget build(BuildContext context) {
    return FilledButton.icon(
      onPressed: busy ? null : onPressed,
      style: FilledButton.styleFrom(minimumSize: const Size.fromHeight(56)),
      icon: busy
          ? const SizedBox(
              height: 20,
              width: 20,
              child: CircularProgressIndicator(
                strokeWidth: 2,
                valueColor: AlwaysStoppedAnimation<Color>(Colors.white),
              ),
            )
          : Icon(icon),
      label: Text(label),
    );
  }
}

/// The trip's dark header (Fieldway: TripDetail.dc.html): Back beside the
/// day and price and the route, the status when the trip is no longer open,
/// and how much of the truck is booked with what a booking keeps.
class _RouteHeader extends StatelessWidget {
  final Trip trip;

  const _RouteHeader({required this.trip});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final day = DateFormat.MMMEd().format(dayInEthiopia(trip.departureDate));
    final price = l10n.tripPricePerQuintalValue(
      trip.pricePerQuintal.toStringAsFixed(0),
    );
    final topInset = MediaQuery.paddingOf(context).top;
    return Container(
      width: double.infinity,
      padding: EdgeInsets.fromLTRB(20, topInset + 12, 20, 22),
      color: AppTheme.soilDark,
      child: FwContentWidth(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const FwBackButton(dark: true),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '$day · $price',
                        style: const TextStyle(
                          fontSize: 13,
                          color: AppTheme.soilMuted,
                        ),
                      ),
                      Semantics(
                        header: true,
                        child: Text(
                          '${trip.fromWoreda} → ${trip.toWoreda}',
                          style: const TextStyle(
                            fontSize: 24,
                            fontWeight: FontWeight.w800,
                            letterSpacing: -0.6,
                            height: 1.15,
                            color: Colors.white,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
                if (trip.status != TripStatus.open) ...[
                  const SizedBox(width: 12),
                  StatusChip(
                    label: tripStatusLabel(trip.status, l10n),
                    kind: tripStatusKind(trip.status),
                  ),
                ],
              ],
            ),
            if (trip.capacity > 0) ...[
              const SizedBox(height: 16),
              _BookedBox(trip: trip),
            ],
          ],
        ),
      ),
    );
  }
}

/// How much of the truck is booked: the quintals out of the capacity, the
/// load bar, and the terms a booking fixes once there is one.
class _BookedBox extends StatelessWidget {
  final Trip trip;

  const _BookedBox({required this.trip});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final capacity = trip.capacity;
    final left = tripRoomLeft(trip);
    final booked = (capacity - left).clamp(0, capacity).toDouble();
    final space = '${l10n.detailBookedOf(
      formatQuintals(booked),
      formatQuintals(capacity),
    )} · ${l10n.detailLeft(formatQuintals(left))}';
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        color: AppTheme.soilCard,
        borderRadius: BorderRadius.circular(18),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Expanded(
                child: Text(
                  l10n.detailBooked,
                  style: const TextStyle(
                    fontSize: 13,
                    color: AppTheme.soilMuted,
                  ),
                ),
              ),
              Text(
                space,
                style: const TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w700,
                  color: Colors.white,
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          ClipRRect(
            borderRadius: BorderRadius.circular(999),
            child: LinearProgressIndicator(
              value: booked / capacity,
              minHeight: 8,
              backgroundColor: AppTheme.soilTrack,
              valueColor: const AlwaysStoppedAnimation<Color>(
                AppTheme.accentSoft,
              ),
            ),
          ),
          if (tripHasBookings(trip)) ...[
            const SizedBox(height: 10),
            Text(
              l10n.tripBookedTermsNote,
              style: const TextStyle(fontSize: 12, color: AppTheme.soilMuted),
            ),
          ],
        ],
      ),
    );
  }
}
