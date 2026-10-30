import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/buyer_intent.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/order_pricing.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/core/utils/money_format.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';

/// Screen for buyers to place an order on a crop listing (the Buyer · Place
/// order board): the listing in one line, the quantity in kg with minus and
/// plus, the chosen transport, the price breakdown, the escrow note and
/// Place Order pinned at the foot. Works fully offline (saved to Isar,
/// synced later).
class PlaceOrderScreen extends StatefulWidget {
  final CropListing listing;
  final String farmerName;
  final Trip? selectedTrip;

  /// The buying request this order came from, when reached via match results.
  /// When set, a [BuyerIntentBloc] MUST be available above this screen: a
  /// successful order that covers the full quantity needed marks the request
  /// fulfilled so it stops generating "new match" badges and push alerts.
  final BuyerIntent? intent;

  const PlaceOrderScreen({
    required this.listing,
    required this.farmerName,
    this.selectedTrip,
    this.intent,
    super.key,
  });

  @override
  State<PlaceOrderScreen> createState() => _PlaceOrderScreenState();
}

class _PlaceOrderScreenState extends State<PlaceOrderScreen> {
  late final TextEditingController _quantityController;
  static const _commissionRate = defaultOrderCommissionRate;
  static final _kgFormat = NumberFormat('#,##0.##', 'en_US');

  /// One quintal: what the minus and plus buttons move the quantity by.
  static const double _stepKg = 100;

  @override
  void initState() {
    super.initState();
    // Pre-fill in kg (listing/intent quantities are quintals). When arriving
    // from a buying request, start from what the buyer NEEDS, capped by what
    // the listing has — not the listing's full quantity.
    final listingKg = widget.listing.quantity * 100;
    final neededKg =
        widget.intent != null ? widget.intent!.quantityNeeded * 100 : listingKg;
    final kgQuantity = neededKg < listingKg ? neededKg : listingKg;
    _quantityController =
        TextEditingController(text: kgQuantity.toStringAsFixed(0));
  }

  @override
  void dispose() {
    _quantityController.dispose();
    super.dispose();
  }

  double get _quantity => double.tryParse(_quantityController.text) ?? 0;

  /// The listing's stock in kg (listings are quoted in quintals). The server
  /// rejects an order above it; checking here catches it offline, before a
  /// mobile order is saved and the rejection only surfaces later in sync.
  double get _stockKg => widget.listing.quantity * 100;

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
  /// rate as [_commissionRate].
  OrderPricing get _pricing =>
      OrderPricing(cropCost: _cropCost, transportCost: _transportCost);

  double get _commission => _pricing.commissionAmount;

  double get _total => _pricing.orderTotal;

