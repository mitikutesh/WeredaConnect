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
import 'package:wereda_connect/features/crop_listing/presentation/widgets/crop_photo_views.dart';
import 'package:wereda_connect/features/order/domain/repositories/base_order_repository.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/trip_listing/domain/repositories/base_trip_listing_repository.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/account_menu.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/listing_card.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// Crop listings list (Fieldway Canvas: MyCrops.dc.html for the farmer,
/// Market.dc.html for the buyer).
///
/// When [ownerMode] is true (farmer), shows the farmer's own listings using
/// `LoadCropListings` under the Active / Reserved / Sold / Drafts switch,
/// and a FAB to create new listings when not embedded. Farmers see only
/// their own listings here (CLAUDE.md visibility rule).
/// When false (buyer), shows every farmer's active listing with the crop
/// chips, the transport line per card and the board's footnote.
///
/// When [embedded] is true, the screen omits its own Scaffold and back
/// arrow so it can live inside the adaptive shell as a tab child.
///
/// When [showHeader] is false, the page header is left out: use this when
/// embedding inside a screen that already has one (e.g. inside
/// [FindCropsScreen]). The buyer's search box and price range then follow
/// [showFilters]; with the header shown, its search button toggles them.
class BrowseListingsScreen extends StatefulWidget {
  final bool ownerMode;
  final bool embedded;
  final bool showHeader;
  final bool showFilters;

  const BrowseListingsScreen({
    this.ownerMode = false,
    this.embedded = false,
    this.showHeader = true,
    this.showFilters = true,
    super.key,
  });

  @override
  State<BrowseListingsScreen> createState() => _BrowseListingsScreenState();
}

class _BrowseListingsScreenState extends State<BrowseListingsScreen> {
  static final NumberFormat _pricePerKgFormat = NumberFormat('#,##0', 'en_US');

  /// My Crops: the segment picked. Filters the loaded list on the phone, so
  /// the counts on the segments stay whole.
  ListingStatus _selectedStatus = ListingStatus.active;
  String? _selectedCropType;
  String? _searchQuery;
  double? _priceMin;
  double? _priceMax;
  Timer? _debounce;

  /// Buyer browse with its own header: whether the search box and the
  /// price range are open (the header's search button toggles them).
  bool _filtersOpen = false;

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

  /// Whether the buyer's search box and price range show: behind the
  /// header's button here, the parent's when the header is theirs.
  bool get _filtersVisible =>
      !widget.ownerMode &&
      (widget.showHeader ? _filtersOpen : widget.showFilters);

  /// True when the visible buyer list is narrowed by the crop chip, the
  /// search box or a price bound. An empty result then means nothing
  /// matches, not that nothing has synced yet (SIM-51). My Crops always has
  /// a segment picked, so it is judged against the farmer's whole list.
  bool get _hasActiveFilters =>
      _selectedCropType != null ||
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
      _selectedStatus = ListingStatus.active;
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

  /// The trips this week per listing and the sorted list, worked out once
  /// per trips read and buyer place, not on every rebuild (a filter
  /// keystroke, a rating resolving): low-end phones.
  List<Trip>? _matchTrips;
  String? _matchWoreda;
  final Map<String, List<Trip>> _thisWeek = {};
  List<CropListing>? _sortedFrom;
  List<CropListing> _sorted = const [];

