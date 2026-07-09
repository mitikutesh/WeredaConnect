import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/trip_detail_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

/// Browse screen for viewing and filtering trip listings.
///
/// Shows offline banner, filter fields, and a lazy-loaded list of trip cards.
///
/// When [embedded] is true, the screen omits its own Scaffold and back
/// arrow so it can live inside the adaptive shell as a tab child.
class BrowseTripsScreen extends StatefulWidget {
  final bool embedded;

  const BrowseTripsScreen({this.embedded = false, super.key});

  @override
  State<BrowseTripsScreen> createState() => _BrowseTripsScreenState();
}

class _BrowseTripsScreenState extends State<BrowseTripsScreen> {
  final _fromWoredaFilterController = TextEditingController();
  final _toWoredaFilterController = TextEditingController();

  @override
  void initState() {
    super.initState();
    _loadTrips();
  }

  @override
  void dispose() {
    _fromWoredaFilterController.dispose();
    _toWoredaFilterController.dispose();
    super.dispose();
  }

  UserRole _getRole() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      return authState.role;
    }
    return UserRole.farmer;
  }

  bool get _isOwnerMode => _getRole() == UserRole.trucker;

  void _loadTrips() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      if (authState.role == UserRole.trucker) {
        context.read<TripListingBloc>().add(
              LoadTrips(truckerId: authState.userId),
            );
      } else {
        context.read<TripListingBloc>().add(
              const LoadOpenTrips(),
            );
      }
    }
  }

  String? _getTruckerId() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      return authState.userId;
    }
    return null;
  }

  void _applyFilters() {
    final fromWoreda = _fromWoredaFilterController.text.trim();
    final toWoreda = _toWoredaFilterController.text.trim();

    if (_isOwnerMode) {
      final truckerId = _getTruckerId();
      if (truckerId == null) return;
      context.read<TripListingBloc>().add(
            FilterTrips(
              truckerId: truckerId,
              fromWoreda: fromWoreda.isNotEmpty ? fromWoreda : null,
              toWoreda: toWoreda.isNotEmpty ? toWoreda : null,
            ),
          );
    } else {
      context.read<TripListingBloc>().add(
            LoadOpenTrips(
              fromWoreda: fromWoreda.isNotEmpty ? fromWoreda : null,
              toWoreda: toWoreda.isNotEmpty ? toWoreda : null,
            ),
          );
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    final content = Stack(
      children: [
        Column(
          children: [
            // ── Header ──────────────────────────────────────────────────
            GreenHeader(
              height: widget.embedded ? 100 : 130,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (!widget.embedded)
                    GestureDetector(
                      onTap: () => Navigator.of(context).pop(),
                      child: const Padding(
                        padding: EdgeInsets.only(top: 4, bottom: 4),
                        child: Icon(
                          Icons.arrow_back,
                          color: Colors.white,
                          size: 24,
                        ),
                      ),
                    ),
                  Text(
                    l10n.myTrips,
                    style: const TextStyle(
                      fontSize: 22,
                      fontWeight: FontWeight.w700,
                      color: Colors.white,
                    ),
                  ),
                ],
              ),
            ),
            const OfflineBanner(),
            // Filter row
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
              child: Row(
                children: [
                  Expanded(
                    child: PlaceSelector(
                      controller: _fromWoredaFilterController,
                      hintText: l10n.fromWoreda,
                      prefixIcon: Icons.trip_origin,
                      isDense: true,
                      // Selecting a suggestion applies the filter immediately;
                      // free text is still applied via the search button.
                      onPlaceSelected: (_) => _applyFilters(),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: PlaceSelector(
                      controller: _toWoredaFilterController,
                      hintText: l10n.toWoreda,
                      prefixIcon: Icons.place_outlined,
                      isDense: true,
                      onPlaceSelected: (_) => _applyFilters(),
                    ),
                  ),
                  const SizedBox(width: 8),
                  IconButton(
                    onPressed: _applyFilters,
                    icon: const Icon(Icons.search),
                    tooltip: l10n.filterByRoute,
                  ),
                ],
              ),
            ),
            // Trip list
            Expanded(
              child: BlocBuilder<TripListingBloc, TripListingState>(
                builder: (context, state) {
                  if (state is TripListingLoading) {
                    return const Center(child: CircularProgressIndicator());
                  }

                  if (state is TripListingError) {
                    return Center(
                      child: Column(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          Icon(
                            Icons.route_outlined,
                            size: 56,
                            color: AppTheme.textMuted.withAlpha(150),
                          ),
                          const SizedBox(height: 16),
                          Text(
                            l10n.noTripsFound,
                            style: const TextStyle(
                              fontSize: 16,
                              fontWeight: FontWeight.w600,
                              color: AppTheme.textDark,
                            ),
                          ),
                          const SizedBox(height: 4),
                          Text(
                            l10n.dataWillSyncWhenOnline,
                            style: const TextStyle(
                              fontSize: 13,
                              color: AppTheme.textMuted,
                            ),
                            textAlign: TextAlign.center,
                          ),
                          const SizedBox(height: 20),
                          FilledButton(
                            onPressed: _loadTrips,
                            child: Text(l10n.retry),
                          ),
                        ],
                      ),
                    );
                  }

                  if (state is TripsLoaded) {
                    if (state.trips.isEmpty) {
                      return Center(
                        child: Column(
                          mainAxisAlignment: MainAxisAlignment.center,
                          children: [
                            Icon(
                              Icons.local_shipping_outlined,
                              size: 80,
                              color: Theme.of(context).colorScheme.secondary,
                            ),
                            const SizedBox(height: 24),
                            Text(
                              l10n.noTripsFound,
                              style: Theme.of(context).textTheme.titleLarge,
                            ),
                            const SizedBox(height: 8),
                            Text(
                              l10n.dataWillSyncWhenOnline,
                              style: Theme.of(context)
                                  .textTheme
                                  .bodyMedium
                                  ?.copyWith(
                                    color: Theme.of(context)
                                        .colorScheme
                                        .onSurface
                                        .withValues(alpha: 0.6),
                                  ),
                              textAlign: TextAlign.center,
                            ),
                          ],
                        ),
                      );
                    }

                    return RefreshIndicator(
                      onRefresh: () async {
                        _loadTrips();
                      },
                      child: ListView.builder(
                        padding: const EdgeInsets.only(top: 8, bottom: 80),
                        itemCount: state.trips.length,
                        itemBuilder: (context, index) {
                          final trip = state.trips[index];
                          return TripCard(
                            trip: trip,
                            onTap: () {
                              Navigator.push(
                                context,
                                MaterialPageRoute<void>(
                                  builder: (context) =>
                                      TripDetailScreen(trip: trip),
                                ),
                              );
                            },
                            onCancel: _isOwnerMode
                                ? () {
                                    final truckerId = _getTruckerId();
                                    if (truckerId == null) return;
                                    context.read<TripListingBloc>().add(
                                          CancelTrip(
                                            tripId: trip.tripId,
                                            truckerId: truckerId,
                                          ),
                                        );
                                  }
                                : null,
                          );
                        },
                      ),
                    );
                  }

                  // Initial state
                  return Center(
                    child: Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        Icon(
                          Icons.local_shipping_outlined,
                          size: 80,
                          color: Theme.of(context).colorScheme.secondary,
                        ),
                        const SizedBox(height: 24),
                        Text(
                          l10n.noTripsFound,
                          style: Theme.of(context).textTheme.titleLarge,
                        ),
                      ],
                    ),
                  );
                },
              ),
            ),
          ],
        ),
        // FAB for truckers to post a trip (hidden when embedded — shell provides FAB)
        if (_isOwnerMode && !widget.embedded)
          Positioned(
            right: 20,
            bottom: 20,
            child: FloatingActionButton(
              onPressed: () async {
                await Navigator.push(
                  context,
                  MaterialPageRoute<void>(
                    builder: (_) => const CreateTripScreen(),
                  ),
                );
                _loadTrips();
              },
              backgroundColor: AppTheme.accentGold,
              child: const Icon(Icons.add, color: Colors.white),
            ),
          ),
      ],
    );

    // A trip created/started/delivered from the shell FAB or a detail screen
    // leaves this still-mounted list stale (the embedded tab doesn't re-run
    // initState). Re-load so the new/changed trip appears immediately instead
    // of the empty "No trips found" placeholder.
    final withRefresh = BlocListener<TripListingBloc, TripListingState>(
      listenWhen: (previous, current) =>
          current is TripCreated ||
          current is TripStarted ||
          current is TripDelivered,
      listener: (context, state) => _loadTrips(),
      child: content,
    );

    return widget.embedded ? withRefresh : Scaffold(body: withRefresh);
  }
}