  void _step(double delta) {
    final next = (_quantity + delta).clamp(0, _stockKg).toDouble();
    _quantityController.text = next.toStringAsFixed(0);
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

  void _placeOrder() {
    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;

    final quantity = _quantity;
    if (quantity <= 0 || _exceedsStock) return;

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
          ),
        );
  }

  /// Under the quantity: the quintals, and whether the chosen trip has room
  /// for them (the room left either way).
  String _quantityNote(AppLocalizations l10n) {
    final quintals =
        l10n.quintalEquivalent((_quantity / 100).toStringAsFixed(1));
    final trip = widget.selectedTrip;
    if (trip == null) return quintals;
    final room = tripRoomLeft(trip);
    final fits = _quantity / 100 <= room + 0.001;
    final roomText = formatQuintals(room);
    return '$quintals · '
        '${fits ? l10n.detailFitsTrip(roomText) : l10n.detailExceedsTrip(roomText)}';
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final trip = widget.selectedTrip;

    return Scaffold(
      body: BlocListener<OrderBloc, OrderState>(
        listener: (context, state) {
          if (state is OrderCreated) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(l10n.orderPlacedSuccess)),
            );
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
                  FwDetailHeader(title: l10n.placeOrder),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        // ── Listing summary ────────────────────────────
                        Semantics(
                          label: l10n.listingSummary,
                          child: _PartyCard(
                            icon: Icons.grass,
                            iconBackground: const Color(0xFFF4EBCF),
                            title: '${CropType.labelFor(
                              widget.listing.cropType,
                              l10n,
                            )} · ${etbAmount(widget.listing.pricePerKg)} '
                                '${l10n.etbPerKg}',
                            subtitle: '${widget.farmerName} · '
                                '${widget.listing.locationName}',
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
                              // Listings and requests are quoted in quintals;
                              // show the conversion so the kg entry isn't a
                              // unit trap.
                              Text(
                                _quantityNote(l10n),
                                style: LabeledField.helperStyle,
                              ),
                            ],
                          ),
                        ),
                        const SizedBox(height: 14),

                        // ESC-15: the trucker this order will ask.
                        if (trip != null) ...[
                          _PartyCard(
                            icon: Icons.local_shipping_outlined,
                            iconBackground: AppTheme.actionCream,
                            title: '${trip.truckerName} · '
                                '${DateFormat.MMMEd().format(dayInEthiopia(trip.departureDate))}',
                            subtitle: '${trip.fromWoreda} → ${trip.toWoreda} · '
                                '${l10n.tripPricePerQuintalValue(trip.pricePerQuintal.toStringAsFixed(0))}',
                            trailing: TextButton(
                              onPressed: () => Navigator.of(context).pop(),
                              style: TextButton.styleFrom(
                                foregroundColor: AppTheme.money,
                                padding: const EdgeInsets.symmetric(
                                  horizontal: 8,
                                ),
                                textStyle: const TextStyle(
                                  fontSize: 13,
                                  fontWeight: FontWeight.w700,
                                ),
                              ),
                              child: Text(l10n.detailChange),
                            ),
                          ),
                          const SizedBox(height: 14),
                        ],

                        // ── Price breakdown ────────────────────────────
                        FwCard(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.stretch,
                            children: [
                              Text(
                                l10n.priceBreakdown,
                                style: const TextStyle(
                                  fontSize: 15,
                                  fontWeight: FontWeight.w800,
                                  color: AppTheme.soilDark,
                                ),
                              ),
                              const SizedBox(height: 11),
                              FwMoneyRow(
                                label: l10n.cropCost,
                                amount: l10n.etbCurrency(etbAmount(_cropCost)),
                              ),
                              if (trip != null) ...[
                                const SizedBox(height: 9),
                                FwMoneyRow(
                                  label: l10n.transportCost,
                                  amount: l10n.etbCurrency(
                                    etbAmount(_transportCost),
                                  ),
                                ),
                              ],
                              const SizedBox(height: 9),
                              FwMoneyRow(
                                label: l10n.platformCommission,
                                amount:
                                    l10n.etbCurrency(etbAmount(_commission)),
                              ),
                              const SizedBox(height: 9),
                              FwMoneyRow(
                                label: l10n.orderTotal,
                                amount: l10n.etbCurrency(etbAmount(_total)),
                                total: true,
                              ),
                            ],
                          ),
                        ),
                        const SizedBox(height: 14),

                        // ── Escrow trust line (PlaceOrder.dc.html) ─────
                        Row(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            const Padding(
                              padding: EdgeInsets.only(top: 2),
                              child: Icon(
                                Icons.lock_outline,
                                size: 16,
                                color: AppTheme.unselectedNav,
                              ),
                            ),
                            const SizedBox(width: 10),
                            Expanded(
                              child: Text(
                                l10n.placeOrderEscrowNote,
                                style: const TextStyle(
                                  fontSize: 13,
                                  height: 1.5,
                                  color: AppTheme.unselectedNav,
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
          final isLoading = state is OrderLoading;
          return FwBottomActions(
            children: [
              FilledButton(
                onPressed: isLoading || _quantity <= 0 || _exceedsStock
                    ? null
                    : _placeOrder,
                style: FilledButton.styleFrom(
                  minimumSize: const Size.fromHeight(56),
                  textStyle: const TextStyle(
                    fontSize: 16,
                    fontWeight: FontWeight.w800,
                  ),
                ),
                child: isLoading
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(
                          strokeWidth: 2,
                          color: Colors.white,
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
  final IconData icon;
  final Color iconBackground;
  final String title;
  final String subtitle;
  final Widget? trailing;

  const _PartyCard({
    required this.icon,
    required this.iconBackground,
    required this.title,
    required this.subtitle,
    this.trailing,
  });

  @override
  Widget build(BuildContext context) {
    return FwCard(
      padding: const EdgeInsets.all(14),
      child: Row(
        children: [
          Container(
            width: 44,
            height: 44,
            decoration: BoxDecoration(
              color: iconBackground,
              borderRadius: BorderRadius.circular(13),
            ),
            child: Icon(icon, size: 22, color: AppTheme.money),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  title,
                  style: const TextStyle(
                    fontSize: 15,
                    fontWeight: FontWeight.w700,
                    color: AppTheme.soilDark,
                  ),
                ),
                const SizedBox(height: 2),
                Text(
                  subtitle,
                  style: const TextStyle(
                    fontSize: 12,
                    color: AppTheme.mutedSteel,
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

/// The board's quantity box: a 56 tall field with a green outline, the
/// number large, and minus and plus squares inside it.
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
            color: AppTheme.surfaceWhite,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(
              color: error == null
                  ? AppTheme.fieldGreen
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
                  inputFormatters: [
                    FilteringTextInputFormatter.allow(
                      RegExp(r'^\d*\.?\d{0,2}'),
                    ),
                  ],
                  style: const TextStyle(
                    fontSize: 22,
                    fontWeight: FontWeight.w800,
                    color: AppTheme.soilDark,
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
              const SizedBox(width: 6),
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
        color: AppTheme.chalkBackground,
        borderRadius: BorderRadius.circular(12),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onPressed,
          child: SizedBox(
            width: 42,
            height: 42,
            child: Icon(
              icon,
              size: 20,
              color:
                  onPressed == null ? AppTheme.mutedSteel : AppTheme.soilDark,
            ),
          ),
        ),
      ),
    );
  }
}
