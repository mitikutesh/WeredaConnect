import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/trip_departure.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:intl/intl.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order_pricing.dart';
import 'package:wereda_connect/core/models/order_status_sets.dart';
import 'package:wereda_connect/core/sync/listing_report_rules.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/domain/services/matching_service.dart'
    if (dart.library.html) 'package:wereda_connect/features/buyer/domain/services/matching_service_web.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/suggested_trip_card.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/crop_listing/presentation/screens/edit_crop_listing_screen.dart';
import 'package:wereda_connect/features/crop_listing/presentation/widgets/crop_photo_views.dart';
import 'package:wereda_connect/features/listing_report/presentation/report_listing_sheet.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/screens/place_order_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/reputation/domain/owner_reputation.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/route_preview_card.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/low_reputation_pill.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// Full detail view for a single crop listing (the Buyer · Crop detail and
/// transport board): the crop and price large, the farmer, where it is (with
/// the buyer's place on the same drawing), the description, the suggested
/// transport with the price for the quantity needed, and the role's action
/// pinned at the foot (trucker → Create Delivery, buyer → Place Order;
/// contact opens on the order, ESC-48).
class CropListingDetailScreen extends StatefulWidget {
  final CropListing listing;
  final String currentUserId;
  final UserRole currentUserRole;
  final String? deliveryWoreda;
  final double? quantityNeeded;

  /// The buying request that led here, when opened from match results. When
  /// set, a [BuyerIntentBloc] must be provided above this screen; it is
  /// forwarded to [PlaceOrderScreen] so a covering order fulfills the request.
  final BuyerIntent? intent;

  /// B-2: the truck a buyer came from (Find crops on this route), chosen for
  /// transport when it can still take the order; otherwise the buyer is told
  /// why and picks from the suggested trucks as usual.
  final Trip? preselectedTrip;

  const CropListingDetailScreen({
    required this.listing,
    required this.currentUserId,
    required this.currentUserRole,
    this.deliveryWoreda,
    this.quantityNeeded,
    this.intent,
    this.preselectedTrip,
    super.key,
  });

  @override
  State<CropListingDetailScreen> createState() =>
      _CropListingDetailScreenState();
}

class _CropListingDetailScreenState extends State<CropListingDetailScreen> {
  /// The listing shown: the one opened, then the farmer's own edit of it as
  /// saved (SIM-56).
  late CropListing _listing = widget.listing;

  List<Trip>? _suggestedTrips;
  Trip? _selectedTrip;
  bool _tripsLoading = false;

  /// B-2: the truck the buyer came from, until its fresh copy is read with
  /// the suggested trucks ([_confirmPreselectedTrip]).
  Trip? _pendingTrip;

  /// Each suggested trucker's aggregate (`GET /ratings?rateeId=`), fetched
  /// once per screen (3G data); null when it cannot be read.
  final Map<String, Future<RatingSummary?>> _truckerRatings = {};

  Future<RatingSummary?> _truckerRatingFor(String truckerId) =>
      _truckerRatings.putIfAbsent(truckerId, () async {
        try {
          return await context
              .read<RatingBloc>()
              .repository
              .getRatingSummary(truckerId);
        } on Object {
          return null;
        }
      });

  @override
  void initState() {
    super.initState();
    _pickPreselectedTrip();
    if (widget.currentUserRole == UserRole.buyer &&
        widget.deliveryWoreda != null) {
      _loadSuggestedTrips();
    }
    // The location drawing needs the place list for the buyer's place and
    // the towns on the way.
    PlaceRepository.instance.ensureLoaded().then((_) {
      if (mounted) setState(() {});
    }).ignore();
  }

  /// B-2: the truck the buyer came from, when it can still carry an order
  /// (open, its day not over, room left), waits for its fresh copy with the
  /// suggested trucks; else the buyer is told why once the screen shows and
  /// chooses from the suggested trucks.
  void _pickPreselectedTrip() {
    final trip = widget.preselectedTrip;
    if (trip == null ||
        widget.currentUserRole != UserRole.buyer ||
        widget.deliveryWoreda == null) {
      return;
    }
    final problem = tripBookingProblem(trip, now: DateTime.now());
    if (problem == null) {
      _pendingTrip = trip;
    } else {
      _tellTripProblem(problem);
    }
  }

