import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/trip_departure.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/sync/listing_report_rules.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/listing_report/presentation/report_listing_sheet.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_bookings_section.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_buyer_actions.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_pickup_scans.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/contact_locked_note.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

/// One trip, as the Trucker · Trip and bookings board draws it (TripDetailM,
/// white ground): the header names the trip (day, price per 100 kg, route),
/// the raised card under it shows how much of the truck is booked,
/// kilograms first; the owner then sees the booked orders with the one to
/// answer first, the black pickup note with its Open link, and Start trip
/// or Cancel trip pinned at the foot. A buyer browsing the trip sees the trucker's name and when
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
    // B-2: a buyer may use the trip; a farmer or another trucker only reads.
    final isBuyer =
        authState is AuthAuthenticated && authState.role == UserRole.buyer;

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
        body: SafeArea(
          bottom: false,
          child: Column(
            children: [
              const OfflineBanner(),
              Expanded(
                child: SingleChildScrollView(
                  child: FwContentWidth(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        _RouteHeader(trip: trip),
                        if (trip.capacity > 0)
                          Padding(
                            padding: const EdgeInsets.fromLTRB(20, 20, 20, 0),
                            child: _BookedBox(trip: trip),
                          ),
                        Padding(
                          padding: const EdgeInsets.fromLTRB(20, 20, 20, 24),
                          child: isOwner
                              ? _OwnerBody(trip: trip)
                              : _VisitorBody(
                                  trip: trip,
                                  // FUP-67: only a buyer reports: a farmer
                                  // or another trucker sees only their own
                                  // listings.
                                  reporterId: isBuyer ? authState.userId : null,
                                ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
        bottomNavigationBar: isOwner
            ? _OwnerActions(trip: trip)
            : (isBuyer ? TripBuyerActions(trip: trip) : null),
      ),
    );
  }
}

/// The owner's page: the booked orders, then the black pickup note: what
/// to do with each farmer's QR on the day (before pickup) and an Open link
/// to the scan of each pickup still waiting (T-2).
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
        TripPickupScans(
          trip: trip,
          note: beforePickup && tripHasBookings(trip)
              ? l10n.tripScanAtPickupNote
              : null,
        ),
      ],
    );
  }
}

/// A buyer's view of someone else's trip: who drives, and that the phone
/// opens on the order once the farmer accepted it (ESC-48), never before.
class _VisitorBody extends StatelessWidget {
  final Trip trip;

  /// The signed-in buyer, who may report the trip (FUP-67); null for anyone
  /// else.
  final String? reporterId;

  const _VisitorBody({required this.trip, this.reporterId});

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
                decoration: BoxDecoration(
                  color: context.roleColors.tint,
                  borderRadius: BorderRadius.circular(14),
                ),
                child: Icon(
                  Icons.local_shipping_outlined,
                  color: context.roleColors.primary,
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
                        fontWeight: FontWeight.w800,
                        color: AppTheme.ink,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      l10n.trucker,
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.slate,
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
        if (reporterId case final reporter?) ...[
          const SizedBox(height: 8),
          ReportListingButton(
            listingType: ReportedListingType.trip,
            listingId: trip.tripId,
            reporterId: reporter,
          ),
        ],
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
            const SizedBox(height: 4),
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
                  confirmColor: null,
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
                  confirmColor: null,
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
                // The board's outlined pill, as tall as the primary.
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(54),
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
    required Color? confirmColor,
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
              // The theme's link colour unless the action destroys.
              style: confirmColor == null
                  ? null
                  : TextButton.styleFrom(foregroundColor: confirmColor),
              child: Text(confirmLabel),
            ),
          ],
        );
      },
    );
  }
}

/// Start trip and Mark delivered: black with white ink, the second
/// strongest button. The page's one orange action is Accept transport on a
/// booking that waits for the trucker, so the foot never competes with it.
/// Shows a spinner while the trip's change is on its way.
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
      style: FilledButton.styleFrom(
        backgroundColor: AppTheme.ink,
        foregroundColor: Colors.white,
      ),
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

