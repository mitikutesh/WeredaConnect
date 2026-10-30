import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/create_trip_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/screens/trip_detail_screen.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_card.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/navigation/role_nav_bar.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fieldway_lists.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

/// The trips list, a sibling of the My Crops and Market boards: page header
/// (person · place, title, profile square), the route filter, then one
/// [TripCard] per trip inside the phone-width column at every window size.
///
/// A trucker sees only their own trips and may cancel an open one here; a
/// buyer sees every open trip from every trucker (CLAUDE.md visibility).
///
/// When [embedded] is true, the screen omits its own Scaffold and back
/// arrow so it can live inside the adaptive shell as a tab child. A parent
/// that already draws the page header (the Market's Transport segment)
/// passes [showHeader] false.
class BrowseTripsScreen extends StatefulWidget {
  final bool embedded;
  final bool showHeader;

  const BrowseTripsScreen({
    this.embedded = false,
    this.showHeader = true,
    super.key,
  });

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

  /// Cancel Trip from the card asks first, like the detail screen does.
  void _confirmCancel(Trip trip) {
    final l10n = AppLocalizations.of(context)!;
    showDialog<void>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(l10n.cancelTrip),
        content: Text(l10n.cancelTripConfirmation),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(),
            child: Text(l10n.listsKeepTrip),
          ),
          TextButton(
            onPressed: () {
              Navigator.of(dialogContext).pop();
              final truckerId = _getTruckerId();
              if (truckerId == null) return;
              context.read<TripListingBloc>().add(
                    CancelTrip(tripId: trip.tripId, truckerId: truckerId),
                  );
            },
            style: TextButton.styleFrom(foregroundColor: AppTheme.statusError),
            child: Text(l10n.cancelTrip),
          ),
        ],
      ),
    );
  }

  void _openProfile() {
    Navigator.push(
      context,
      MaterialPageRoute<void>(builder: (_) => const ProfileScreen()),
    );
  }

  Widget _buildHeader(AppLocalizations l10n) {
    final authState = context.read<AuthBloc>().state;
    final isOwner = _isOwnerMode;
    final header = FwPageHeader(
      padding: EdgeInsets.zero,
      eyebrow: fwPersonPlaceEyebrow(
        authState,
        isOwner ? l10n.trucker : l10n.marketTransport,
      ),
      title: isOwner ? l10n.myTrips : l10n.trips,
      trailing: FwSquareButton(
        onTap: _openProfile,
        semanticLabel: l10n.profile,
        child: const Icon(
          Icons.person_outline,
          size: 22,
          color: AppTheme.soilDark,
        ),
      ),
    );
    // A tab child draws under the status bar; a pushed screen has SafeArea.
    final top = widget.embedded ? MediaQuery.paddingOf(context).top : 0.0;
    return Padding(
      padding: EdgeInsets.fromLTRB(20, 16 + top, 20, 12),
      child: widget.embedded
          ? header
          : Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                FwSquareButton(
                  onTap: () => Navigator.of(context).maybePop(),
                  semanticLabel:
                      MaterialLocalizations.of(context).backButtonTooltip,
                  child: const Icon(
                    Icons.arrow_back,
                    size: 20,
                    color: AppTheme.soilDark,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(child: header),
              ],
            ),
    );
  }

  Widget _buildFilters(AppLocalizations l10n) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 0, 20, 8),
      child: Row(
        children: [
          Expanded(
            child: PlaceSelector(
              controller: _fromWoredaFilterController,
              hintText: l10n.listsRouteFrom,
              prefixIcon: Icons.trip_origin,
              contentPadding: const EdgeInsets.symmetric(
                horizontal: 12,
                vertical: 12,
              ),
              // Selecting a suggestion applies the filter immediately;
              // free text is still applied via the search button.
              onPlaceSelected: (_) => _applyFilters(),
            ),
          ),
          const SizedBox(width: 8),
          Expanded(
            child: PlaceSelector(
              controller: _toWoredaFilterController,
              hintText: l10n.listsRouteTo,
              prefixIcon: Icons.place_outlined,
              contentPadding: const EdgeInsets.symmetric(
                horizontal: 12,
                vertical: 12,
              ),
              onPlaceSelected: (_) => _applyFilters(),
            ),
          ),
          const SizedBox(width: 8),
          FwSquareButton(
            onTap: _applyFilters,
            semanticLabel: l10n.filterByRoute,
            child: const Icon(
              Icons.search,
              size: 22,
              color: AppTheme.soilDark,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildEmpty(AppLocalizations l10n, {String? detail}) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 32),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(
              Icons.local_shipping_outlined,
              size: 56,
              color: AppTheme.mutedSteel,
            ),
            const SizedBox(height: 16),
            Text(l10n.noTripsFound, style: FwText.cardTitle),
            const SizedBox(height: 4),
            Text(
              detail ?? l10n.dataWillSyncWhenOnline,
              style: FwText.body,
              textAlign: TextAlign.center,
            ),
            if (detail != null) ...[
              const SizedBox(height: 20),
              FilledButton(
                onPressed: _loadTrips,
                child: Text(l10n.retry),
              ),
            ],
          ],
        ),
      ),
    );
  }

  Widget _buildList(AppLocalizations l10n, List<Trip> trips) {
    if (trips.isEmpty) return _buildEmpty(l10n);
    final isOwner = _isOwnerMode;
    // The last row is the note the boards end their lists with: who else
    // sees these trips.
    final note = Padding(
      padding: const EdgeInsets.fromLTRB(20, 8, 20, 0),
      child: isOwner
          ? FwInfoNote(
              icon: Icons.lock_outline,
              text: l10n.listsMyTripsPrivacyNote,
            )
          : Padding(
              padding: const EdgeInsets.symmetric(horizontal: 4),
              child: Text(l10n.listsAllTripsNote, style: FwText.body),
            ),
    );
    return RefreshIndicator(
      onRefresh: () async {
        _loadTrips();
      },
      child: ListView.builder(
        padding: const EdgeInsets.only(
          top: 4,
          bottom: Breakpoints.fabClearance,
        ),
        itemCount: trips.length + 1,
        itemBuilder: (context, index) {
          if (index == trips.length) return note;
          final trip = trips[index];
          return TripCard(
            trip: trip,
            onTap: () {
              Navigator.push(
                context,
                MaterialPageRoute<void>(
                  builder: (context) => TripDetailScreen(trip: trip),
                ),
              );
            },
            onCancel: isOwner ? () => _confirmCancel(trip) : null,
          );
        },
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    final column = Column(
      children: [
        if (widget.showHeader || !widget.embedded) _buildHeader(l10n),
        const OfflineBanner(),
        _buildFilters(l10n),
        Expanded(
          child: BlocBuilder<TripListingBloc, TripListingState>(
            builder: (context, state) {
              if (state is TripListingLoading) {
                return const Center(child: CircularProgressIndicator());
              }
              if (state is TripListingError) {
                return _buildEmpty(
                  l10n,
                  detail: localizeLoadError(state.code, l10n),
                );
              }
              if (state is TripsLoaded) return _buildList(l10n, state.trips);
              // Initial state
              return _buildEmpty(l10n);
            },
          ),
        ),
      ],
    );

    final content = Stack(
      children: [
        FwContentWidth(child: column),
        // Post Trip for a trucker who reached the list outside the shell
        // (the shell draws its own create button when embedded).
        if (_isOwnerMode && !widget.embedded)
          Positioned(
            left: 0,
            right: 0,
            bottom: 16,
            child: Center(
              child: RoleCreateButton(
                role: UserRole.trucker,
                onPressed: () async {
                  await Navigator.push(
                    context,
                    MaterialPageRoute<void>(
                      builder: (_) => const CreateTripScreen(),
                    ),
                  );
                  _loadTrips();
                },
              ),
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

    return widget.embedded
        ? withRefresh
        : Scaffold(body: SafeArea(child: withRefresh));
  }
}
