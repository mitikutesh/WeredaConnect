import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order_delivery_location.dart';
import 'package:wereda_connect/core/models/order_dropoff_note.dart';
import 'package:wereda_connect/core/models/order_pricing.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/trip_departure.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/grouped_number_input.dart';
import 'package:wereda_connect/core/utils/party_name.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/features/order/presentation/widgets/delivery_place.dart';
import 'package:wereda_connect/features/order/presentation/widgets/dropoff_note.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/reputation/domain/owner_reputation.dart';
import 'package:wereda_connect/features/reputation/presentation/widgets/low_reputation_order_dialog.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/trip_status_kind.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/crop_glyph.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';
import 'package:wereda_connect/features/buyer/presentation/widgets/suggested_trip_card.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/features/order/presentation/commission_label.dart';

/// Screen for buyers to place an order on a crop listing (the Buyer · Place
/// order board): the listing in one line, the quantity in kg with minus and
/// plus, the chosen transport, the price breakdown, the escrow note and
/// Place Order pinned at the foot. Works fully offline (saved to Isar,
/// synced later).
///
/// LT-12: with a [selectedTrip] the buyer may add a drop-off landmark for
/// the driver (a self-pickup order has no drop-off).
///
/// LT-14: with a [selectedTrip] the buyer picks the drop-off place, starting
/// from the [intent]'s place or the registered place; the order carries its
/// point (none when no place is picked: the trip's destination stands in).
///
/// B-2: with a [selectedTrip] the order is checked against the truck when
/// placed. A truck that left or no longer takes orders closes the screen
/// with `true`, so the listing detail unselects it; one too small for the
/// quantity keeps the screen open to order less.
class PlaceOrderScreen extends StatefulWidget {
  final CropListing listing;
  final String farmerName;
  final Trip? selectedTrip;

  /// The buying request this order came from, when reached via match results.
  /// When set, a [BuyerIntentBloc] MUST be available above this screen: a
  /// successful order that covers the full quantity needed marks the request
  /// fulfilled so it stops generating "new match" badges and push alerts.
  final BuyerIntent? intent;

  /// The place list for the drop-off place (LT-14); the shared one when
  /// null. Tests pass a loaded one.
  final PlaceRepository? placeRepository;

  const PlaceOrderScreen({
    required this.listing,
    required this.farmerName,
    this.selectedTrip,
    this.intent,
    this.placeRepository,
    super.key,
  });

  @override
  State<PlaceOrderScreen> createState() => _PlaceOrderScreenState();
}

class _PlaceOrderScreenState extends State<PlaceOrderScreen> {
  late final TextEditingController _quantityController;

  /// LT-12: the optional drop-off landmark (trip orders only).
  final TextEditingController _dropoffNoteController = TextEditingController();

  /// LT-14: the drop-off place (trip orders only): the field's text and the
  /// place picked from the list (null for typed text: no point is sent).
  final TextEditingController _deliveryPlaceController =
      TextEditingController();
  Place? _deliveryPlace;
  static const _commissionRate = defaultOrderCommissionRate;
  static final _kgFormat = NumberFormat('#,##0.##', 'en_US');

  /// One quintal: what the minus and plus buttons move the quantity by.
  static const double _stepKg = 100;

  /// The order was created: Place Order stays disabled from then on, also
  /// while the saved-or-sent snackbar reads the connectivity before this
  /// screen is replaced, so a second tap never creates a second order.
  bool _placed = false;

  @override
  void initState() {
    super.initState();
    // BUG-20: the quantity starts empty, with what can be ordered named
    // under the field. It used to start at the listing's whole stock, one
    // tap away from buying all of it. Only a buying request pre-fills: with
    // what the buyer said they NEED (quintals there, kg here), capped by
    // what can be ordered.
    final intent = widget.intent;
    final trip = widget.selectedTrip;
    _quantityController = TextEditingController(
      text: intent == null
          ? ''
          // Grouped as the field groups what is typed ("1,500").
          : formatGroupedNumber(
              math.min(intent.quantityNeeded * 100, _availableKg),
            ),
    );
    _loadTruckerRating();
    if (trip != null) _startFromDefaultPlace();
  }