  void _tellTripProblem(TripBookingProblem problem) {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            tripBookingProblemMessage(problem, AppLocalizations.of(context)!),
          ),
        ),
      );
    });
  }

  /// B-2: the truck the buyer came from as it is now, never the copy the
  /// trip detail held: its entry among [suggested], else read again by id
  /// (the cache the pull just refreshed, the server on web). Its price and
  /// room drive the transport line and Place Order. When it filled up,
  /// closed, left or is gone the buyer is told why and none is chosen.
  Future<Trip?> _confirmPreselectedTrip(List<Trip> suggested) async {
    final stale = _pendingTrip;
    if (stale == null) return null;
    _pendingTrip = null;
    Trip? fresh;
    for (final trip in suggested) {
      if (trip.tripId == stale.tripId) fresh = trip;
    }
    fresh ??= await _readTrip(stale.tripId);
    final problem = fresh == null
        ? TripBookingProblem.closed
        : tripBookingProblem(fresh, now: DateTime.now());
    if (problem != null) {
      _tellTripProblem(problem);
      return null;
    }
    return fresh;
  }

  Future<Trip?> _readTrip(String tripId) async {
    if (!mounted) return null;
    try {
      return await context
          .read<TripListingBloc>()
          .repository
          .getTripById(tripId);
    } on Object catch (e) {
      debugPrint('[CropListingDetail] trip re-read failed: $e');
      return null;
    }
  }

  Future<void> _loadSuggestedTrips() async {
    setState(() => _tripsLoading = true);

    // Mobile: refresh open trips from the server into Isar so the local read
    // below sees every trucker's trips even when this screen is opened
    // without a sibling event having pulled first. Null on web, where the web
    // matching stub fetches from the API directly. Offline failures fall back
    // to whatever is cached (offline-first).
    final marketplaceSync = _readMarketplaceSync();
    if (marketplaceSync != null) {
      try {
        await marketplaceSync.pullTrips();
      } on Object catch (e) {
        debugPrint('[CropListingDetail] trip pull failed, using cached: $e');
      }
    }

    List<Trip> trips;
    try {
      // On web, MatchingService is the web stub (accepts dynamic isar).
      // On mobile, IsarService.instance.db returns the real Isar instance.
      final matchingService = MatchingService(isar: IsarService.instance.db);
      trips = await matchingService.findTripsForListing(
        _listing,
        widget.deliveryWoreda!,
        widget.quantityNeeded ?? _listing.quantity,
      );
    } on Object catch (e) {
      // Suggested trips are secondary info, so degrade to an empty list rather
      // than failing the whole screen — but log it instead of swallowing
      // silently, so a real matching bug is not mistaken for "no trips".
      debugPrint('[CropListingDetail] suggested-trips matching failed: $e');
      trips = const [];
    }
    // B-2: the truck the buyer came from, fresh, first when matching left
    // it out (another spelling of the place), so the choice stays visible.
    final chosen = await _confirmPreselectedTrip(trips);
    if (!mounted) return;
    setState(() {
      _suggestedTrips =
          chosen == null || trips.any((t) => t.tripId == chosen.tripId)
              ? trips
              : [chosen, ...trips];
      if (chosen != null) _selectedTrip = chosen;
      _tripsLoading = false;
    });
  }

  /// Reads the (mobile-only) marketplace sync if it is provided. Returns null
  /// when absent (web, or a test that pumps this screen without providers) so
  /// the screen still works against cached data.
  MarketplaceSync? _readMarketplaceSync() {
    try {
      return context.read<MarketplaceSync?>();
    } on ProviderNotFoundException {
      return null;
    }
  }

  /// Open [PlaceOrderScreen], forwarding the buying request + its BLoC when
  /// this screen was reached from match results so a covering order marks the
  /// request fulfilled.
  Future<void> _openPlaceOrder({Trip? trip}) async {
    final farmerName = await _lookupFarmerName(_listing.farmerId);
    if (!mounted) return;
    final intent = widget.intent;
    final intentBloc = intent != null ? context.read<BuyerIntentBloc>() : null;
    final tripUnavailable = await Navigator.push<bool>(
      context,
      MaterialPageRoute<bool>(
        builder: (_) {
          final screen = PlaceOrderScreen(
            listing: _listing,
            farmerName: farmerName,
            selectedTrip: trip,
            intent: intent,
          );
          return intentBloc == null
              ? screen
              : BlocProvider.value(value: intentBloc, child: screen);
        },
      ),
    );
    // B-2: Place Order found the truck can no longer go (already told the
    // buyer why): unselect it and drop it from the suggestions.
    if (tripUnavailable == true && trip != null && mounted) {
      setState(() {
        if (_selectedTrip?.tripId == trip.tripId) _selectedTrip = null;
        _suggestedTrips =
            _suggestedTrips?.where((t) => t.tripId != trip.tripId).toList();
      });
    }
  }

  double get _quantity => widget.quantityNeeded ?? _listing.quantity;

  double get _cropCost => _quantity * 100 * _listing.pricePerKg;

  double get _transportCost =>
      _selectedTrip != null ? _quantity * _selectedTrip!.pricePerQuintal : 0.0;

  /// The server's formula: commission on the crop cost only (SIM-53). The
  /// rows show its santim-rounded legs (SIM-30), so they add up to the total.
  OrderPricing get _pricing =>
      OrderPricing(cropCost: _cropCost, transportCost: _transportCost);

  double get _commission => _pricing.commissionAmount;

  double get _total => _pricing.orderTotal;

  /// The buyer's place, resolved from the delivery wereda for the drawing.
  Place? get _buyerPlace {
    final name = widget.deliveryWoreda;
    if (name == null || name.trim().isEmpty) return null;
    if (!PlaceRepository.instance.isLoaded) return null;
    return PlaceRepository.instance.findByDisplayName(name.trim());
  }

  /// The crop's place: the listing's own position under its place name.
  Place? get _listingPlace {
    final listing = _listing;
    if (listing.location.latitude == 0 && listing.location.longitude == 0) {
      return null;
    }
    return Place(
      id: listing.placeId ?? 'listing-${listing.listingId}',
      en: listing.locationName,
      lat: listing.location.latitude,
      lng: listing.location.longitude,
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final cropType = CropType.fromKey(_listing.cropType);
    final isBuyer = widget.currentUserRole == UserRole.buyer;
    final showTransport = isBuyer && widget.deliveryWoreda != null;

    return Scaffold(
      body: SafeArea(
        bottom: false,
        child: SingleChildScrollView(
          child: FwContentWidth(
            child: Padding(
              padding: const EdgeInsets.fromLTRB(20, 16, 20, 24),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // Back and the status, then the crop large with the price.
                  Row(
                    children: [
                      const FwBackButton(),
                      const Spacer(),
                      _StatusChip(status: _listing.status),
                    ],
                  ),
                  const SizedBox(height: 20),
                  // The photos first: the full 1024 px renditions, read
                  // when the detail opens (the list only has thumbs).
                  if (_listing.photos.isNotEmpty) ...[
                    CropPhotoGallery(listing: _listing),
                    const SizedBox(height: 16),
                  ],
                  _HeaderSection(cropType: cropType, listing: _listing),
                  const SizedBox(height: 14),

                  // Who sells it (Crop detail board): name and rating. Not
                  // for a listing without a real name (SIM-33).
                  if (partyDisplayName(_listing.farmerName, l10n)
                      .isNotEmpty) ...[
                    _FarmerCard(
                      listing: _listing,
                      showLowLabel: isBuyer,
                    ),
                    const SizedBox(height: 14),
                  ],

                  // Where it is: the crop and the buyer on one drawing when
                  // both places are known, else the place name.
                  _LocationSection(
                    listing: _listing,
                    from: _listingPlace,
                    to: isBuyer ? _buyerPlace : null,
                    deliverTo: widget.deliveryWoreda,
                  ),

                  if (_listing.description.isNotEmpty) ...[
                    const SizedBox(height: 14),
                    Text(l10n.description, style: _sectionTitle),
                    const SizedBox(height: 6),
                    Text(
                      _listing.description,
                      style: const TextStyle(
                        fontSize: 14,
                        height: 1.5,
                        color: AppTheme.unselectedNav,
                      ),
                    ),
                  ],

                  // Suggested transport section (buyer with delivery woreda)
                  if (showTransport) ...[
                    const SizedBox(height: 14),
                    ..._buildTransportSection(context, l10n),
                  ],

                  if (isBuyer) ...[
                    const SizedBox(height: 14),
                    // ESC-48: no contact before the money is held. The buyer
                    // reaches the farmer from the order once accepted.
                    Text(
                      l10n.contactAfterEscrowNote,
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.mutedSteel,
                      ),
                    ),
                    // FUP-67: a buyer reports what no check can catch (a
                    // number in a voice note or a photo, a scam).
                    if (_listing.farmerId != widget.currentUserId) ...[
                      const SizedBox(height: 8),
                      ReportListingButton(
                        listingType: ReportedListingType.crop,
                        listingId: _listing.listingId,
                        reporterId: widget.currentUserId,
                      ),
                    ],
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
      bottomNavigationBar: _bottomActions(context, l10n),
    );
  }

  List<Widget> _buildTransportSection(
    BuildContext context,
    AppLocalizations l10n,
  ) {
    return [
      Text(l10n.suggestedTransport, style: _sectionTitle),
      const SizedBox(height: 10),

      // Trip cards or loading/empty state
      if (_tripsLoading)
        const Center(
          child: Padding(
            padding: EdgeInsets.all(16),
            child: CircularProgressIndicator(),
          ),
        )
      else if (_suggestedTrips == null || _suggestedTrips!.isEmpty)
        FwCard(
          child: Center(
            child: Text(
              l10n.noTripsYet,
              style: const TextStyle(color: AppTheme.mutedSteel),
            ),
          ),
        )
      else
        ...(_suggestedTrips!.take(3).map(
              (trip) => Padding(
                padding: const EdgeInsets.only(bottom: 10),
                // The trucker's real rating ends the route line, as on the
                // board; nothing while it loads, offline or unrated.
                child: FutureBuilder<RatingSummary?>(
                  // ESC-31: a trip that carries the trucker's reputation
                  // copy shows it from the row and makes no call.
                  future: OwnerReputation.ofTrip(trip).isPresent
                      ? null
                      : _truckerRatingFor(trip.truckerId),
                  builder: (context, snapshot) {
                    final summary = snapshot.data;
                    final reputation = OwnerReputation.ofTrip(trip);
                    return SuggestedTripCard(
                      trip: trip,
                      isSelected: _selectedTrip?.tripId == trip.tripId,
                      rating: reputation.isPresent
                          ? reputation.rating
                          : (summary != null && summary.count > 0
                              ? summary.average
                              : null),
                      lowReputation: reputation.isLow,
                      onSelect: () {
                        setState(() {
                          _selectedTrip = _selectedTrip?.tripId == trip.tripId
                              ? null
                              : trip;
                        });
                      },
                    );
                  },
                ),
              ),
            )),

      // Price for the quantity the buyer needs (Crop detail board).
      const SizedBox(height: 4),
      FwCard(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              l10n.detailForQuantity(
                l10n.quantityKg(wholeNumber(_quantity * 100)),
              ),
              style: _sectionTitle,
            ),
            const SizedBox(height: 11),
            FwMoneyRow(
              label: l10n.cropCostLabel,
              amount: l10n.etbCurrency(formatEtbAmount(_pricing.cropCostEtb)),
            ),
            if (_selectedTrip != null) ...[
              const SizedBox(height: 9),
              FwMoneyRow(
                label: l10n.transportCostLabel,
                amount: l10n.etbCurrency(
                  formatEtbAmount(_pricing.transportCostEtb),
                ),
              ),
            ],
            const SizedBox(height: 9),
            FwMoneyRow(
              label: l10n.commissionLabel,
              amount: l10n.etbCurrency(formatEtbAmount(_commission)),
            ),
            const SizedBox(height: 9),
            FwMoneyRow(
              label: l10n.totalLabel,
              amount: l10n.etbCurrency(formatEtbAmount(_total)),
              total: true,
            ),
          ],
        ),
      ),
    ];
  }

  /// SIM-56: the owner edits or deletes their own active listing.
  bool _deleting = false;

  /// SIM-56: the farmer's own active listing has Edit (how much is left, the
  /// price, the description) and Delete. A reserved, sold or expired listing
  /// has neither: its orders decide what happens to it.
  Widget? _ownerActions(AppLocalizations l10n) {
    if (_listing.farmerId != widget.currentUserId ||
        _listing.status != ListingStatus.active) {
      return null;
    }
    const label = TextStyle(
      fontFamily: AppTheme.fontFamily,
      fontFamilyFallback: AppTheme.ethiopicFontFallback,
      fontSize: 16,
      fontWeight: FontWeight.w800,
    );
    return FwBottomActions(
      children: [
        FilledButton(
          key: const Key('crop_detail_edit'),
          onPressed: _deleting ? null : _openEdit,
          style: FilledButton.styleFrom(
            minimumSize: const Size.fromHeight(56),
            textStyle: label,
          ),
          child: Text(l10n.edit),
        ),
        const SizedBox(height: 8),
        OutlinedButton(
          key: const Key('crop_detail_delete'),
          onPressed: _deleting ? null : _confirmDelete,
          style: OutlinedButton.styleFrom(
            minimumSize: const Size.fromHeight(48),
            foregroundColor: AppTheme.statusError,
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(14),
            ),
            textStyle:
                label.copyWith(fontSize: 14, fontWeight: FontWeight.w700),
          ),
          child: _deleting
              ? const SizedBox(
                  height: 18,
                  width: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : Text(l10n.delete),
        ),
      ],
    );
  }

  /// Opens the edit form on the bloc this screen uses; the saved listing
  /// replaces the one shown.
  Future<void> _openEdit() async {
    final bloc = context.read<CropListingBloc>();
    final saved = await Navigator.push<CropListing>(
      context,
      MaterialPageRoute<CropListing>(
        builder: (_) => BlocProvider.value(
          value: bloc,
          child: EditCropListingScreen(
            listing: _listing,
            farmerId: widget.currentUserId,
          ),
        ),
      ),
    );
    if (saved != null && mounted) setState(() => _listing = saved);
  }

  /// Asks first, then refuses a listing that orders still need (as the
  /// server does, rule 10), then deletes it and goes back to My Crops. On
  /// the phone the delete is saved first and sent later; a refusal then puts
  /// the listing back (sync-protocol.md 1.17.0).
  Future<void> _confirmDelete() async {
    final l10n = AppLocalizations.of(context)!;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(l10n.deleteListingTitle),
        content: Text(l10n.deleteListingBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(dialogContext, false),
            child: Text(l10n.cancel),
          ),
          FilledButton(
            key: const Key('crop_detail_delete_confirm'),
            onPressed: () => Navigator.pop(dialogContext, true),
            style: FilledButton.styleFrom(
              backgroundColor: AppTheme.statusError,
            ),
            child: Text(l10n.delete),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    final messenger = ScaffoldMessenger.of(context);
    if (await _hasOrdersInProgress()) {
      messenger.showSnackBar(
        SnackBar(content: Text(l10n.errorCropHasActiveOrders)),
      );
      return;
    }
    if (!mounted) return;
    final bloc = context.read<CropListingBloc>();
    setState(() => _deleting = true);
    final outcome = bloc.stream.firstWhere(
      (state) => state is CropListingsLoaded || state is CropListingError,
    );
    bloc.add(
      DeleteCropListing(
        listingId: _listing.listingId,
        farmerId: widget.currentUserId,
      ),
    );
    final CropListingState state;
    try {
      state = await outcome;
    } on StateError {
      // The bloc closed first (the app is going away): nothing to say.
      return;
    }
    if (!mounted) return;
    setState(() => _deleting = false);
    if (state is CropListingError) {
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            localizeErrorCode(state.code, l10n, message: state.message),
          ),
          backgroundColor: AppTheme.statusError,
        ),
      );
      return;
    }
    showSavedOrSent(context, sent: l10n.listingDeleted).ignore();
    Navigator.of(context).pop();
  }

  /// Whether an order this device knows still needs the listing
  /// ([listingRemovalBlockingOrderStatuses]). A read that fails, or no
  /// order repository here, leaves it to the server.
  Future<bool> _hasOrdersInProgress() async {
    try {
      final orders = await context
          .read<OrderBloc>()
          .repository
          .getOrdersByFarmer(widget.currentUserId);
      return orders.any(
        (order) =>
            order.listingId == _listing.listingId &&
            listingRemovalBlockingOrderStatuses.contains(order.status),
      );
    } on Object catch (e) {
      debugPrint('[CropListingDetail] order read before delete failed: $e');
      return false;
    }
  }

  /// The role's actions pinned at the foot: the buyer places the order
  /// (with the chosen transport, or without); the farmer edits or deletes
  /// their own active listing (SIM-56). A trucker has none: a delivery is
  /// created from its order, never from a listing (ESC-23).
  Widget? _bottomActions(BuildContext context, AppLocalizations l10n) {
    switch (widget.currentUserRole) {
      case UserRole.farmer:
        return _ownerActions(l10n);
      case UserRole.trucker:
        return null;
      case UserRole.buyer:
        final withTransport =
            widget.deliveryWoreda != null && _selectedTrip != null;
        return FwBottomActions(
          children: [
            FilledButton(
              key: const Key('crop_detail_place_order'),
              onPressed: () => _openPlaceOrder(trip: _selectedTrip),
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(56),
                textStyle: const TextStyle(
                  fontFamily: AppTheme.fontFamily,
                  fontFamilyFallback: AppTheme.ethiopicFontFallback,
                  fontSize: 16,
                  fontWeight: FontWeight.w800,
                ),
              ),
              child: Text(
                withTransport ? l10n.placeOrderWithTransport : l10n.placeOrder,
              ),
            ),
            if (withTransport) ...[
              const SizedBox(height: 8),
              OutlinedButton(
                key: const Key('crop_detail_place_order_no_transport'),
                onPressed: () => _openPlaceOrder(),
                style: OutlinedButton.styleFrom(
                  minimumSize: const Size.fromHeight(48),
                  shape: RoundedRectangleBorder(
                    borderRadius: BorderRadius.circular(14),
                  ),
                  textStyle: const TextStyle(
                    fontFamily: AppTheme.fontFamily,
                    fontFamilyFallback: AppTheme.ethiopicFontFallback,
                    fontSize: 14,
                    fontWeight: FontWeight.w700,
                  ),
                ),
                child: Text(l10n.placeOrderNoTransport),
              ),
            ],
          ],
        );
    }
  }

  /// The farmer's name for the order: the listing's, else the cached
  /// profile's (phone only), else empty. Never the id (FUP-130): the server
  /// stamps the name it knows and the screens label an unnamed farmer.
  Future<String> _lookupFarmerName(String farmerId) async {
    final listed = partyNameToStore(_listing.farmerName);
    if (listed.isNotEmpty || kIsWeb) return listed;
    final isar = IsarService.instance.db;
    final farmer =
        await isar.users.filter().userIdEqualTo(farmerId).findFirst();
    return partyNameToStore(farmer?.name);
  }
}

