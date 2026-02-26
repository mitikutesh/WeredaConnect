import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
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
  String? _selectedStatus;
  String? _selectedCropType;
  String? _weredaFilter;
  double? _priceMin;
  double? _priceMax;
  Timer? _debounce;

  final _weredaController = TextEditingController();
  final _priceMinController = TextEditingController();
  final _priceMaxController = TextEditingController();

  @override
  void initState() {
    super.initState();
    _loadListings();
  }

  @override
  void dispose() {
    _debounce?.cancel();
    _weredaController.dispose();
    _priceMinController.dispose();
    _priceMaxController.dispose();
    super.dispose();
  }

  void _debouncedApplyFilters() {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 300), _applyFilters);
  }

  UserRole _getRole() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      return authState.role;
    }
    return UserRole.farmer;
  }

  bool get _isOwnerMode => _getRole() == UserRole.farmer;

  void _loadListings() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      if (authState.role == UserRole.farmer) {
        context.read<CropListingBloc>().add(
              FilterCropListings(farmerId: authState.userId),
            );
      } else {
        context.read<CropListingBloc>().add(
              const LoadActiveListings(),
            );
      }
    }
  }

  String? _getFarmerId() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      return authState.userId;
    }
    return null;
  }

  void _applyFilters() {
    if (_isOwnerMode) {
      final farmerId = _getFarmerId();
      if (farmerId == null) return;
      context.read<CropListingBloc>().add(
            FilterCropListings(
              farmerId: farmerId,
              cropType: _selectedCropType,
              statusFilter: _selectedStatus,
            ),
          );
    } else {
      context.read<CropListingBloc>().add(
            LoadActiveListings(
              cropTypeFilter: _selectedCropType,
              weredaFilter: _weredaFilter,
              priceMin: _priceMin,
              priceMax: _priceMax,
            ),
          );
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Column(
      children: [
        const OfflineBanner(),
        // Status filter chips (farmer mode only)
        if (_isOwnerMode)
          _StatusFilterChipsRow(
            selectedStatus: _selectedStatus,
            onStatusChanged: (status) {
              setState(() {
                _selectedStatus = status;
              });
              _applyFilters();
            },
          ),
        // Crop type filter chips
        _CropTypeFilterChipsRow(
          selectedCropType: _selectedCropType,
          onCropTypeChanged: (cropType) {
            setState(() {
              _selectedCropType = cropType;
            });
            _applyFilters();
          },
        ),
        // Woreda and price range filters (buyer/trucker mode only)
        if (!_isOwnerMode)
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16),
            child: Column(
              children: [
                // Woreda text filter
                TextField(
                  controller: _weredaController,
                  decoration: InputDecoration(
                    isDense: true,
                    prefixIcon: const Icon(Icons.location_on),
                    hintText: l10n.wereda,
                    border: const OutlineInputBorder(),
                    contentPadding: const EdgeInsets.symmetric(
                      horizontal: 12,
                      vertical: 8,
                    ),
                  ),
                  onChanged: (value) {
                    setState(() {
                      _weredaFilter = value.isEmpty ? null : value;
                    });
                    _debouncedApplyFilters();
                  },
                ),
                const SizedBox(height: 8),
                // Price range filter
                Row(
                  children: [
                    Expanded(
                      child: TextField(
                        controller: _priceMinController,
                        keyboardType: TextInputType.number,
                        inputFormatters: [
                          FilteringTextInputFormatter.digitsOnly,
                        ],
                        decoration: InputDecoration(
                          isDense: true,
                          prefixText: '${l10n.etbCurrency} ',
                          hintText: l10n.filterMin,
                          border: const OutlineInputBorder(),
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 8,
                          ),
                        ),
                        onChanged: (value) {
                          setState(() {
                            _priceMin =
                                value.isEmpty ? null : double.tryParse(value);
                          });
                          _debouncedApplyFilters();
                        },
                      ),
                    ),
                    const SizedBox(width: 12),
                    Expanded(
                      child: TextField(
                        controller: _priceMaxController,
                        keyboardType: TextInputType.number,
                        inputFormatters: [
                          FilteringTextInputFormatter.digitsOnly,
                        ],
                        decoration: InputDecoration(
                          isDense: true,
                          prefixText: '${l10n.etbCurrency} ',
                          hintText: l10n.filterMax,
                          border: const OutlineInputBorder(),
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 8,
                          ),
                        ),
                        onChanged: (value) {
                          setState(() {
                            _priceMax =
                                value.isEmpty ? null : double.tryParse(value);
                          });
                          _debouncedApplyFilters();
                        },
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
              ],
            ),
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
                      Text(l10n.genericError),
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
                      onTap: () {
                        final authState = context.read<AuthBloc>().state;
                        if (authState is AuthAuthenticated) {
                          Navigator.push(
                            context,
                            MaterialPageRoute<void>(
                              builder: (_) => CropListingDetailScreen(
                                listing: listing,
                                currentUserId: authState.userId,
                                currentUserRole: authState.role,
                              ),
                            ),
                          );
                        }
                      },
                      onDelete: _isOwnerMode
                          ? () {
                              final farmerId = _getFarmerId();
                              if (farmerId == null) return;
                              context.read<CropListingBloc>().add(
                                    DeleteCropListing(
                                      listingId: listing.listingId,
                                      farmerId: farmerId,
                                    ),
                                  );
                            }
                          : null,
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

/// Horizontal row of status filter chips (farmer mode).
class _StatusFilterChipsRow extends StatelessWidget {
  final String? selectedStatus;
  final ValueChanged<String?> onStatusChanged;

  const _StatusFilterChipsRow({
    required this.selectedStatus,
    required this.onStatusChanged,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    final statusLabels = {
      null: l10n.listingFilterAll,
      'draft': l10n.listingStatusDraft,
      'active': l10n.listingStatusActive,
      'reserved': l10n.listingStatusReserved,
      'sold': l10n.listingStatusSold,
      'expired': l10n.listingStatusExpired,
    };

    return SizedBox(
      height: 50,
      child: ListView(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
        children: statusLabels.entries.map((entry) {
          return Padding(
            padding: const EdgeInsets.only(right: 8),
            child: FilterChip(
              label: Text(entry.value),
              selected: selectedStatus == entry.key,
              onSelected: (_) => onStatusChanged(entry.key),
            ),
          );
        }).toList(),
      ),
    );
  }
}

/// Horizontal row of filter chips for crop types.
class _CropTypeFilterChipsRow extends StatelessWidget {
  final String? selectedCropType;
  final ValueChanged<String?> onCropTypeChanged;

  const _CropTypeFilterChipsRow({
    required this.selectedCropType,
    required this.onCropTypeChanged,
  });

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
              selected: selectedCropType == null,
              onSelected: (_) => onCropTypeChanged(null),
            ),
          ),
          // Crop type chips
          ...CropType.values.map((type) {
            return Padding(
              padding: const EdgeInsets.only(right: 8),
              child: FilterChip(
                avatar: Icon(type.icon, size: 18),
                label: Text(type.displayName(l10n)),
                selected: selectedCropType == type.name,
                onSelected: (_) => onCropTypeChanged(type.name),
              ),
            );
          }),
        ],
      ),
    );
  }
}