  /// LT-14: the drop-off place starts at the intent's place, else the
  /// registered place, once the bundled list is loaded, unless the buyer
  /// typed first. Best effort: without the list the field starts empty.
  Future<void> _startFromDefaultPlace() async {
    final places = widget.placeRepository ?? PlaceRepository.instance;
    final authState = context.read<AuthBloc>().state;
    final registered = authState is AuthAuthenticated ? authState.woreda : null;
    try {
      await places.ensureLoaded();
    } on Object catch (e) {
      debugPrint('[PlaceOrder] place list unreadable: $e');
      return;
    }
    if (!mounted || _deliveryPlaceController.text.isNotEmpty) return;
    final place = defaultDeliveryPlace(
      places,
      intentPlaceId: widget.intent?.placeId,
      intentWoreda: widget.intent?.deliveryWoreda,
      registeredWoreda: registered,
    );
    if (place == null) return;
    setState(() {
      _deliveryPlace = place;
      _deliveryPlaceController.text = place.en;
    });
  }

  /// The buyer typed in the place field: a name that is no longer the
  /// picked place's drops the pick.
  void _deliveryPlaceTyped(String text) {
    final picked = _deliveryPlace;
    setState(() {
      if (picked != null && text.trim() != picked.en) _deliveryPlace = null;
    });
  }

  @override
  void dispose() {
    _quantityController.dispose();
    _dropoffNoteController.dispose();
    _deliveryPlaceController.dispose();
    super.dispose();
  }

  /// The landmark holds a phone number (or is too long): Place Order waits.
  bool get _dropoffNoteBlocks =>
      widget.selectedTrip != null &&
      dropoffNoteProblem(_dropoffNoteController.text) != null;

  double get _quantity => parseGroupedNumber(_quantityController.text) ?? 0;

  /// The trucker's average rating, ending the transport line as on the
  /// board; null while unread, offline or unrated.
  double? _truckerRating;

  Future<void> _loadTruckerRating() async {
    final truckerId = widget.selectedTrip?.truckerId;
    if (truckerId == null || truckerId.isEmpty) return;
    // ESC-31: a trip that carries the trucker's reputation copy shows it
    // from the row, with no call.
    final reputation = OwnerReputation.ofTrip(widget.selectedTrip!);
    if (reputation.isPresent) {
      _truckerRating = reputation.rating;
      return;
    }
    try {
      final summary = await context
          .read<RatingBloc>()
          .repository
          .getRatingSummary(truckerId);
      if (mounted && summary.count > 0) {
        setState(() => _truckerRating = summary.average);
      }
    } on Object {
      // No rating shown: never a placeholder.
    }
  }

  /// The listing's stock in kg (listings are quoted in quintals). The server
  /// rejects an order above it; checking here catches it offline, before a
  /// mobile order is saved and the rejection only surfaces later in sync.
  double get _stockKg => widget.listing.quantity * 100;

  /// What one order can take, in kg: the listing's stock, and with a chosen
  /// truck no more than it has room for (B-2).
  double get _availableKg {
    final trip = widget.selectedTrip;
    final roomKg = trip == null ? 0.0 : tripRoomLeft(trip) * 100;
    return roomKg > 0 && roomKg < _stockKg ? roomKg : _stockKg;
  }

  /// The quick action under the quantity: order everything available.
  void _orderAll() {
    _quantityController.text = formatGroupedNumber(_availableKg);
    setState(() {});
  }

  /// BUG-18: a typed drop-off place that was not picked from the list has
  /// no point to send, and the order would silently fall back to the truck's
  /// destination. Place Order waits until the buyer picks a place or clears
  /// the field (an empty field means the truck's destination, LT-14).
  bool get _deliveryPlaceBlocks =>
      widget.selectedTrip != null &&
      _deliveryPlace == null &&
      _deliveryPlaceController.text.trim().isNotEmpty;

  /// Same 0.001 kg slack as the server's stock check, so float noise from the
  /// quintal-to-kg conversion never blocks ordering the whole listing.
  bool get _exceedsStock => _quantity > _stockKg + 0.001;

  double get _cropCost => _quantity * widget.listing.pricePerKg;

