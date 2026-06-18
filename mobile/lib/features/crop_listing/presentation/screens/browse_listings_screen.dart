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
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';
import 'package:wereda_connect/shared/widgets/listing_card.dart';

/// Marketplace browse screen with search bar, category filters, and listing cards.
///
/// When [ownerMode] is true (farmer), shows the farmer's own listings using
/// `LoadCropListings` and displays a FAB to create new listings.
/// When false (buyer), shows all active listings as a marketplace.
///
/// When [embedded] is true, the screen omits its own Scaffold and back
/// arrow so it can live inside the adaptive shell as a tab child.
///
/// When [showHeader] is false, the green header is hidden — use this when
/// embedding inside a screen that already has its own header (e.g. inside
/// [FindCropsScreen]). A compact search bar is shown instead for buyer mode.
class BrowseListingsScreen extends StatefulWidget {
  final bool ownerMode;
  final bool embedded;
  final bool showHeader;

  const BrowseListingsScreen({
    this.ownerMode = false,
    this.embedded = false,
    this.showHeader = true,
    super.key,
  });

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

  String? _getFarmerId() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) return authState.userId;
    return null;
  }

  void _loadListings() {
    if (widget.ownerMode) {
      final farmerId = _getFarmerId();
      if (farmerId != null) {
        context.read<CropListingBloc>().add(
              LoadCropListings(farmerId: farmerId),
            );
      }
    } else {
      context.read<CropListingBloc>().add(
            const LoadActiveListings(),
          );
    }
  }

  void _applyFilters() {
    if (widget.ownerMode) {
      final farmerId = _getFarmerId();
      if (farmerId != null) {
        context.read<CropListingBloc>().add(
              FilterCropListings(
                farmerId: farmerId,
                cropType: _selectedCropType,
              ),
            );
      }
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

    final content = Stack(
      children: [
        Column(
          children: [
            // ── Header ──────────────────────────────────────────────────
            if (widget.showHeader)
              GreenHeader(
                height: widget.embedded ? 140 : 170,
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
                      widget.ownerMode ? l10n.myListings : l10n.market,
                      style: const TextStyle(
                        fontSize: 22,
                        fontWeight: FontWeight.w700,
                        color: Colors.white,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      widget.ownerMode
                          ? l10n.yourCropListings
                          : l10n.browseVerifiedFarmers,
                      style: TextStyle(
                        fontSize: 14,
                        color: Colors.white.withAlpha(180),
                      ),
                    ),
                    // Search bar (marketplace browse only — wereda
                    // filter is not used in owner mode)
                    if (!widget.ownerMode) ...[
                      const SizedBox(height: 10),
                      Material(
                        color: Colors.white,
                        borderRadius: BorderRadius.circular(12),
                        child: SizedBox(
                          height: 42,
                          child: TextField(
                            controller: _searchController,
                            decoration: InputDecoration(
                              hintText: l10n.searchListings,
                              hintStyle: const TextStyle(
                                fontSize: 14,
                                color: AppTheme.textMuted,
                              ),
                              prefixIcon: const Icon(
                                Icons.search,
                                color: AppTheme.textMuted,
                              ),
                              border: InputBorder.none,
                              contentPadding:
                                  const EdgeInsets.symmetric(vertical: 10),
                            ),
                            onChanged: (value) {
                              setState(() {
                                _weredaFilter = value.isEmpty ? null : value;
                              });
                              _debouncedApplyFilters();
                            },
                          ),
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            // Compact search bar when header is hidden (buyer browse embedded mode)
            if (!widget.showHeader && !widget.ownerMode)
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 12, 16, 0),
                child: Material(
                  color: AppTheme.surfaceWhite,
                  borderRadius: BorderRadius.circular(12),
                  elevation: 1,
                  child: SizedBox(
                    height: 42,
                    child: TextField(
                      controller: _searchController,
                      decoration: InputDecoration(
                        hintText: l10n.searchListings,
                        hintStyle: const TextStyle(
                          fontSize: 14,
                          color: AppTheme.textMuted,
                        ),
                        prefixIcon: const Icon(
                          Icons.search,
                          color: AppTheme.textMuted,
                        ),
                        border: InputBorder.none,
                        contentPadding:
                            const EdgeInsets.symmetric(vertical: 10),
                      ),
                      onChanged: (value) {
                        setState(() {
                          _weredaFilter = value.isEmpty ? null : value;
                        });
                        _debouncedApplyFilters();
                      },
                    ),
                  ),
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
            // Price range filters (marketplace browse only — not relevant
            // for farmers viewing their own listings)
            if (!widget.ownerMode)
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
                          prefixText: '${l10n.etbCurrency('')} ',
                          hintText: l10n.filterMin,
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 10,
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
                          prefixText: '${l10n.etbCurrency('')} ',
                          hintText: l10n.filterMax,
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 12,
                            vertical: 10,
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
              ),
            const SizedBox(height: 8),
            // Listings
            Expanded(
              child: BlocBuilder<CropListingBloc, CropListingState>(
                buildWhen: (previous, current) =>
                    current is! CropListingCreated,
                builder: (context, state) {
                  if (state is CropListingLoading) {
                    return const Center(child: CircularProgressIndicator());
                  }

                  if (state is CropListingError) {
                    return Center(
                      child: Column(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          Icon(
                            Icons.eco_outlined,
                            size: 56,
                            color: AppTheme.textMuted.withAlpha(150),
                          ),
                          const SizedBox(height: 16),
                          Text(
                            l10n.noCropListings,
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
                      padding: const EdgeInsets.symmetric(
                        horizontal: 20,
                        vertical: 12,
                      ),
                      itemCount: state.listings.length,
                      separatorBuilder: (_, __) => const SizedBox(height: 10),
                      itemBuilder: (context, index) {
                        final listing = state.listings[index];
                        return ListingCard(
                          title: listing.cropType,
                          location: listing.locationName,
                          sellerName: listing.farmerName.isNotEmpty
                              ? listing.farmerName
                              : listing.farmerId,
                          price: l10n.etbCurrency(
                            listing.pricePerKg.toStringAsFixed(0),
                          ),
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
        ),
        // FAB for farmer owner mode (hidden when embedded — shell provides FAB)
        if (widget.ownerMode && !widget.embedded)
          Positioned(
            right: 20,
            bottom: 20,
            child: FloatingActionButton(
              onPressed: () async {
                await Navigator.push(
                  context,
                  MaterialPageRoute<void>(
                    builder: (_) => const CreateCropListingScreen(),
                  ),
                );
                if (!mounted) return;
                _loadListings();
              },
              backgroundColor: AppTheme.accentGold,
              child: const Icon(Icons.add, color: Colors.white),
            ),
          ),
      ],
    );

    return BlocListener<CropListingBloc, CropListingState>(
      listenWhen: (previous, current) => current is CropListingCreated,
      listener: (context, state) => _loadListings(),
      child: widget.embedded ? content : Scaffold(body: content),
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
