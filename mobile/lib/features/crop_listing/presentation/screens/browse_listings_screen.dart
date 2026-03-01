import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';
import 'package:wereda_connect/shared/widgets/listing_card.dart';

/// Marketplace browse screen with search bar, category filters, and listing cards.
///
/// Role-based behavior:
/// - Farmer: "My Listings" — shows own listings
/// - Trucker: "My Trips" — delegates to trip browse (shown as placeholder)
/// - Buyer: "Marketplace" — shows all active listings
class BrowseListingsScreen extends StatefulWidget {
  const BrowseListingsScreen({super.key});

  @override
  State<BrowseListingsScreen> createState() => _BrowseListingsScreenState();
}

class _BrowseListingsScreenState extends State<BrowseListingsScreen> {
  String? _selectedCropType;
  String? _weredaFilter;
  double? _priceMin;
  double? _priceMax;
  Timer? _debounce;

  final _searchController = TextEditingController();
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
    _searchController.dispose();
    _priceMinController.dispose();
    _priceMaxController.dispose();
    super.dispose();
  }

  void _debouncedApplyFilters() {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 300), _applyFilters);
  }

  void _loadListings() {
    context.read<CropListingBloc>().add(
          const LoadActiveListings(),
        );
  }

  void _applyFilters() {
    context.read<CropListingBloc>().add(
          LoadActiveListings(
            cropTypeFilter: _selectedCropType,
            weredaFilter: _weredaFilter,
            priceMin: _priceMin,
            priceMax: _priceMax,
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Column(
      children: [
        // ── Header ──────────────────────────────────────────────────
        GreenHeader(
          height: 155,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const SizedBox(height: 8),
              Text(
                l10n.market,
                style: const TextStyle(
                  fontSize: 22,
                  fontWeight: FontWeight.w700,
                  color: Colors.white,
                ),
              ),
              const SizedBox(height: 4),
              Text(
                l10n.browseVerifiedFarmers,
                style: TextStyle(
                  fontSize: 14,
                  color: Colors.white.withAlpha(180),
                ),
              ),
              const SizedBox(height: 10),
              // Search bar
              Container(
                height: 42,
                decoration: BoxDecoration(
                  color: Colors.white,
                  borderRadius: BorderRadius.circular(12),
                ),
                child: TextField(
                  controller: _searchController,
                  decoration: InputDecoration(
                    hintText: l10n.searchListings,
                    hintStyle: const TextStyle(
                      fontSize: 14,
                      color: AppTheme.textMuted,
                    ),
                    prefixIcon:
                        const Icon(Icons.search, color: AppTheme.textMuted),
                    border: InputBorder.none,
                    contentPadding: const EdgeInsets.symmetric(vertical: 10),
                  ),
                  onChanged: (value) {
                    setState(() {
                      _weredaFilter = value.isEmpty ? null : value;
                    });
                    _debouncedApplyFilters();
                  },
                ),
              ),
            ],
          ),
        ),
        const OfflineBanner(),
        // Crop type filter chips
        _CropTypeFilterChipsRow(
          selectedCropType: _selectedCropType,
          onCropTypeChanged: (cropType) {
            setState(() => _selectedCropType = cropType);
            _applyFilters();
          },
        ),
        // Price range filters
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 20),
          child: Row(
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
                    contentPadding: const EdgeInsets.symmetric(
                      horizontal: 12,
                      vertical: 10,
                    ),
                  ),
                  onChanged: (value) {
                    setState(() {
                      _priceMin = value.isEmpty ? null : double.tryParse(value);
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
                    contentPadding: const EdgeInsets.symmetric(
                      horizontal: 12,
                      vertical: 10,
                    ),
                  ),
                  onChanged: (value) {
                    setState(() {
                      _priceMax = value.isEmpty ? null : double.tryParse(value);
                    });
                    _debouncedApplyFilters();
                  },
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: 8),
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
                        const Icon(
                          Icons.eco_outlined,
                          size: 64,
                          color: AppTheme.textMuted,
                        ),
                        const SizedBox(height: 16),
                        Text(
                          l10n.noCropListings,
                          style: const TextStyle(
                            fontSize: 16,
                            fontWeight: FontWeight.w500,
                            color: AppTheme.textMuted,
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
                      ],
                    ),
                  );
                }

                return ListView.separated(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 20, vertical: 12),
                  itemCount: state.listings.length,
                  separatorBuilder: (_, __) => const SizedBox(height: 10),
                  itemBuilder: (context, index) {
                    final listing = state.listings[index];
                    return ListingCard(
                      title: listing.cropType,
                      location: listing.locationName,
                      sellerName: listing.farmerId,
                      price:
                          '${listing.pricePerKg.toStringAsFixed(0)} ${l10n.etbCurrency}',
                      hasEscrow: true,
                      rating: 4.5,
                      icon: Icons.eco,
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
                    );
                  },
                );
              }

              // Initial state
              return Center(
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    const Icon(
                      Icons.eco_outlined,
                      size: 64,
                      color: AppTheme.textMuted,
                    ),
                    const SizedBox(height: 16),
                    Text(
                      l10n.noCropListings,
                      style: const TextStyle(
                        fontSize: 16,
                        color: AppTheme.textMuted,
                      ),
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
        padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
        children: [
          Padding(
            padding: const EdgeInsets.only(right: 8),
            child: FilterChip(
              label: Text(l10n.allCrops),
              selected: selectedCropType == null,
              labelStyle: TextStyle(
                color:
                    selectedCropType == null ? Colors.white : AppTheme.textDark,
              ),
              onSelected: (_) => onCropTypeChanged(null),
            ),
          ),
          ...CropType.values.map((type) {
            final isSelected = selectedCropType == type.name;
            return Padding(
              padding: const EdgeInsets.only(right: 8),
              child: FilterChip(
                avatar: Icon(type.icon, size: 18),
                label: Text(type.displayName(l10n)),
                selected: isSelected,
                labelStyle: TextStyle(
                  color: isSelected ? Colors.white : AppTheme.textDark,
                ),
                onSelected: (_) => onCropTypeChanged(type.name),
              ),
            );
          }),
        ],
      ),
    );
  }
}
