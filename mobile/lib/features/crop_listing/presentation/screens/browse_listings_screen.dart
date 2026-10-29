import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/crop_listing/domain/transport_this_week.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/create_crop_listing_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';
import 'package:wereda_connect/shared/widgets/listing_card.dart';
import 'package:wereda_connect/shared/widgets/status_chip.dart';

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
  /// My Crops: the status chip picked (null = all). Filters the loaded
  /// list on the phone, so the counts on the chips stay whole.
  ListingStatus? _selectedStatus;
  String? _selectedCropType;
  String? _searchQuery;
  double? _priceMin;
  double? _priceMax;
  Timer? _debounce;

  final _searchController = TextEditingController();
  final _priceMinController = TextEditingController();
  final _priceMaxController = TextEditingController();

  /// Seller aggregates (`GET /ratings?rateeId=`), one fetch per farmer per
  /// screen instance: cards rebuild on every filter and scroll, so the future
  /// is cached to spend 3G data once. SIM-19 (was a hardcoded 4.5).
  final Map<String, Future<RatingSummary>> _ratingFutures = {};

  Future<RatingSummary> _ratingFor(String farmerId) =>
      _ratingFutures.putIfAbsent(
        farmerId,
        () => context.read<RatingBloc>().repository.getRatingSummary(farmerId),
      );

  @override
  void initState() {
    super.initState();
    _loadListings();
    _loadOrderedQuintals();
    _loadOpenTrips();
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

  /// Search box text (crop, farmer or place, SIM-51); blank means no search.
  void _onSearchChanged(String value) {
    setState(() {
      _searchQuery = value.trim().isEmpty ? null : value;
    });
    _debouncedApplyFilters();
  }

  /// True when the visible list is narrowed by the crop chip or, in buyer
  /// browse, by the search box or a price bound. An empty result then means
  /// nothing matches, not that nothing has synced yet (SIM-51).
  bool get _hasActiveFilters =>
      _selectedCropType != null ||
      (widget.ownerMode && _selectedStatus != null) ||
      (!widget.ownerMode &&
          (_searchQuery != null || _priceMin != null || _priceMax != null));

  /// Resets the search box, crop chip and price range, then reloads the
  /// unfiltered list.
  void _clearFilters() {
    _debounce?.cancel();
    _searchController.clear();
    _priceMinController.clear();
    _priceMaxController.clear();
    setState(() {
      _selectedCropType = null;
      _selectedStatus = null;
      _searchQuery = null;
      _priceMin = null;
      _priceMax = null;
    });
    _loadListings();
  }

  /// The buyer's registered wereda, the default drop-off for transport
  /// suggestions; null for other roles or when the profile has none.
  static String? _buyerDeliveryWoreda(AuthAuthenticated auth) {
    if (auth.role != UserRole.buyer) return null;
    final woreda = auth.woreda?.trim();
    return (woreda == null || woreda.isEmpty) ? null : woreda;
  }

  /// My Crops: quintals ordered per listing id, from the farmer's own paid
  /// orders (their quantity is what the server took off the listing).
  Map<String, double> _orderedByListing = const {};

  Future<void> _loadOrderedQuintals() async {
    final farmerId = _getFarmerId();
    if (!widget.ownerMode || farmerId == null) return;
    try {
      final BaseOrderRepository repository =
          context.read<OrderBloc>().repository;
      final orders = await repository.getOrdersByFarmer(farmerId);
      final ordered = <String, double>{};
      for (final order in orders) {
        if (!listingStockTakenOrderStatuses.contains(order.status)) continue;
        ordered[order.listingId] =
            (ordered[order.listingId] ?? 0) + order.quantity / 100;
      }
      if (mounted) setState(() => _orderedByListing = ordered);
    } on Object catch (e) {
      // Secondary detail: the cards stay without it.
      debugPrint('[MyCrops] order read failed: $e');
    }
  }

  /// Buyer browse: the open trips, read once, for each card's transport
  /// line. Null until read (or with no trip repository), so no line shows.
  List<Trip>? _openTrips;

  Future<void> _loadOpenTrips() async {
    if (widget.ownerMode) return;
    final auth = context.read<AuthBloc>().state;
    // Buyers see every trip; nobody else gets this read.
    if (auth is! AuthAuthenticated || auth.role != UserRole.buyer) return;
    try {
      // Secondary detail: no trip bloc, or a failed read, leaves no line.
      final BaseTripListingRepository repository =
          context.read<TripListingBloc>().repository;
      final trips = await repository.getOpenTrips();
      if (mounted) setState(() => _openTrips = trips);
    } on Object catch (e) {
      debugPrint('[FindCrops] trip read failed: $e');
    }
  }

  /// "Transport Thu · 18 quintals free", "2 trips this week" or "No trip
  /// yet" (Buyer · Find crops board); null before the trips are read.
  String? _transportLine(CropListing listing, AppLocalizations l10n) {
    final trips = _openTrips;
    if (trips == null) return null;
    final auth = context.read<AuthBloc>().state;
    final woreda =
        auth is AuthAuthenticated ? _buyerDeliveryWoreda(auth) : null;
    final fitting =
        tripsThisWeekFor(listing, trips, woreda, DateTime.now().toUtc());
    if (fitting.isEmpty) return l10n.marketNoTripYet;
    if (fitting.length > 1) return l10n.marketTripsThisWeek(fitting.length);
    final trip = fitting.single;
    final day = DateFormat.E().format(dayInEthiopia(trip.departureDate));
    return '${l10n.marketTransportOn(day)} · '
        '${l10n.tripRemainingCapacity(formatQuintals(tripRoomLeft(trip)))}';
  }

  /// "Meki · listed 28 Oct": the farmer's own card needs no seller name.
  String _ownListingSubtitle(CropListing listing, AppLocalizations l10n) {
    final listed = l10n.myCropsListedOn(
      DateFormat.MMMd().format(dayInEthiopia(listing.createdAt)),
    );
    return listing.locationName.isEmpty
        ? listed
        : '${listing.locationName} · $listed';
  }

  /// The share of the listing already ordered (My Crops bar); null when no
  /// order has been read.
  double? _orderedShare(CropListing listing) {
    final ordered = _orderedByListing[listing.listingId] ?? 0;
    final total = listing.quantity + ordered;
    return total <= 0 ? null : ordered / total;
  }

  /// The listing's status as the card shows it under the price.
  String _statusLabel(ListingStatus status, AppLocalizations l10n) =>
      switch (status) {
        ListingStatus.active => l10n.listingStatusActive,
        ListingStatus.reserved => l10n.listingStatusReserved,
        ListingStatus.sold => l10n.listingStatusSold,
        ListingStatus.draft => l10n.listingStatusDraft,
        ListingStatus.expired => l10n.listingStatusExpired,
      };

  /// Whether a trip this week can carry [listing] (the pill turns amber).
  bool _hasTransportThisWeek(CropListing listing) {
    final trips = _openTrips;
    if (trips == null) return false;
    final auth = context.read<AuthBloc>().state;
    final woreda =
        auth is AuthAuthenticated ? _buyerDeliveryWoreda(auth) : null;
    return tripsThisWeekFor(listing, trips, woreda, DateTime.now().toUtc())
        .isNotEmpty;
  }

  /// "32 of 60 quintals ordered", or "8 quintals, no orders yet".
  String _orderedDetail(CropListing listing, AppLocalizations l10n) {
    final ordered = _orderedByListing[listing.listingId] ?? 0;
    if (ordered <= 0) {
      return l10n.myCropsNoOrdersYet(formatQuintals(listing.quantity));
    }
    return l10n.myCropsOrderedOf(
      formatQuintals(ordered),
      formatQuintals(listing.quantity + ordered),
    );
  }

  /// My Crops header: "Almaz Bekele · Meki", from the profile; null when
  /// it has neither.
  String? _ownerLine() {
    final auth = context.read<AuthBloc>().state;
    if (auth is! AuthAuthenticated) return null;
    final parts = [auth.userName.trim(), (auth.woreda ?? '').trim()]
        .where((p) => p.isNotEmpty);
    return parts.isEmpty ? null : parts.join(' · ');
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
              searchQuery: _searchQuery,
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
                light: true,
                // My Crops has no search box under its title.
                height: widget.ownerMode
                    ? (widget.embedded ? 92 : 122)
                    : (widget.embedded ? 140 : 170),
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
                            color: AppTheme.soilDark,
                            size: 24,
                          ),
                        ),
                      ),
                    // MyCrops board: "Almaz Bekele · Meki" above the title.
                    if (widget.ownerMode) ...[
                      Text(
                        _ownerLine() ?? l10n.yourCropListings,
                        style: const TextStyle(
                          fontSize: 13,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                      const SizedBox(height: 2),
                    ],
                    Row(
                      children: [
                        Expanded(
                          child: Text(
                            widget.ownerMode ? l10n.myCrops : l10n.market,
                            style: const TextStyle(
                              fontSize: 26,
                              fontWeight: FontWeight.w800,
                              letterSpacing: -0.5,
                              color: AppTheme.soilDark,
                            ),
                          ),
                        ),
                        if (widget.ownerMode)
                          StatusChip(
                            label: l10n.farmer,
                            kind: StatusKind.success,
                            icon: Icons.eco_outlined,
                          ),
                      ],
                    ),
                    if (!widget.ownerMode) ...[
                      const SizedBox(height: 4),
                      Text(
                        l10n.browseVerifiedFarmers,
                        style: const TextStyle(
                          fontSize: 14,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                    ],
                    // Search bar (marketplace browse only: owner mode
                    // has no search)
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
                            onChanged: _onSearchChanged,
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
                      onChanged: _onSearchChanged,
                    ),
                  ),
                ),
              ),
            const OfflineBanner(),
            // My Crops filters by status (MyCrops board); buyer browse by
            // crop type.
            if (widget.ownerMode)
              BlocBuilder<CropListingBloc, CropListingState>(
                builder: (context, state) => _StatusFilterChipsRow(
                  listings: state is CropListingsLoaded
                      ? state.listings
                      : const <CropListing>[],
                  selected: _selectedStatus,
                  onChanged: (status) =>
                      setState(() => _selectedStatus = status),
                ),
              )
            else
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
                            localizeLoadError(state.code, l10n),
                            style: const TextStyle(
                              fontSize: 13,
                              color: AppTheme.textMuted,
                            ),
                            textAlign: TextAlign.center,
                          ),
                          const SizedBox(height: 20),
                          FilledButton(
                            // Keep the visible search and filters.
                            onPressed: _applyFilters,
                            child: Text(l10n.retry),
                          ),
                        ],
                      ),
                    );
                  }

                  if (state is CropListingsLoaded) {
                    final status = _selectedStatus;
                    final listings = widget.ownerMode && status != null
                        ? [
                            for (final l in state.listings)
                              if (l.status == status) l,
                          ]
                        : state.listings;
                    if (listings.isEmpty && _hasActiveFilters) {
                      // A search or filter with no hits says so and offers a
                      // way back, instead of the offline copy below (SIM-51).
                      return Center(
                        child: SingleChildScrollView(
                          padding: const EdgeInsets.symmetric(horizontal: 24),
                          child: Column(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              const Icon(
                                Icons.search_off,
                                size: 64,
                                color: AppTheme.textMuted,
                              ),
                              const SizedBox(height: 16),
                              Text(
                                l10n.noListingsMatchFilters,
                                style: const TextStyle(
                                  fontSize: 16,
                                  fontWeight: FontWeight.w500,
                                  color: AppTheme.textMuted,
                                ),
                                textAlign: TextAlign.center,
                              ),
                              const SizedBox(height: 4),
                              Text(
                                l10n.noListingsMatchFiltersHint,
                                style: const TextStyle(
                                  fontSize: 13,
                                  color: AppTheme.textMuted,
                                ),
                                textAlign: TextAlign.center,
                              ),
                              const SizedBox(height: 20),
                              OutlinedButton(
                                onPressed: _clearFilters,
                                child: Text(l10n.clearFilters),
                              ),
                            ],
                          ),
                        ),
                      );
                    }

                    if (listings.isEmpty) {
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

                    return RefreshIndicator(
                      // Refresh keeps the visible search and filters.
                      onRefresh: () async {
                        _applyFilters();
                        await _loadOrderedQuintals();
                      },
                      child: ListView.separated(
                        physics: const AlwaysScrollableScrollPhysics(),
                        padding: const EdgeInsets.fromLTRB(
                          20,
                          12,
                          20,
                          Breakpoints.fabClearance,
                        ),
                        // The farmer's own list ends with who sees it.
                        itemCount: listings.length + (widget.ownerMode ? 1 : 0),
                        separatorBuilder: (_, __) => const SizedBox(height: 10),
                        itemBuilder: (context, index) {
                          if (index == listings.length) {
                            // MyCrops board: who sees the listings, in a
                            // blue note.
                            return Container(
                              padding: const EdgeInsets.all(14),
                              decoration: BoxDecoration(
                                color: const Color(0xFFE6ECF5),
                                borderRadius: BorderRadius.circular(16),
                              ),
                              child: Row(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  const Icon(
                                    Icons.place_outlined,
                                    size: 18,
                                    color: Color(0xFF2E4C7D),
                                  ),
                                  const SizedBox(width: 10),
                                  Expanded(
                                    child: Text(
                                      l10n.myCropsPrivacyNote,
                                      style: const TextStyle(
                                        fontSize: 13,
                                        height: 1.4,
                                        fontWeight: FontWeight.w600,
                                        color: Color(0xFF2E4C7D),
                                      ),
                                    ),
                                  ),
                                ],
                              ),
                            );
                          }
                          final listing = listings[index];
                          // The seller's real rating; hidden until it loads
                          // or when the farmer has none. No escrow badge:
                          // there is no per-listing escrow fact to show.
                          return FutureBuilder<RatingSummary>(
                            future: _ratingFor(listing.farmerId),
                            builder: (context, snapshot) {
                              final summary = snapshot.data;
                              final rated =
                                  summary != null && summary.count > 0;
                              return ListingCard(
                                title: CropType.labelFor(
                                  listing.cropType,
                                  l10n,
                                ),
                                location: listing.locationName,
                                sellerName: listing.farmerName.isNotEmpty
                                    ? listing.farmerName
                                    : listing.farmerId,
                                // "42" over "ETB/kg", as on the boards.
                                price: listing.pricePerKg.toStringAsFixed(0),
                                priceUnit: l10n.etbPerKg,
                                rating: rated ? summary.average : null,
                                ratingCount: rated ? summary.count : null,
                                icon: Icons.eco,
                                detail: widget.ownerMode
                                    ? _orderedDetail(listing, l10n)
                                    : _transportLine(listing, l10n),
                                detailAsPill: !widget.ownerMode,
                                detailHighlighted: !widget.ownerMode &&
                                    _hasTransportThisWeek(listing),
                                progress: widget.ownerMode
                                    ? _orderedShare(listing)
                                    : null,
                                status: widget.ownerMode
                                    ? _statusLabel(listing.status, l10n)
                                    : null,
                                subtitle: widget.ownerMode
                                    ? _ownListingSubtitle(listing, l10n)
                                    : null,
                                onTap: () {
                                  final authState =
                                      context.read<AuthBloc>().state;
                                  if (authState is AuthAuthenticated) {
                                    Navigator.push(
                                      context,
                                      MaterialPageRoute<void>(
                                        builder: (_) => CropListingDetailScreen(
                                          listing: listing,
                                          currentUserId: authState.userId,
                                          currentUserRole: authState.role,
                                          // A buyer's own wereda is where
                                          // the crop goes unless a buying
                                          // request says otherwise, so the
                                          // detail can suggest transport.
                                          deliveryWoreda:
                                              _buyerDeliveryWoreda(authState),
                                        ),
                                      ),
                                    );
                                  }
                                },
                              );
                            },
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

/// My Crops status chips with counts: All, Active, Reserved, Sold, Draft.
class _StatusFilterChipsRow extends StatelessWidget {
  final List<CropListing> listings;
  final ListingStatus? selected;
  final ValueChanged<ListingStatus?> onChanged;

  const _StatusFilterChipsRow({
    required this.listings,
    required this.selected,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final chips = <(ListingStatus?, String)>[
      (null, l10n.all),
      (ListingStatus.active, l10n.listingStatusActive),
      (ListingStatus.reserved, l10n.listingStatusReserved),
      (ListingStatus.sold, l10n.listingStatusSold),
      (ListingStatus.draft, l10n.listingStatusDraft),
    ];

    return SizedBox(
      height: 50,
      child: ListView(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
        children: [
          for (final (status, label) in chips)
            Padding(
              padding: const EdgeInsets.only(right: 8),
              child: Builder(
                builder: (context) {
                  final count = status == null
                      ? listings.length
                      : listings.where((l) => l.status == status).length;
                  final isSelected = selected == status;
                  return FilterChip(
                    key: ValueKey('status-chip-${status?.name ?? 'all'}'),
                    label: Text(count > 0 ? '$label · $count' : label),
                    selected: isSelected,
                    showCheckmark: false,
                    labelStyle: TextStyle(
                      fontWeight: FontWeight.w700,
                      color: isSelected ? Colors.white : AppTheme.textDark,
                    ),
                    selectedColor: AppTheme.soilDark,
                    onSelected: (_) => onChanged(status),
                  );
                },
              ),
            ),
        ],
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