  double get _transportCost {
    if (widget.selectedTrip == null) return 0;
    // Convert kg quantity to quintals for transport cost
    final quintals = _quantity / 100;
    return quintals * widget.selectedTrip!.pricePerQuintal;
  }

  /// The server's formula: commission on the crop cost only (SIM-53), so the
  /// preview equals what the buyer is asked to pay. Uses the same default
  /// rate as [_commissionRate]. The rows show its santim-rounded legs
  /// (SIM-30), so they add up to the total shown.
  OrderPricing get _pricing =>
      OrderPricing(cropCost: _cropCost, transportCost: _transportCost);

  double get _commission => _pricing.commissionAmount;

  double get _total => _pricing.orderTotal;

  void _step(double delta) {
    final next = (_quantity + delta).clamp(0, _stockKg).toDouble();
    _quantityController.text = formatGroupedNumber(next);
    setState(() {});
  }

  /// Close the loop from order back to the buying request: when the order
  /// covers the full quantity the request asked for, mark it fulfilled so it
  /// stops accumulating "new match" badges and server push alerts. Partial
  /// orders leave the request active — the buyer still needs the rest.
  void _fulfillIntentIfCovered(BuildContext context, AppLocalizations l10n) {
    final intent = widget.intent;
    if (intent == null) return;
    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;
    final neededKg = intent.quantityNeeded * 100;
    if (_quantity < neededKg) return;

    context.read<BuyerIntentBloc>().add(
          FulfillIntent(intentId: intent.intentId, buyerId: authState.userId),
        );
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(l10n.requestFulfilled)),
    );
  }

  /// Telemetry: record the match→order conversion (best-effort, no UI effect).
  void _logOrderCreated(BuildContext context) {
    final intent = widget.intent;
    if (intent == null) return;
    context.read<BuyerIntentBloc>().add(
          LogMatchOrderCreated(
            intent: intent,
            listingId: widget.listing.listingId,
          ),
        );
  }

  /// ESC-31: the confirm is open. Place Order does nothing meanwhile.
  bool _confirming = false;

  Future<void> _placeOrder() async {
    if (_placed || _confirming) return;
    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;

    final quantity = _quantity;
    if (quantity <= 0 ||
        _exceedsStock ||
        _dropoffNoteBlocks ||
        _deliveryPlaceBlocks) {
      return;
    }

    // B-2: the truck again, at the moment the order is placed: it may have
    // left, filled up or closed since it was chosen (a cached trip offline).
    final trip = widget.selectedTrip;
    if (trip != null) {
      final problem = tripBookingProblem(
        trip,
        now: DateTime.now(),
        quintals: quantity / 100,
      );
      if (problem != null) {
        final l10n = AppLocalizations.of(context)!;
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text(tripBookingProblemMessage(problem, l10n))),
        );
        if (problem != TripBookingProblem.full) {
          Navigator.of(context).pop(true);
        }
        return;
      }
    }

    // ESC-31: the farmer or the chosen trucker carries the Low reputation
    // label (from the cached row, so it works offline): ask before ordering.
    // No label, no dialog. The server still refuses a restricted party when
    // the order reaches it (reputation-api.md 8.2).
    _confirming = true;
    final bool confirmed;
    try {
      confirmed = await confirmLowReputationOrder(
        context,
        farmerLow: OwnerReputation.ofListing(widget.listing).isLow,
        truckerLow: trip != null && OwnerReputation.ofTrip(trip).isLow,
      );
    } finally {
      _confirming = false;
    }
    if (!confirmed || !mounted || _placed) return;

    context.read<OrderBloc>().add(
          CreateOrder(
            listingId: widget.listing.listingId,
            buyerId: authState.userId,
            farmerId: widget.listing.farmerId,
            buyerName: authState.userName,
            farmerName: widget.farmerName,
            cropType: widget.listing.cropType,
            quantity: quantity,
            pricePerUnit: widget.listing.pricePerKg,
            tripId: widget.selectedTrip?.tripId,
            truckerId: widget.selectedTrip?.truckerId,
            truckerName: widget.selectedTrip?.truckerName,
            transportCost: _transportCost > 0 ? _transportCost : null,
            commissionRate: _commissionRate,
            dropoffNote: trip == null ? null : _dropoffNoteController.text,
            deliveryPoint:
                trip == null ? null : deliveryPointOfPlace(_deliveryPlace),
          ),
        );
  }

  /// Under the quantity (PlaceOrderM board): "1,500 kg (15 quintals) · fits
  /// this trip, 1,800 kg left (18 quintals)". Kilograms lead in the role
  /// colour, the quintals follow muted; a quantity the trip has no room for
  /// reads in the error colour.
  Widget _quantityNote(BuildContext context, AppLocalizations l10n) {
    const muted = TextStyle(color: AppTheme.slate);
    // No-break spaces: the parentheses wrap whole.
    String nb(String text) => text.replaceAll(' ', '\u00A0');
    final trip = widget.selectedTrip;
    final spans = <InlineSpan>[
      TextSpan(text: kgQuantityText(_quantity, l10n)),
      TextSpan(
        text: ' ${nb(quintalsInParensText(_quantity, l10n))}',
        style: muted,
      ),
    ];
    var fits = true;
    if (trip != null) {
      final room = tripRoomLeft(trip);
      final roomKg = room * kgPerQuintal;
      fits = _quantity / kgPerQuintal <= room + 0.001;
      // A full trip has "0 kg left", not a dash.
      final roomText = kgRoomText(roomKg, l10n);
      final roomQuintals = quintalsInParensText(roomKg, l10n);
      spans.add(
        TextSpan(
          text: ' · '
              '${fits ? l10n.placeOrderFitsTripKg(roomText) : l10n.placeOrderExceedsTripKg(roomText)}',
        ),
      );
      if (roomQuintals.isNotEmpty) {
        spans.add(TextSpan(text: ' ${nb(roomQuintals)}', style: muted));
      }
    }
    return Text.rich(
      TextSpan(children: spans),
      key: const Key('place_order_quantity_note'),
      style: TextStyle(
        fontSize: 13,
        height: 1.4,
        fontWeight: FontWeight.w500,
        color: fits
            ? context.roleColors.primary
            : Theme.of(context).colorScheme.error,
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final trip = widget.selectedTrip;

    return Scaffold(
      body: BlocListener<OrderBloc, OrderState>(
        listener: (context, state) async {
          if (state is OrderCreated) {
            setState(() => _placed = true);
            // E-5: placed (sent), or saved on the phone to send later. Its
            // snackbar goes before the request's.
            await showSavedOrSent(context, sent: l10n.orderPlacedSuccess);
            if (!context.mounted) return;
            _logOrderCreated(context);
            _fulfillIntentIfCovered(context, l10n);
            Navigator.pushReplacement(
              context,
              MaterialPageRoute<void>(
                builder: (_) => PaymentInstructionsScreen(
                  order: state.order,
                ),
              ),
            );
          } else if (state is OrderError) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(localizeErrorCode(state.code, l10n))),
            );
          }
        },
        child: SafeArea(
          bottom: false,
          child: SingleChildScrollView(
            child: FwContentWidth(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // PlaceOrderM board: Back, the 32 px title, the buyer
                  // sketch at the header's end.
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 16, 20, 0),
                    child: Row(
                      children: [
                        const FwBackButton(),
                        const SizedBox(width: 12),
                        Expanded(
                          child: Semantics(
                            header: true,
                            child: Text(
                              l10n.placeOrder,
                              style: FwText.detailTitle,
                            ),
                          ),
                        ),
                        const SizedBox(width: 12),
                        const RoleSketch(role: UserRole.buyer, width: 64),
                      ],
                    ),
                  ),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 20, 20, 24),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        // ── Listing summary ────────────────────────────
                        Semantics(
                          label: l10n.listingSummary,
                          child: _PartyCard(
                            sand: true,
                            cropKey: widget.listing.cropType,
                            iconBackground:
                                cropTileColors(widget.listing.cropType)
                                    .background,
                            iconColor: cropTileColors(widget.listing.cropType)
                                .foreground,
                            title: '${CropType.labelFor(
                              widget.listing.cropType,
                              l10n,
                            )} · ${formatEtbAmount(widget.listing.pricePerKg)} '
                                '${l10n.etbPerKg}',
                            // SIM-33: no id for a name, no junk place.
                            subtitle: [
                              partyDisplayName(widget.farmerName, l10n),
                              placeDisplayName(widget.listing.locationName),
                            ].where((part) => part.isNotEmpty).join(' · '),
                          ),
                        ),
                        const SizedBox(height: 14),

                        // ── Quantity input ─────────────────────────────
                        LabeledField(
                          label: l10n.quantity,
                          field: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              _QuantityField(
                                controller: _quantityController,
                                onChanged: (_) => setState(() {}),
                                onLess: _quantity > 0
                                    ? () => _step(-_stepKg)
                                    : null,
                                onMore: _quantity + _stepKg <= _stockKg + 0.001
                                    ? () => _step(_stepKg)
                                    : null,
                                error: _exceedsStock
                                    ? l10n.orderQuantityExceedsStock(
                                        _kgFormat.format(_stockKg),
                                      )
                                    : null,
                              ),
                              const SizedBox(height: 6),
                              // BUG-20: what can be ordered, and one tap to
                              // order all of it.
                              Row(
                                children: [
                                  Expanded(
                                    child: Text(
                                      l10n.orderQuantityAvailable(
                                        _kgFormat.format(_availableKg),
                                      ),
                                      key: const Key(
                                        'place_order_available',
                                      ),
                                      style: LabeledField.helperStyle,
                                    ),
                                  ),
                                  TextButton(
                                    key: const Key('place_order_all'),
                                    onPressed: _orderAll,
                                    style: TextButton.styleFrom(
                                      foregroundColor:
                                          context.roleColors.primary,
                                      minimumSize: const Size(48, 48),
                                    ),
                                    child: Text(l10n.all),
                                  ),
                                ],
                              ),
                              // Listings and requests are quoted in quintals;
                              // show the conversion so the kg entry isn't a
                              // unit trap.
                              if (_quantity > 0) _quantityNote(context, l10n),
                            ],
                          ),
                        ),
                        const SizedBox(height: 14),

                        // ESC-15: the trucker this order will ask.
                        if (trip != null) ...[
                          _PartyCard(
                            icon: Icons.local_shipping_outlined,
                            iconBackground: AppTheme.ink,
                            iconColor: AppTheme.orange,
                            title: '${trip.truckerName} · '
                                '${boardDayInEthiopia(trip.departureDate)}',
                            // "1.50 ETB per kg (150 per quintal)".
                            subtitle: '${trip.fromWoreda} → ${trip.toWoreda} · '
                                '${l10n.transportPricePerKgAndQuintal(transportPricePerKgText(trip.pricePerQuintal), formatEtbAmount(trip.pricePerQuintal))}'
                                '${_truckerRating == null ? '' : ' · ${_truckerRating!.toStringAsFixed(1)}'}',
                            trailing: TextButton(
                              onPressed: () => Navigator.of(context).pop(),
                              style: TextButton.styleFrom(
                                foregroundColor: context.roleColors.primary,
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 8,
                                ),
                                textStyle: const TextStyle(
                                  fontFamily: AppTheme.fontFamily,
                                  fontFamilyFallback:
                                      AppTheme.ethiopicFontFallback,
                                  fontSize: 13,
                                  fontWeight: FontWeight.w800,
                                ),
                              ),
                              child: Text(l10n.detailChange),
                            ),
                          ),
                          const SizedBox(height: 14),

                          // LT-14: where the goods go.
                          DeliveryPlaceField(
                            controller: _deliveryPlaceController,
                            repository: widget.placeRepository,
                            showPickHint: _deliveryPlaceBlocks,
                            onPlaceSelected: (place) =>
                                setState(() => _deliveryPlace = place),
                            onChanged: _deliveryPlaceTyped,
                          ),
                          const SizedBox(height: 14),

                          // LT-12: where the driver drops the goods.
                          DropoffNoteField(
                            controller: _dropoffNoteController,
                            onChanged: (_) => setState(() {}),
                          ),
                          const SizedBox(height: 14),
                        ],

                        // ── Price breakdown ────────────────────────────
                        FwCard(
                          raised: true,
                          padding: const EdgeInsets.all(18),
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              Row(
                                children: [
                                  const Icon(
                                    Icons.receipt_long_outlined,
                                    size: 16,
                                    color: AppTheme.slate,
                                  ),
                                  const SizedBox(width: 8),
                                  Expanded(
                                    child: FwSectionLabel(l10n.priceBreakdown),
                                  ),
                                ],
                              ),
                              const SizedBox(height: 12),
                              FwMoneyRow(
                                label: l10n.cropCost,
                                amount: l10n.etbCurrency(
                                  formatEtbAmount(_pricing.cropCostEtb),
                                ),
                              ),
                              if (trip != null) ...[
                                const SizedBox(height: 9),
                                FwMoneyRow(
                                  label: l10n.transportCost,
                                  amount: l10n.etbCurrency(
                                    formatEtbAmount(_pricing.transportCostEtb),
                                  ),
                                ),
                              ],
                              const SizedBox(height: 9),
                              FwMoneyRow(
                                // The standard rate before the order exists;
                                // the order then carries its own.
                                label: commissionLabelFor(
                                  l10n,
                                  _commissionRate,
                                ),
                                amount: l10n
                                    .etbCurrency(formatEtbAmount(_commission)),
                              ),
                              const SizedBox(height: 11),
                              const Divider(
                                height: 1,
                                thickness: 1,
                                color: AppTheme.sketch,
                              ),
                              const SizedBox(height: 9),
                              // The total large in the role colour.
                              Row(
                                crossAxisAlignment: CrossAxisAlignment.baseline,
                                textBaseline: TextBaseline.alphabetic,
                                children: [
                                  Expanded(
                                    child: Text(
                                      l10n.orderTotal,
                                      style: const TextStyle(
                                        fontSize: 15,
                                        fontWeight: FontWeight.w800,
                                        color: AppTheme.ink,
                                      ),
                                    ),
                                  ),
                                  const SizedBox(width: 12),
                                  Text(
                                    l10n.etbCurrency(formatEtbAmount(_total)),
                                    key: const Key('place_order_total'),
                                    style: TextStyle(
                                      fontSize: 24,
                                      fontWeight: FontWeight.w800,
                                      letterSpacing: -0.7,
                                      color: context.roleColors.primary,
                                    ),
                                  ),
                                ],
                              ),
                            ],
                          ),
                        ),
                        const SizedBox(height: 18),

                        // ── Escrow trust line (PlaceOrder.dc.html) ─────
                        Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Padding(
                              padding: const EdgeInsets.only(top: 1),
                              child: Icon(
                                Icons.verified_user_outlined,
                                size: 18,
                                color: context.roleColors.primary,
                              ),
                            ),
                            const SizedBox(width: 10),
                            Expanded(
                              child: Text(
                                l10n.placeOrderEscrowNote,
                                style: const TextStyle(
                                  fontSize: 13,
                                  height: 1.5,
                                  color: AppTheme.inkSoft,
                                ),
                              ),
                            ),
                          ],
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
      // ── Place Order, pinned at the foot ────────────────────────────
      bottomNavigationBar: BlocBuilder<OrderBloc, OrderState>(
        builder: (context, state) {
          final isLoading = state is OrderLoading || _placed;
          return FwBottomActions(
            children: [
              FilledButton(
                onPressed: isLoading ||
                        _quantity <= 0 ||
                        _exceedsStock ||
                        _dropoffNoteBlocks ||
                        _deliveryPlaceBlocks
                    ? null
                    : _placeOrder,
                child: isLoading
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          color: AppTheme.ink,
                        ),
                      )
                    : Text(l10n.placeOrder),
              ),
            ],
          );
        },
      ),
    );
  }
}