const TextStyle _sectionTitle = TextStyle(
  fontSize: 15,
  fontWeight: FontWeight.w800,
  color: AppTheme.soilDark,
);

/// The crop large with how much is left and when it was listed, and the
/// price large on the right (Buyer · Crop detail board).
class _HeaderSection extends StatelessWidget {
  final CropType cropType;
  final CropListing listing;

  const _HeaderSection({required this.cropType, required this.listing});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final listed = l10n.myCropsListedOn(
      DateFormat.MMMd().format(dayInEthiopia(listing.createdAt)),
    );
    return Row(
      crossAxisAlignment: CrossAxisAlignment.end,
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Semantics(
                header: true,
                child: Text(
                  cropType.displayName(l10n),
                  style: const TextStyle(
                    fontSize: 30,
                    fontWeight: FontWeight.w800,
                    letterSpacing: -0.9,
                    height: 1.1,
                    color: AppTheme.soilDark,
                  ),
                ),
              ),
              const SizedBox(height: 2),
              Text(
                '${l10n.detailQuintalsLeft(formatQuintals(listing.quantity))}'
                ' · $listed',
                style: const TextStyle(
                  fontSize: 14,
                  color: AppTheme.mutedSteel,
                ),
              ),
            ],
          ),
        ),
        const SizedBox(width: 12),
        Column(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Text(
              formatEtbAmount(listing.pricePerKg),
              style: const TextStyle(
                fontSize: 30,
                fontWeight: FontWeight.w800,
                letterSpacing: -0.9,
                height: 1.1,
                color: AppTheme.soilDark,
              ),
            ),
            Text(
              l10n.etbPerKg,
              style: const TextStyle(fontSize: 12, color: AppTheme.mutedSteel),
            ),
          ],
        ),
      ],
    );
  }
}

