import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/widgets/contact_locked_note.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_bookings_section.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Screen showing full details of a single trip.
///
/// Displays route, departure date, capacity, price, trucker info,
/// status badge, and a cancel button for the trip owner.
/// Follows the same pattern as crop listing detail views.
class TripDetailScreen extends StatelessWidget {
  final Trip trip;

  const TripDetailScreen({required this.trip, super.key});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final dateFormat = DateFormat.yMMMd();

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
        appBar: AppBar(
          title: Text(l10n.tripDetails),
          elevation: 0,
        ),
        body: SafeArea(
          child: Column(
            children: [
              const OfflineBanner(),
              Expanded(
                child: SingleChildScrollView(
                  padding: const EdgeInsets.all(24.0),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      // Route header
                      _RouteHeader(trip: trip),
                      const SizedBox(height: 24),

                      // Status badge
                      _TripDetailStatusBadge(status: trip.status),
                      const SizedBox(height: 24),

                      // Trip info cards
                      _InfoRow(
                        icon: Icons.calendar_today,
                        label: l10n.departureDate,
                        value: dateFormat
                            .format(dayInEthiopia(trip.departureDate)),
                      ),
                      const SizedBox(height: 12),
                      _InfoRow(
                        icon: Icons.local_shipping,
                        label: l10n.capacity,
                        value: trip.remainingQuintals == null
                            ? '${trip.capacity} ${l10n.quintals}'
                            : '${trip.capacity} ${l10n.quintals} · '
                                '${l10n.tripRemainingCapacity(formatQuintals(tripRoomLeft(trip)))}',
                      ),
                      const SizedBox(height: 12),
                      _InfoRow(
                        icon: Icons.payments,
                        label: l10n.pricePerQuintal,
                        value: l10n.etbCurrency(
                          trip.pricePerQuintal.toStringAsFixed(2),
                        ),
                      ),
                      const SizedBox(height: 24),

                      // Trucker contact section
                      Text(
                        l10n.truckerContact,
                        style: theme.textTheme.titleMedium?.copyWith(
                          fontWeight: FontWeight.bold,
                        ),
                      ),
                      const SizedBox(height: 12),
                      _InfoRow(
                        icon: Icons.person,
                        label: l10n.truckerName,
                        value: trip.truckerName,
                      ),
                      // ESC-48: only the trip's own trucker sees the phone
                      // here. A buyer reaches the trucker from the order once
                      // the farmer accepted it, never before any order.
                      Builder(
                        builder: (context) {
                          final authState = context.read<AuthBloc>().state;
                          final me = authState is AuthAuthenticated
                              ? authState.userId
                              : '';
                          if (me.isNotEmpty && me == trip.truckerId) {
                            return Padding(
                              padding: const EdgeInsets.only(top: 12),
                              child: _InfoRow(
                                icon: Icons.phone,
                                label: l10n.truckerPhone,
                                value: trip.truckerPhone,
                              ),
                            );
                          }
                          return Padding(
                            padding: const EdgeInsets.only(top: 16),
                            child: ContactLockedNote(
                              text: l10n.contactAfterEscrowNote,
                            ),
                          );
                        },
                      ),
                      const SizedBox(height: 32),

                      // ESC-15: the orders booked on the trucker's own trip.
                      Builder(
                        builder: (context) {
                          final authState = context.read<AuthBloc>().state;
                          final isOwner = authState is AuthAuthenticated &&
                              authState.userId == trip.truckerId;
                          if (!isOwner) return const SizedBox.shrink();
                          return Padding(
                            padding: const EdgeInsets.only(bottom: 32),
                            child: TripBookingsSection(trip: trip),
                          );
                        },
                      ),

                      // Contextual action buttons based on trip status
                      BlocBuilder<TripListingBloc, TripListingState>(
                        builder: (context, state) {
                          // Owner-only actions: hide Start/Mark Delivered/Cancel
                          // for non-owners (e.g. Buyers browsing all open trips).
                          final authState = context.read<AuthBloc>().state;
                          final isOwner = authState is AuthAuthenticated &&
                              authState.userId == trip.truckerId;
                          if (!isOwner) {
                            return const SizedBox.shrink();
                          }
                          final isLoading = state is TripListingLoading;
                          // ESC-15: a trip stays open while it has room, so
                          // one with bookings may start too (matched = full).
                          final canStart = trip.status == TripStatus.matched ||
                              (trip.status == TripStatus.open &&
                                  tripHasBookings(trip));
                          return Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              // Start Trip (matched trips, or open with bookings)
                              if (canStart)
                                SizedBox(
                                  width: double.infinity,
                                  child: ElevatedButton.icon(
                                    onPressed: isLoading
                                        ? null
                                        : () => _showStartTripConfirmation(
                                              context,
                                              l10n,
                                            ),
                                    style: ElevatedButton.styleFrom(
                                      backgroundColor: AppTheme.statusSuccess,
                                      foregroundColor: Colors.white,
                                      padding: const EdgeInsets.symmetric(
                                        vertical: 16,
                                      ),
                                      minimumSize: const Size.fromHeight(56),
                                      shape: RoundedRectangleBorder(
                                        borderRadius: BorderRadius.circular(8),
                                      ),
                                    ),
                                    icon: isLoading
                                        ? const SizedBox(
                                            height: 20,
                                            width: 20,
                                            child: CircularProgressIndicator(
                                              strokeWidth: 2,
                                              valueColor:
                                                  AlwaysStoppedAnimation<Color>(
                                                Colors.white,
                                              ),
                                            ),
                                          )
                                        : const Icon(Icons.play_arrow),
                                    label: Text(
                                      l10n.startTrip,
                                      style: const TextStyle(fontSize: 16),
                                    ),
                                  ),
                                ),
                              // Mark Delivered (inTransit trips)
                              if (trip.status == TripStatus.inTransit)
                                SizedBox(
                                  width: double.infinity,
                                  child: ElevatedButton.icon(
                                    onPressed: isLoading
                                        ? null
                                        : () => _showMarkDeliveredConfirmation(
                                              context,
                                              l10n,
                                            ),
                                    style: ElevatedButton.styleFrom(
                                      backgroundColor: AppTheme.statusSuccess,
                                      foregroundColor: Colors.white,
                                      padding: const EdgeInsets.symmetric(
                                        vertical: 16,
                                      ),
                                      minimumSize: const Size.fromHeight(56),
                                      shape: RoundedRectangleBorder(
                                        borderRadius: BorderRadius.circular(8),
                                      ),
                                    ),
                                    icon: isLoading
                                        ? const SizedBox(
                                            height: 20,
                                            width: 20,
                                            child: CircularProgressIndicator(
                                              strokeWidth: 2,
                                              valueColor:
                                                  AlwaysStoppedAnimation<Color>(
                                                Colors.white,
                                              ),
                                            ),
                                          )
                                        : const Icon(
                                            Icons.check_circle_outline,
                                          ),
                                    label: Text(
                                      l10n.markDelivered,
                                      style: const TextStyle(fontSize: 16),
                                    ),
                                  ),
                                ),
                              // Cancel button (open/matched trips)
                              if (trip.status == TripStatus.open ||
                                  trip.status == TripStatus.matched) ...[
                                if (canStart) const SizedBox(height: 12),
                                SizedBox(
                                  width: double.infinity,
                                  child: ElevatedButton.icon(
                                    onPressed: isLoading
                                        ? null
                                        : () => _showCancelConfirmation(
                                              context,
                                              l10n,
                                            ),
                                    style: ElevatedButton.styleFrom(
                                      backgroundColor: AppTheme.statusError,
                                      foregroundColor: Colors.white,
                                      padding: const EdgeInsets.symmetric(
                                        vertical: 16,
                                      ),
                                      minimumSize: const Size.fromHeight(56),
                                      shape: RoundedRectangleBorder(
                                        borderRadius: BorderRadius.circular(8),
                                      ),
                                    ),
                                    icon: isLoading
                                        ? const SizedBox(
                                            height: 20,
                                            width: 20,
                                            child: CircularProgressIndicator(
                                              strokeWidth: 2,
                                              valueColor:
                                                  AlwaysStoppedAnimation<Color>(
                                                Colors.white,
                                              ),
                                            ),
                                          )
                                        : const Icon(Icons.cancel_outlined),
                                    label: Text(
                                      l10n.cancelTrip,
                                      style: const TextStyle(fontSize: 16),
                                    ),
                                  ),
                                ),
                              ],
                            ],
                          );
                        },
                      ),
                    ],
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  void _showTripConfirmation(
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

  void _showStartTripConfirmation(
    BuildContext context,
    AppLocalizations l10n,
  ) {
    _showTripConfirmation(
      context,
      title: l10n.startTrip,
      body: l10n.startTripConfirmation,
      confirmLabel: l10n.startTrip,
      confirmColor: AppTheme.statusSuccess,
      eventFactory: (truckerId) =>
          StartTrip(tripId: trip.tripId, truckerId: truckerId),
    );
  }

  void _showMarkDeliveredConfirmation(
    BuildContext context,
    AppLocalizations l10n,
  ) {
    _showTripConfirmation(
      context,
      title: l10n.markDelivered,
      body: l10n.markDeliveredConfirmation,
      confirmLabel: l10n.markDelivered,
      confirmColor: AppTheme.statusSuccess,
      eventFactory: (truckerId) =>
          MarkTripDelivered(tripId: trip.tripId, truckerId: truckerId),
    );
  }

  void _showCancelConfirmation(BuildContext context, AppLocalizations l10n) {
    _showTripConfirmation(
      context,
      title: l10n.cancelTrip,
      body: l10n.cancelTripConfirmation,
      confirmLabel: l10n.cancelTrip,
      confirmColor: AppTheme.statusError,
      eventFactory: (truckerId) =>
          CancelTrip(tripId: trip.tripId, truckerId: truckerId),
    );
  }
}

/// Large route display at the top of the detail screen (Fieldway: TripDetail.dc.html).
class _RouteHeader extends StatelessWidget {
  final Trip trip;

  const _RouteHeader({required this.trip});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 18),
      decoration: BoxDecoration(
        color: const Color(0xFF15201A),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Row(
        children: [
          Container(
            width: 48,
            height: 48,
            decoration: BoxDecoration(
              color: const Color(0xFF22302A),
              borderRadius: BorderRadius.circular(14),
            ),
            child: const Icon(
              Icons.local_shipping,
              size: 24,
              color: Color(0xFFD9922B),
            ),
          ),
          const SizedBox(width: 16),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  '${trip.fromWoreda} → ${trip.toWoreda}',
                  style: const TextStyle(
                    fontSize: 20,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -0.5,
                    color: Colors.white,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  '${trip.pricePerQuintal.toStringAsFixed(0)} ETB per quintal',
                  style: const TextStyle(
                    fontSize: 13,
                    color: Color(0xFFB9C2BD),
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

/// Status badge for the detail screen (larger than card version).
///
/// Uses the shared status vocabulary — same kind mapping as the trip card,
/// so the detail screen can never show a different color for the same state.
class _TripDetailStatusBadge extends StatelessWidget {
  final TripStatus status;

  const _TripDetailStatusBadge({required this.status});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return StatusChip(
      label: tripStatusLabel(status, l10n),
      kind: tripStatusKind(status),
      large: true,
    );
  }
}

/// Reusable row showing icon + label + value.
class _InfoRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;

  const _InfoRow({
    required this.icon,
    required this.label,
    required this.value,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Row(
      children: [
        Icon(
          icon,
          size: 20,
          color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
        ),
        const SizedBox(width: 12),
        Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              label,
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
              ),
            ),
            Text(
              value,
              style: theme.textTheme.bodyLarge?.copyWith(
                fontWeight: FontWeight.w500,
              ),
            ),
          ],
        ),
      ],
    );
  }
}