/// The board's one-line card: a tinted icon tile, a title, a line under it
/// and an optional action at the end (the listing, the transport).
class _PartyCard extends StatelessWidget {
  /// The tile's icon; null draws the glyph of [cropKey].
  final IconData? icon;

  /// The listing's stored crop key, for its glyph.
  final String? cropKey;
  final Color iconBackground;
  final Color? iconColor;
  final String title;
  final String subtitle;
  final Widget? trailing;

  /// A flat sand tile (the listing) instead of the outlined row (the
  /// transport).
  final bool sand;

  const _PartyCard({
    required this.iconBackground,
    required this.title,
    required this.subtitle,
    this.icon,
    this.cropKey,
    this.iconColor,
    this.trailing,
    this.sand = false,
  });

  @override
  Widget build(BuildContext context) {
    return FwCard(
      color: sand ? AppTheme.sand : Colors.transparent,
      borderColor: sand ? null : AppTheme.fieldBorder,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      child: Row(
        children: [
          Container(
            width: 44,
            height: 44,
            decoration: BoxDecoration(
              color: iconBackground,
              borderRadius: BorderRadius.circular(14),
            ),
            child: icon == null
                ? CropGlyph(cropKey: cropKey, size: 22, color: iconColor)
                : Icon(icon, size: 22, color: iconColor),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: FwText.cardTitle.copyWith(fontSize: sand ? 17 : 15),
                ),
                const SizedBox(height: 3),
                Text(
                  subtitle,
                  style: TextStyle(
                    fontSize: 14,
                    color: sand ? AppTheme.inkSoft : AppTheme.slate,
                  ),
                ),
              ],
            ),
          ),
          if (trailing != null) ...[
            const SizedBox(width: 8),
            trailing!,
          ],
        ],
      ),
    );
  }
}