/// The listing's status as the board's small pill.
class _StatusChip extends StatelessWidget {
  final ListingStatus status;

  const _StatusChip({required this.status});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final (background, foreground, label) = switch (status) {
      ListingStatus.active => (
          AppTheme.fieldGreenTint,
          AppTheme.fieldGreen,
          l10n.listingStatusActive,
        ),
      ListingStatus.reserved => (
          AppTheme.actionCream,
          AppTheme.actionInk,
          l10n.listingStatusReserved,
        ),
      ListingStatus.draft => (
          AppTheme.segmentTrack,
          AppTheme.unselectedNav,
          l10n.listingStatusDraft,
        ),
      ListingStatus.sold => (
          AppTheme.dropoffBlueTint,
          AppTheme.dropoffBlue,
          l10n.listingStatusSold,
        ),
      ListingStatus.expired => (
          AppTheme.statusErrorContainer,
          AppTheme.onStatusErrorContainer,
          l10n.listingStatusExpired,
        ),
    };
    return FwPill(label: label, background: background, foreground: foreground);
  }
}

/// Where the crop is. With the buyer's place known too, the board's drawing
/// of both ends ("Meki · crop here", "Merkato · you") and the distance;
/// else the place name on a card.
class _LocationSection extends StatelessWidget {
  final CropListing listing;
  final Place? from;
  final Place? to;
  final String? deliverTo;