  List<Trip> _tripsThisWeek(
    CropListing listing,
    List<Trip> trips,
    String? woreda,
  ) {
    if (!identical(trips, _matchTrips) || woreda != _matchWoreda) {
      _matchTrips = trips;
      _matchWoreda = woreda;
      _thisWeek.clear();
      _sortedFrom = null;
    }
    return _thisWeek.putIfAbsent(
      listing.listingId,
      () => tripsThisWeekFor(listing, trips, woreda, DateTime.now().toUtc()),
    );
  }

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
    final fitting = _tripsThisWeek(listing, trips, woreda);
    if (fitting.isEmpty) return l10n.marketNoTripYet;
    if (fitting.length > 1) return l10n.marketTripsThisWeek(fitting.length);
    final trip = fitting.single;
    final day = DateFormat.E().format(dayInEthiopia(trip.departureDate));
    // "Transport Thu · 18 q room", short enough for the pill.
    return '${l10n.marketTransportOn(day)} · '
        '${l10n.marketQuintalsRoom(formatQuintals(tripRoomLeft(trip)))}';
  }

  /// "Meki · listed Mon" (this week), "Meki · listed today", or the short
  /// date further back: the farmer's own card needs no seller name.
  String _ownListingSubtitle(CropListing listing, AppLocalizations l10n) {
    DateTime date(DateTime at) => DateTime(at.year, at.month, at.day);
    final day = dayInEthiopia(listing.createdAt);
    final today = dayInEthiopia(DateTime.now().toUtc());
    final daysAgo = date(today).difference(date(day)).inDays;
    final listed = daysAgo == 0
        ? l10n.cropsListedToday
        : daysAgo > 0 && daysAgo < 7
            ? l10n.myCropsListedOn(DateFormat.E().format(day))
            : l10n.myCropsListedOn(DateFormat.MMMd().format(day));
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
    return _tripsThisWeek(listing, trips, woreda).isNotEmpty;
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
        FwContentWidth(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              // ── Header ──────────────────────────────────────────────
              if (widget.showHeader)
                SafeArea(
                  bottom: false,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      if (!widget.embedded)
                        Padding(
                          padding: const EdgeInsets.fromLTRB(8, 4, 8, 0),
                          child: IconButton(
                            onPressed: () => Navigator.of(context).pop(),
                            tooltip: l10n.back,
                            icon: const Icon(
                              Icons.arrow_back,
                              color: AppTheme.soilDark,
                            ),
                          ),
                        ),
                      if (widget.ownerMode)
                        // MyCrops board: "Almaz Bekele · Meki" over the
                        // title, the Farmer pill on the right, then the
                        // account button where no sidebar lists Profile.
                        FwPageHeader(
                          eyebrow: _ownerLine() ?? l10n.yourCropListings,
                          title: l10n.myCrops,
                          trailing: Row(
                            mainAxisSize: MainAxisSize.min,
                            children: [
                              _RolePill(label: l10n.farmer),
                              if (!SidebarScope.of(context)) ...[
                                const SizedBox(width: 8),
                                const AccountButton(),
                              ],
                            ],
                          ),
                        )
                      else
                        FwPageHeader(
                          eyebrow: l10n.browseVerifiedFarmers,
                          title: l10n.market,
                          trailing: FwSquareButton(
                            semanticLabel: l10n.marketSearchToggle,
                            background: _filtersOpen
                                ? AppTheme.fieldGreenTint
                                : AppTheme.surfaceWhite,
                            onTap: () =>
                                setState(() => _filtersOpen = !_filtersOpen),
                            child: Icon(
                              Icons.search,
                              size: 20,
                              color: _filtersOpen
                                  ? AppTheme.fieldGreen
                                  : AppTheme.soilDark,
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
              // Search box and price range (buyer browse only), out of the
              // default view as the Market board shows the list bare.
              if (_filtersVisible)
                Padding(
                  padding: const EdgeInsets.fromLTRB(20, 4, 20, 4),
                  child: Column(
                    children: [
                      _FilterField(
                        controller: _searchController,
                        hintText: l10n.searchListings,
                        prefixIcon: Icons.search,
                        onChanged: _onSearchChanged,
                      ),
                      const SizedBox(height: 8),
                      Row(
                        children: [
                          Expanded(
                            child: _FilterField(
                              controller: _priceMinController,
                              hintText: l10n.filterMin,
                              prefixText: '${l10n.etbCurrency('')} ',
                              numeric: true,
                              onChanged: (value) {
                                setState(() {
                                  _priceMin = value.isEmpty
                                      ? null
                                      : double.tryParse(value);
                                });
                                _debouncedApplyFilters();
                              },
                            ),
                          ),
                          const SizedBox(width: 10),
                          Expanded(
                            child: _FilterField(
                              controller: _priceMaxController,
                              hintText: l10n.filterMax,
                              prefixText: '${l10n.etbCurrency('')} ',
                              numeric: true,
                              onChanged: (value) {
                                setState(() {
                                  _priceMax = value.isEmpty
                                      ? null
                                      : double.tryParse(value);
                                });
                                _debouncedApplyFilters();
                              },
                            ),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),
              const OfflineBanner(),
              // My Crops: the status switch (MyCrops board); buyer browse:
              // the crop chips (Market board).
              if (widget.ownerMode)
                BlocBuilder<CropListingBloc, CropListingState>(
                  builder: (context, state) => Padding(
                    padding: const EdgeInsets.fromLTRB(20, 4, 20, 4),
                    child: _StatusSegmented(
                      listings: state is CropListingsLoaded
                          ? state.listings
                          : const <CropListing>[],
                      selected: _selectedStatus,
                      onChanged: (status) =>
                          setState(() => _selectedStatus = status),
                    ),
                  ),
                )
              else
                _CropTypeChipsRow(
                  selectedCropType: _selectedCropType,
                  onCropTypeChanged: (cropType) {
                    setState(() => _selectedCropType = cropType);
                    _applyFilters();
                  },
                ),
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
                      final listings = widget.ownerMode
                          ? [
                              for (final l in state.listings)
                                if (l.status == _selectedStatus) l,
                            ]
                          : _sortedByTransport(state.listings);
                      if (widget.ownerMode &&
                          listings.isEmpty &&
                          state.listings.isNotEmpty) {
                        // The picked segment is empty; the others are not.
                        return _NoMatch(
                          onClear: null,
                          title: l10n.noListingsMatchFilters,
                          hint: l10n.noListingsMatchFiltersHint,
                          clearLabel: l10n.clearFilters,
                        );
                      }
                      if (listings.isEmpty && _hasActiveFilters) {
                        // A search or filter with no hits says so and offers
                        // a way back, instead of the offline copy below
                        // (SIM-51).
                        return _NoMatch(
                          onClear: _clearFilters,
                          title: l10n.noListingsMatchFilters,
                          hint: l10n.noListingsMatchFiltersHint,
                          clearLabel: l10n.clearFilters,
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
                            8,
                            20,
                            Breakpoints.fabClearance,
                          ),
                          // Both lists end with a line about who sees what.
                          itemCount: listings.length + 1,
                          separatorBuilder: (_, __) =>
                              const SizedBox(height: 10),
                          itemBuilder: (context, index) {
                            if (index == listings.length) {
                              return widget.ownerMode
                                  ? _PrivacyNote(text: l10n.myCropsPrivacyNote)
                                  : _Footnote(text: _footnote(l10n));
                            }
                            final listing = listings[index];
                            final crop = CropType.labelFor(
                              listing.cropType,
                              l10n,
                            );
                            // The seller's real rating; hidden until it
                            // loads or when the farmer has none. No escrow
                            // badge: there is no per-listing escrow fact to
                            // show.
                            return FutureBuilder<RatingSummary>(
                              future: _ratingFor(listing.farmerId),
                              builder: (context, snapshot) {
                                final summary = snapshot.data;
                                final rated =
                                    summary != null && summary.count > 0;
                                return ListingCard(
                                  // Market board: "Wheat · 28 quintals left";
                                  // My Crops: the crop alone.
                                  title: widget.ownerMode
                                      ? crop
                                      : '$crop · ${l10n.marketQuintalsLeft(formatQuintals(listing.quantity))}',
                                  location: listing.locationName,
                                  sellerName: listing.farmerName.isNotEmpty
                                      ? listing.farmerName
                                      : listing.farmerId,
                                  // "42" (or "1,000") with "ETB/kg" beside it.
                                  price: _pricePerKgFormat
                                      .format(listing.pricePerKg.round()),
                                  priceUnit: l10n.etbPerKg,
                                  rating: rated ? summary.average : null,
                                  ratingCount: rated ? summary.count : null,
                                  cropKey: listing.cropType,
                                  // The cover thumb (320 px rendition);
                                  // the full photos open on the detail.
                                  leading: listing.photos.isEmpty
                                      ? null
                                      : CropPhotoThumb(listing: listing),
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
                                          builder: (_) =>
                                              CropListingDetailScreen(
                                            listing: listing,
                                            currentUserId: authState.userId,
                                            currentUserRole: authState.role,
                                            // A buyer's own wereda is where
                                            // the crop goes unless a buying
                                            // request says otherwise, so the
                                            // detail can suggest transport.
                                            deliveryWoreda:
                                                _buyerDeliveryWoreda(
                                              authState,
                                            ),
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
        ),
        // FAB for farmer owner mode (hidden when embedded: the shell provides
        // the create button)
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

  /// Market board: every farmer's listing, the ones a trip this week can
  /// carry to the buyer's place first (the amber pill), then those some open
  /// trip still leaves from, then the rest, the server's order kept inside
  /// each group. Unsorted until the trips are read. Nothing is dropped: the
  /// buyer sees every listing.
  List<CropListing> _sortedByTransport(List<CropListing> listings) {
    final trips = _openTrips;
    if (trips == null) return listings;
    final auth = context.read<AuthBloc>().state;
    final woreda =
        auth is AuthAuthenticated ? _buyerDeliveryWoreda(auth) : null;
    // Resets the caches when the trips or the place changed.
    if (listings.isNotEmpty) _tripsThisWeek(listings.first, trips, woreda);
    if (!identical(listings, _sortedFrom)) {
      _sortedFrom = listings;
      _sorted = sortedByTransportMatch(
        listings,
        trips,
        woreda,
        DateTime.now().toUtc(),
      );
    }
    return _sorted;
  }

  /// Market board: "Every farmer's listing, sorted by the best match for
  /// Merkato." (the buyer's place), or the no-place line.
  String _footnote(AppLocalizations l10n) {
    final auth = context.read<AuthBloc>().state;
    final woreda =
        auth is AuthAuthenticated ? _buyerDeliveryWoreda(auth) : null;
    return woreda == null
        ? l10n.marketFootnoteNoPlace
        : l10n.marketFootnote(woreda);
  }
}

/// The MyCrops board's "Farmer" pill beside the title.
class _RolePill extends StatelessWidget {
  final String label;

  const _RolePill({required this.label});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 9),
      decoration: BoxDecoration(
        color: AppTheme.fieldGreenTint,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.eco_outlined, size: 16, color: AppTheme.fieldGreen),
          const SizedBox(width: 6),
          Text(
            label,
            style: const TextStyle(
              fontSize: 13,
              fontWeight: FontWeight.w700,
              color: AppTheme.fieldGreen,
            ),
          ),
        ],
      ),
    );
  }
}

/// A search or price field: white, whisper border, radius 14.
class _FilterField extends StatelessWidget {
  final TextEditingController controller;
  final String hintText;
  final IconData? prefixIcon;
  final String? prefixText;
  final bool numeric;
  final ValueChanged<String> onChanged;

  const _FilterField({
    required this.controller,
    required this.hintText,
    required this.onChanged,
    this.prefixIcon,
    this.prefixText,
    this.numeric = false,
  });

  @override
  Widget build(BuildContext context) {
    final border = OutlineInputBorder(
      borderRadius: BorderRadius.circular(14),
      borderSide: const BorderSide(color: AppTheme.whisperBorder),
    );
    return TextField(
      controller: controller,
      keyboardType: numeric ? TextInputType.number : null,
      inputFormatters:
          numeric ? [FilteringTextInputFormatter.digitsOnly] : null,
      style: const TextStyle(fontSize: 14, color: AppTheme.soilDark),
      decoration: InputDecoration(
        isDense: true,
        filled: true,
        fillColor: AppTheme.surfaceWhite,
        hintText: hintText,
        hintStyle: FwText.body.copyWith(fontSize: 14),
        prefixIcon: prefixIcon == null
            ? null
            : Icon(prefixIcon, size: 20, color: AppTheme.mutedSteel),
        prefixText: prefixText,
        contentPadding:
            const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
        border: border,
        enabledBorder: border,
        focusedBorder: border.copyWith(
          borderSide: const BorderSide(color: AppTheme.fieldGreen),
        ),
      ),
      onChanged: onChanged,
    );
  }
}

/// "No listings match your filters": with [onClear] a Clear filters button,
/// without one just the words (My Crops, where the segments are the filter).
class _NoMatch extends StatelessWidget {
  final VoidCallback? onClear;
  final String title;
  final String hint;
  final String clearLabel;

  const _NoMatch({
    required this.onClear,
    required this.title,
    required this.hint,
    required this.clearLabel,
  });

  @override
  Widget build(BuildContext context) {
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
              title,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w500,
                color: AppTheme.textMuted,
              ),
              textAlign: TextAlign.center,
            ),
            const SizedBox(height: 4),
            Text(
              hint,
              style: const TextStyle(
                fontSize: 13,
                color: AppTheme.textMuted,
              ),
              textAlign: TextAlign.center,
            ),
            if (onClear != null) ...[
              const SizedBox(height: 20),
              OutlinedButton(
                onPressed: onClear,
                child: Text(clearLabel),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

/// MyCrops board: who sees the listings, in a blue note under the list.
class _PrivacyNote extends StatelessWidget {
  final String text;

  const _PrivacyNote({required this.text});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: AppTheme.dropoffBlueTint,
        borderRadius: BorderRadius.circular(16),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Icon(
            Icons.place_outlined,
            size: 18,
            color: AppTheme.dropoffBlue,
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              text,
              style: const TextStyle(
                fontSize: 13,
                height: 1.4,
                fontWeight: FontWeight.w600,
                color: AppTheme.dropoffBlue,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// Market board: the muted line under the buyer's list.
class _Footnote extends StatelessWidget {
  final String text;

  const _Footnote({required this.text});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(4, 2, 4, 0),
      child: Text(
        text,
        textAlign: TextAlign.center,
        style: FwText.body.copyWith(height: 1.4),
      ),
    );
  }
}

/// My Crops: the Active / Reserved / Sold / Drafts switch with counts
/// (MyCrops board); Expired joins it only when the farmer has one.
class _StatusSegmented extends StatelessWidget {
  final List<CropListing> listings;
  final ListingStatus selected;
  final ValueChanged<ListingStatus> onChanged;

  const _StatusSegmented({
    required this.listings,
    required this.selected,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    int count(ListingStatus status) =>
        listings.where((l) => l.status == status).length;
    FwSegment<ListingStatus> segment(ListingStatus status, String label) {
      final n = count(status);
      return FwSegment(value: status, label: label, count: n > 0 ? n : null);
    }

    final expired = count(ListingStatus.expired);
    return FwSegmented<ListingStatus>(
      segments: [
        segment(ListingStatus.active, l10n.listingStatusActive),
        segment(ListingStatus.reserved, l10n.listingStatusReserved),
        segment(ListingStatus.sold, l10n.listingStatusSold),
        segment(ListingStatus.draft, l10n.cropsDrafts),
        if (expired > 0 || selected == ListingStatus.expired)
          segment(ListingStatus.expired, l10n.listingStatusExpired),
      ],
      selected: selected,
      onChanged: onChanged,
    );
  }
}

/// Market board: the crop chips, "All" filled field green, the others
/// white with a whisper border.
class _CropTypeChipsRow extends StatelessWidget {
  final String? selectedCropType;
  final ValueChanged<String?> onCropTypeChanged;

  const _CropTypeChipsRow({
    required this.selectedCropType,
    required this.onCropTypeChanged,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return SizedBox(
      height: 60,
      child: ListView(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.fromLTRB(20, 8, 12, 8),
        children: [
          _CropChip(
            label: l10n.allCrops,
            selected: selectedCropType == null,
            onTap: () => onCropTypeChanged(null),
          ),
          for (final type in CropType.values)
            _CropChip(
              label: type.displayName(l10n),
              selected: selectedCropType == type.name,
              onTap: () => onCropTypeChanged(type.name),
            ),
        ],
      ),
    );
  }
}

class _CropChip extends StatelessWidget {
  final String label;
  final bool selected;
  final VoidCallback onTap;

  const _CropChip({
    required this.label,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(right: 8),
      child: Semantics(
        button: true,
        selected: selected,
        child: Material(
          color: selected ? AppTheme.fieldGreen : AppTheme.surfaceWhite,
          shape: StadiumBorder(
            side: BorderSide(
              color: selected ? AppTheme.fieldGreen : AppTheme.whisperBorder,
            ),
          ),
          clipBehavior: Clip.antiAlias,
          child: InkWell(
            onTap: onTap,
            child: Container(
              height: 44,
              padding: const EdgeInsets.symmetric(horizontal: 18),
              alignment: Alignment.center,
              child: Text(
                label,
                style: TextStyle(
                  fontSize: 14,
                  fontWeight: selected ? FontWeight.w700 : FontWeight.w600,
                  color: selected ? Colors.white : AppTheme.soilDark,
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