/// The board's quantity box: a 56 tall paper field with a black outline,
/// the number large, and round minus and plus buttons on sand inside it.
class _QuantityField extends StatelessWidget {
  final TextEditingController controller;
  final ValueChanged<String> onChanged;
  final VoidCallback? onLess;
  final VoidCallback? onMore;
  final String? error;

  const _QuantityField({
    required this.controller,
    required this.onChanged,
    required this.onLess,
    required this.onMore,
    required this.error,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Container(
          height: 56,
          padding: const EdgeInsetsDirectional.only(start: 16, end: 6),
          decoration: BoxDecoration(
            color: AppTheme.paper,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(
              color: error == null
                  ? AppTheme.ink
                  : Theme.of(context).colorScheme.error,
              width: 2,
            ),
          ),
          child: Row(
            children: [
              Expanded(
                child: TextField(
                  key: const Key('place_order_quantity'),
                  controller: controller,
                  keyboardType:
                      const TextInputType.numberWithOptions(decimal: true),
                  // "1,500" as on the board; [parseGroupedNumber] reads it.
                  inputFormatters: [GroupedNumberInputFormatter()],
                  style: const TextStyle(
                    fontSize: 22,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.ink,
                  ),
                  decoration: InputDecoration(
                    hintText: l10n.quantity,
                    border: InputBorder.none,
                    enabledBorder: InputBorder.none,
                    focusedBorder: InputBorder.none,
                    filled: false,
                    isDense: true,
                    contentPadding: EdgeInsets.zero,
                  ),
                  onChanged: onChanged,
                ),
              ),
              _StepButton(
                icon: Icons.remove,
                semanticLabel: l10n.detailLess,
                onPressed: onLess,
              ),
              const SizedBox(width: 8),
              _StepButton(
                icon: Icons.add,
                semanticLabel: l10n.detailMore,
                onPressed: onMore,
              ),
            ],
          ),
        ),
        if (error != null)
          Padding(
            padding: const EdgeInsets.only(top: 6),
            child: Text(
              error!,
              style: TextStyle(
                fontSize: 12,
                color: Theme.of(context).colorScheme.error,
              ),
            ),
          ),
      ],
    );
  }
}

class _StepButton extends StatelessWidget {
  final IconData icon;
  final String semanticLabel;
  final VoidCallback? onPressed;

  const _StepButton({
    required this.icon,
    required this.semanticLabel,
    required this.onPressed,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: true,
      label: semanticLabel,
      enabled: onPressed != null,
      child: Material(
        color: AppTheme.sand,
        shape: const CircleBorder(),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onPressed,
          child: SizedBox(
            width: 44,
            height: 44,
            child: Icon(
              icon,
              size: 20,
              color: onPressed == null ? AppTheme.slate : AppTheme.ink,
            ),
          ),
        ),
      ),
    );
  }
}