  const _LocationSection({
    required this.listing,
    required this.from,
    required this.to,
    required this.deliverTo,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final from = this.from;
    final to = this.to;
    final km = routeDistanceKm(from, to);
    // SIM-33: a junk place name ("105") is left out.
    final place = placeDisplayName(listing.locationName);
    if (from != null && to != null && km != null) {
      final languageCode = Localizations.localeOf(context).languageCode;
      return RoutePreviewCard(
        from: from,
        to: to,
        places: PlaceRepository.instance.places,
        fromLabel: place.isEmpty
            ? l10n.detailCropHere
            : '$place · ${l10n.detailCropHere}',
        toLabel: '${to.displayName(languageCode)} · '
            '${l10n.detailYou.toLowerCase()}',
        footer: l10n.detailKmToYou(km),
        height: 140,
      );
    }
    final deliverTo = this.deliverTo ?? '';
    if (place.isEmpty && deliverTo.isEmpty) return const SizedBox.shrink();
    return FwCard(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      child: Row(
        children: [
          const Icon(
            Icons.location_on_outlined,
            size: 20,
            color: AppTheme.mutedSteel,
          ),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              place,
              style: const TextStyle(
                fontSize: 14,
                fontWeight: FontWeight.w600,
                color: AppTheme.soilDark,
              ),
            ),
          ),
          if (deliverTo.isNotEmpty)
            Text(
              '→ $deliverTo',
              style: const TextStyle(
                fontSize: 13,
                color: AppTheme.mutedSteel,
              ),
            ),
        ],
      ),
    );
  }
}

