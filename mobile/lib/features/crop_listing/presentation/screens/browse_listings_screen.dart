import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/widgets/crop_listing_card.dart';
import 'package:wereda_connect/features/crop_listing/presentation/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Browse screen for viewing and filtering crop listings.
///
/// Replaces the placeholder ListingsScreen.
/// Shows offline banner, filter chips, and a lazy-loaded list of cards.
class BrowseListingsScreen extends StatefulWidget {
  const BrowseListingsScreen({super.key});

  @override
  State<BrowseListingsScreen> createState() => _BrowseListingsScreenState();
}

class _BrowseListingsScreenState extends State<BrowseListingsScreen> {
  @override
  void initState() {
    super.initState();
    _loadListings();
  }

  void _loadListings() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      context.read<CropListingBloc>().add(
            LoadCropListings(farmerId: authState.userId),
          );
    }
  }

  String? _getFarmerId() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      return authState.userId;
    }
    // TEMPORARY: Mock farmer ID for demo (bypass auth)
    return 'demo-farmer-001';
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Column(
      children: [
        const OfflineBanner(),
        // Filter chips
        _FilterChipsRow(
          onFilterChanged: (cropType) {
            final farmerId = _getFarmerId();
            if (farmerId == null) return;
            context.read<CropListingBloc>().add(
                  FilterCropListings(
                    farmerId: farmerId,
                    cropType: cropType,
                  ),
                );
          },
        ),
        // Listings
        Expanded(
          child: BlocBuilder<CropListingBloc, CropListingState>(
            builder: (context, state) {
              if (state is CropListingLoading) {
                return const Center(child: CircularProgressIndicator());
              }

              if (state is CropListingError) {
                return Center(
                  child: Column(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      const Icon(Icons.error_outline, size: 48),
                      const SizedBox(height: 16),
                      Text(state.message),
                      const SizedBox(height: 16),
                      FilledButton(
                        onPressed: _loadListings,
                        child: Text(l10n.retry),
                      ),
                    ],
                  ),
                );
              }

              if (state is CropListingsLoaded) {
                if (state.listings.isEmpty) {
                  return Center(
                    child: Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        Icon(
                          Icons.list_alt,
                          size: 80,
                          color: Theme.of(context).colorScheme.secondary,
                        ),
                        const SizedBox(height: 24),
                        Text(
                          l10n.noCropListings,
                          style: Theme.of(context).textTheme.titleLarge,
                        ),
                        const SizedBox(height: 8),
                        Text(
                          l10n.dataWillSyncWhenOnline,
                          style:
                              Theme.of(context).textTheme.bodyMedium?.copyWith(
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

                return ListView.builder(
                  padding: const EdgeInsets.only(top: 8, bottom: 80),
                  itemCount: state.listings.length,
                  itemBuilder: (context, index) {
                    final listing = state.listings[index];
                    return CropListingCard(
                      listing: listing,
                      onDelete: () {
                        final farmerId = _getFarmerId();
                        if (farmerId == null) return;
                        context.read<CropListingBloc>().add(
                              DeleteCropListing(
                                listingId: listing.listingId,
                                farmerId: farmerId,
                              ),
                            );
                      },
                    );
                  },
                );
              }

              // Initial state - show empty
              return Center(
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    Icon(
                      Icons.list_alt,
                      size: 80,
                      color: Theme.of(context).colorScheme.secondary,
                    ),
                    const SizedBox(height: 24),
                    Text(
                      l10n.noCropListings,
                      style: Theme.of(context).textTheme.titleLarge,
                    ),
                  ],
                ),
              );
            },
          ),
        ),
      ],
    );
  }
}

/// Horizontal row of filter chips for crop types.
class _FilterChipsRow extends StatefulWidget {
  final ValueChanged<String?> onFilterChanged;

  const _FilterChipsRow({required this.onFilterChanged});

  @override
  State<_FilterChipsRow> createState() => _FilterChipsRowState();
}

class _FilterChipsRowState extends State<_FilterChipsRow> {
  String? _activeFilter;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return SizedBox(
      height: 50,
      child: ListView(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
        children: [
          // "All" chip
          Padding(
            padding: const EdgeInsets.only(right: 8),
            child: FilterChip(
              label: Text(l10n.allCrops),
              selected: _activeFilter == null,
              onSelected: (_) {
                setState(() {
                  _activeFilter = null;
                });
                widget.onFilterChanged(null);
              },
            ),
          ),
          // Crop type chips
          ...CropType.values.map((type) {
            return Padding(
              padding: const EdgeInsets.only(right: 8),
              child: FilterChip(
                avatar: Icon(type.icon, size: 18),
                label: Text(type.displayName(l10n)),
                selected: _activeFilter == type.name,
                onSelected: (_) {
                  setState(() {
                    _activeFilter = type.name;
                  });
                  widget.onFilterChanged(type.name);
                },
              ),
            );
          }),
        ],
      ),
    );
  }
}
