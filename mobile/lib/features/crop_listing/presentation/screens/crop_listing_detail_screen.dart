import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:intl/intl.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order_pricing.dart';
import 'package:wereda_connect/core/sync/marketplace_sync.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/domain/services/matching_service.dart'
    if (dart.library.html) 'package:wereda_connect/features/buyer/domain/services/matching_service_web.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/utils/money_format.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/suggested_trip_card.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/create_delivery_screen.dart';
import 'package:wereda_connect/features/order/presentation/screens/place_order_screen.dart';
import 'package:wereda_connect/features/rating/domain/repositories/base_rating_repository.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/route_preview_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

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

  const CropListingDetailScreen({
    required this.listing,
    required this.currentUserId,
    required this.currentUserRole,
    this.deliveryWoreda,
    this.quantityNeeded,
    this.intent,
    super.key,
  });

  @override
  State<CropListingDetailScreen> createState() =>
      _CropListingDetailScreenState();
}

class _CropListingDetailScreenState extends State<CropListingDetailScreen> {
  List<Trip>? _suggestedTrips;
  Trip? _selectedTrip;
  bool _tripsLoading = false;

  @override
  void initState() {
    super.initState();
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

    try {
      // On web, MatchingService is the web stub (accepts dynamic isar).
      // On mobile, IsarService.instance.db returns the real Isar instance.
      final matchingService = MatchingService(isar: IsarService.instance.db);
      final trips = await matchingService.findTripsForListing(
        widget.listing,
        widget.deliveryWoreda!,
        widget.quantityNeeded ?? widget.listing.quantity,
      );
      if (mounted) {
        setState(() {
          _suggestedTrips = trips;
          _tripsLoading = false;
        });
      }
    } on Object catch (e) {
      // Suggested trips are secondary info, so degrade to an empty list rather
      // than failing the whole screen — but log it instead of swallowing
      // silently, so a real matching bug is not mistaken for "no trips".
      debugPrint('[CropListingDetail] suggested-trips matching failed: $e');
      if (mounted) {
        setState(() {
          _suggestedTrips = [];
          _tripsLoading = false;
        });
      }
    }
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
    final farmerName = await _lookupFarmerName(widget.listing.farmerId);
    if (!mounted) return;
    final intent = widget.intent;
    final intentBloc = intent != null ? context.read<BuyerIntentBloc>() : null;
    await Navigator.push(
      context,
      MaterialPageRoute<void>(
        builder: (_) {
          final screen = PlaceOrderScreen(
            listing: widget.listing,
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
  }

  double get _quantity => widget.quantityNeeded ?? widget.listing.quantity;

  double get _cropCost => _quantity * 100 * widget.listing.pricePerKg;

  double get _transportCost =>
      _selectedTrip != null ? _quantity * _selectedTrip!.pricePerQuintal : 0.0;

  /// The server's formula: commission on the crop cost only (SIM-53).
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
    final listing = widget.listing;
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
    final cropType = CropType.fromKey(widget.listing.cropType);
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
                      _StatusChip(status: widget.listing.status),
                    ],
                  ),
                  const SizedBox(height: 20),
                  _HeaderSection(cropType: cropType, listing: widget.listing),
                  const SizedBox(height: 14),

                  // Who sells it (Crop detail board): name and rating.
                  if (widget.listing.farmerName.isNotEmpty) ...[
                    _FarmerCard(listing: widget.listing),
                    const SizedBox(height: 14),
                  ],

                  // Where it is: the crop and the buyer on one drawing when
                  // both places are known, else the place name.
                  _LocationSection(
                    listing: widget.listing,
                    from: _listingPlace,
                    to: isBuyer ? _buyerPlace : null,
                    deliverTo: widget.deliveryWoreda,
                  ),

                  if (widget.listing.description.isNotEmpty) ...[
                    const SizedBox(height: 14),
                    Text(l10n.description, style: _sectionTitle),
                    const SizedBox(height: 6),
                    Text(
                      widget.listing.description,
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
                child: SuggestedTripCard(
                  trip: trip,
                  isSelected: _selectedTrip?.tripId == trip.tripId,
                  onSelect: () {
                    setState(() {
                      _selectedTrip =
                          _selectedTrip?.tripId == trip.tripId ? null : trip;
                    });
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
              amount: l10n.etbCurrency(etbAmount(_cropCost)),
            ),
            if (_selectedTrip != null) ...[
              const SizedBox(height: 9),
              FwMoneyRow(
                label: l10n.transportCostLabel,
                amount: l10n.etbCurrency(etbAmount(_transportCost)),
              ),
            ],
            const SizedBox(height: 9),
            FwMoneyRow(
              label: l10n.commissionLabel,
              amount: l10n.etbCurrency(etbAmount(_commission)),
            ),
            const SizedBox(height: 9),
            FwMoneyRow(
              label: l10n.totalLabel,
              amount: l10n.etbCurrency(etbAmount(_total)),
              total: true,
            ),
          ],
        ),
      ),
    ];
  }