/// The farmer who sells the crop with their rating (average and count).
///
/// ESC-31: a listing that carries the server's reputation copy shows it
/// from the row, with no call and offline: the effective rating, how
/// reliably the farmer completes orders, when the copy was written and, for
/// a buyer ([showLowLabel]), the "Low reputation" label with one plain
/// line. A listing without the copy reads the aggregate best effort from
/// the server as before; "no ratings yet" while loading or offline.
class _FarmerCard extends StatefulWidget {
  final CropListing listing;

  /// Whether the "Low reputation" label is shown (buyers). The farmer
  /// looking at their own listing reads their standing on My reputation.
  final bool showLowLabel;

  const _FarmerCard({required this.listing, required this.showLowLabel});

  @override
  State<_FarmerCard> createState() => _FarmerCardState();
}

class _FarmerCardState extends State<_FarmerCard> {
  late final OwnerReputation _reputation =
      OwnerReputation.ofListing(widget.listing);

  // Fetched once, not on every rebuild (minimise 3G data usage), and not at
  // all when the row carries the copy.
  late final Future<RatingSummary>? _summaryFuture = _reputation.isPresent
      ? null
      : context
          .read<RatingBloc>()
          .repository
          .getRatingSummary(widget.listing.farmerId);

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final name = partyDisplayName(widget.listing.farmerName, l10n).trim();
    final initials = name
        .split(RegExp(r'\s+'))
        .where((part) => part.isNotEmpty)
        .take(2)
        .map((part) => part.characters.first.toUpperCase())
        .join();
    const muted = TextStyle(fontSize: 12, color: AppTheme.mutedSteel);
    return FutureBuilder<RatingSummary>(
      future: _summaryFuture,
      builder: (context, snapshot) {
        final summary = snapshot.data;
        final double? average;
        final int count;
        if (_reputation.isPresent) {
          average = _reputation.rating;
          count = _reputation.count ?? 0;
        } else {
          final rated = summary != null && summary.count > 0;
          average = rated ? summary.average : null;
          count = rated ? summary.count : 0;
        }
        final reliability = _reputation.reliabilityPct;
        final asOf = _reputation.asOf;
        final low = widget.showLowLabel && _reputation.isLow;
        return FwCard(
          padding: const EdgeInsets.all(14),
          child: Padding(
            padding: EdgeInsets.zero,
            child: Row(
              children: [
                CircleAvatar(
                  radius: 22,
                  backgroundColor: const Color(0xFFE3EEE6),
                  child: Text(
                    initials,
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w800,
                      color: AppTheme.fieldGreen,
                    ),
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        name,
                        style: const TextStyle(
                          fontSize: 15,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.soilDark,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        average != null
                            ? '${l10n.farmerRating} '
                                '${average.toStringAsFixed(1)} · '
                                '${l10n.ratingsCount(count)}'
                            : '${l10n.farmerRating}: ${l10n.noRatingsYet}',
                        style: muted,
                      ),
                      if (_reputation.isPresent && reliability != null) ...[
                        const SizedBox(height: 2),
                        Text(
                          l10n.reputationReliability('$reliability'),
                          style: muted,
                        ),
                      ],
                      if (low) ...[
                        const SizedBox(height: 6),
                        const LowReputationPill(),
                        const SizedBox(height: 4),
                        Text(l10n.reputationLowInfo, style: muted),
                      ],
                      if (_reputation.isPresent && asOf != null) ...[
                        const SizedBox(height: 2),
                        Text(
                          l10n.reputationAsOf(
                            DateFormat.yMMMd().format(dayInEthiopia(asOf)),
                          ),
                          style: muted,
                        ),
                      ],
                    ],
                  ),
                ),
                if (average != null) ...[
                  const SizedBox(width: 8),
                  Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      const Icon(
                        Icons.star_rounded,
                        size: 16,
                        color: AppTheme.money,
                      ),
                      const SizedBox(width: 3),
                      Text(
                        average.toStringAsFixed(1),
                        style: const TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.money,
                        ),
                      ),
                    ],
                  ),
                ],
              ],
            ),
          ),
        );
      },
    );
  }
}