/// The trip's header on the white page (TripDetailM): Back beside the day
/// and the price per 100 kg over the route, and the status when the trip is
/// no longer open.
class _RouteHeader extends StatelessWidget {
  final Trip trip;

  const _RouteHeader({required this.trip});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final day = boardDayInEthiopia(trip.departureDate);
    final price = l10n.tripPricePer100KgValue(
      formatEtbAmount(trip.pricePerQuintal),
    );
    // L-7: as the My Trips card, an open or matched trip whose day is over.
    final datePassed =
        (trip.status == TripStatus.open || trip.status == TripStatus.matched) &&
            tripHasLeft(trip, DateTime.now());
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 24, 20, 0),
      child: Row(
        children: [
          const FwBackButton(),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // "Thu 30 Oct · 150 ETB per 100 kg (quintal)".
                Text(
                  '$day · $price ${l10n.quintalInParens}',
                  style: const TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w500,
                    color: AppTheme.slate,
                  ),
                ),
                const SizedBox(height: 2),
                Semantics(
                  header: true,
                  child: Text(
                    '${trip.fromWoreda} → ${trip.toWoreda}',
                    style: const TextStyle(
                      fontSize: 28,
                      fontWeight: FontWeight.w800,
                      letterSpacing: -1,
                      height: 1.05,
                      color: AppTheme.ink,
                    ),
                  ),
                ),
              ],
            ),
          ),
          if (trip.status != TripStatus.open || datePassed) ...[
            const SizedBox(width: 12),
            StatusChip(
              label: datePassed
                  ? l10n.tripStatusDatePassed
                  : tripStatusLabel(trip.status, l10n),
              kind:
                  datePassed ? StatusKind.warning : tripStatusKind(trip.status),
            ),
          ],
        ],
      ),
    );
  }
}

/// Whole kilograms of a load kept in quintals, with grouped thousands.
String _kgOf(double quintals) => wholeNumber((quintals * kgPerQuintal).round());

/// How much of the truck is booked, kilograms first (TripDetailM): "4,700
/// of 5,000 kg · 300 kg left" in the role's colour, the load bar, the same
/// in quintals in parentheses, and the terms a booking fixes once there is
/// one.
class _BookedBox extends StatelessWidget {
  final Trip trip;

  const _BookedBox({required this.trip});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final capacity = trip.capacity;
    final left = tripRoomLeft(trip).clamp(0, capacity).toDouble();
    final booked = (capacity - left).clamp(0, capacity).toDouble();
    final kilograms = '${l10n.tripKgOfKg(_kgOf(booked), _kgOf(capacity))} · '
        '${l10n.tripKgLeft(_kgOf(left))}';
    final quintals = '(${l10n.detailBookedOf(
      formatQuintals(booked),
      formatQuintals(capacity),
    )} · ${l10n.detailLeft(formatQuintals(left))})';
    return FwCard(
      key: const Key('trip_detail_booked'),
      raised: true,
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 18),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Wrap(
            spacing: 10,
            runSpacing: 2,
            alignment: WrapAlignment.spaceBetween,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Text(
                l10n.detailBooked,
                style: const TextStyle(fontSize: 13, color: AppTheme.slate),
              ),
              Text(
                kilograms,
                style: TextStyle(
                  fontSize: 17,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -0.2,
                  color: context.roleColors.primary,
                ),
              ),
            ],
          ),
          const SizedBox(height: 10),
          ClipRRect(
            borderRadius: BorderRadius.circular(999),
            child: LinearProgressIndicator(
              value: booked / capacity,
              minHeight: 6,
              backgroundColor: AppTheme.track,
              valueColor: AlwaysStoppedAnimation<Color>(
                context.roleColors.primary,
              ),
            ),
          ),
          const SizedBox(height: 10),
          Text(
            quintals,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w500,
              color: AppTheme.slate,
            ),
          ),
          if (tripHasBookings(trip)) ...[
            const SizedBox(height: 10),
            Text(
              l10n.tripBookedTermsNote,
              style: const TextStyle(fontSize: 14, color: AppTheme.slate),
            ),
          ],
        ],
      ),
    );
  }
}