  /// The role's actions pinned at the foot: the trucker creates a delivery,
  /// the buyer places the order (with the chosen transport, or without).
  /// The owner has none for now (editing is future work).
  Widget? _bottomActions(BuildContext context, AppLocalizations l10n) {
    switch (widget.currentUserRole) {
      case UserRole.farmer:
        return null;
      case UserRole.trucker:
        return FwBottomActions(
          children: [
            FilledButton.icon(
              onPressed: () {
                Navigator.push(
                  context,
                  MaterialPageRoute<void>(
                    builder: (_) =>
                        CreateDeliveryScreen(listing: widget.listing),
                  ),
                );
              },
              icon: const Icon(Icons.local_shipping),
              label: Text(l10n.createDelivery),
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(56),
              ),
            ),
          ],
        );
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

  Future<String> _lookupFarmerName(String farmerId) async {
    // Use farmerName from the listing if available
    if (widget.listing.farmerName.isNotEmpty) {
      return widget.listing.farmerName;
    }
    if (kIsWeb) {
      return farmerId;
    }
    final isar = IsarService.instance.db;
    final farmer =
        await isar.users.filter().userIdEqualTo(farmerId).findFirst();
    return farmer?.name ?? farmerId;
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
              etbAmount(listing.pricePerKg),
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
    if (from != null && to != null && km != null) {
      final languageCode = Localizations.localeOf(context).languageCode;
      return RoutePreviewCard(
        from: from,
        to: to,
        places: PlaceRepository.instance.places,
        fromLabel: '${listing.locationName} · ${l10n.detailCropHere}',
        toLabel: '${to.displayName(languageCode)} · '
            '${l10n.detailYou.toLowerCase()}',
        footer: l10n.detailKmToYou(km),
        height: 140,
      );
    }
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
              listing.locationName,
              style: const TextStyle(
                fontSize: 14,
                fontWeight: FontWeight.w600,
                color: AppTheme.soilDark,
              ),
            ),
          ),
          if (deliverTo != null && deliverTo!.isNotEmpty)
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

/// The farmer who sells the crop with their aggregate rating (average and
/// count), fetched best effort from the server; "no ratings yet" while
/// loading or offline.
class _FarmerCard extends StatefulWidget {
  final CropListing listing;

  const _FarmerCard({required this.listing});

  @override
  State<_FarmerCard> createState() => _FarmerCardState();
}

class _FarmerCardState extends State<_FarmerCard> {
  // Fetched once, not on every rebuild (minimise 3G data usage).
  late final Future<RatingSummary> _summaryFuture = context
      .read<RatingBloc>()
      .repository
      .getRatingSummary(widget.listing.farmerId);

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final name = widget.listing.farmerName.trim();
    final initials = name
        .split(RegExp(r'\s+'))
        .where((part) => part.isNotEmpty)
        .take(2)
        .map((part) => part.characters.first.toUpperCase())
        .join();
    return FutureBuilder<RatingSummary>(
      future: _summaryFuture,
      builder: (context, snapshot) {
        final summary = snapshot.data;
        final rated = summary != null && summary.count > 0;
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
                        rated
                            ? '${l10n.farmerRating} '
                                '${summary.average.toStringAsFixed(1)} · '
                                '${l10n.ratingsCount(summary.count)}'
                            : '${l10n.farmerRating}: ${l10n.noRatingsYet}',
                        style: const TextStyle(
                          fontSize: 12,
                          color: AppTheme.mutedSteel,
                        ),
                      ),
                    ],
                  ),
                ),
                if (rated) ...[
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
                        summary.average.toStringAsFixed(1),
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
