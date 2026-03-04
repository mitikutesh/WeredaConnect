import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_bloc.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_event.dart';
import 'package:wereda_connect/features/order/presentation/bloc/order_state.dart';
import 'package:wereda_connect/features/order/presentation/screens/payment_instructions_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Screen for buyers to place an order on a crop listing.
///
/// Shows listing summary, quantity input, price breakdown, and
/// a Place Order button. Works fully offline (saved to Isar, synced later).
class PlaceOrderScreen extends StatefulWidget {
  final CropListing listing;
  final String farmerName;
  final Trip? selectedTrip;

  const PlaceOrderScreen({
    required this.listing,
    required this.farmerName,
    this.selectedTrip,
    super.key,
  });

  @override
  State<PlaceOrderScreen> createState() => _PlaceOrderScreenState();
}

class _PlaceOrderScreenState extends State<PlaceOrderScreen> {
  late final TextEditingController _quantityController;
  static const _commissionRate = 0.05;

  @override
  void initState() {
    super.initState();
    // Pre-fill with listing quantity (convert quintals to kg)
    final kgQuantity = widget.listing.quantity * 100;
    _quantityController =
        TextEditingController(text: kgQuantity.toStringAsFixed(0));
  }

  @override
  void dispose() {
    _quantityController.dispose();
    super.dispose();
  }

  double get _quantity => double.tryParse(_quantityController.text) ?? 0;

  double get _cropCost => _quantity * widget.listing.pricePerKg;

  double get _transportCost {
    if (widget.selectedTrip == null) return 0;
    // Convert kg quantity to quintals for transport cost
    final quintals = _quantity / 100;
    return quintals * widget.selectedTrip!.pricePerQuintal;
  }

  double get _commission => (_cropCost + _transportCost) * _commissionRate;

  double get _total => _cropCost + _transportCost + _commission;

  void _placeOrder() {
    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;

    final quantity = _quantity;
    if (quantity <= 0) return;

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

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(title: Text(l10n.placeOrder)),
      body: BlocListener<OrderBloc, OrderState>(
        listener: (context, state) {
          if (state is OrderCreated) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(l10n.orderPlacedSuccess)),
            );
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
              SnackBar(content: Text(state.message)),
            );
          }
        },
        child: SafeArea(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // ── Listing summary ──────────────────────────────────
                Text(
                  l10n.listingSummary,
                  style: theme.textTheme.titleMedium
                      ?.copyWith(fontWeight: FontWeight.bold),
                ),
                const SizedBox(height: 12),
                Card(
                  margin: EdgeInsets.zero,
                  child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: Column(
                      children: [
                        _SummaryRow(
                          icon: Icons.grass,
                          label: widget.listing.cropType,
                          value: '',
                        ),
                        const Divider(height: 16),
                        _SummaryRow(
                          icon: Icons.person,
                          label: l10n.farmer,
                          value: widget.farmerName,
                        ),
                        const Divider(height: 16),
                        _SummaryRow(
                          icon: Icons.payments,
                          label: l10n.etbCurrency(''),
                          value:
                              '${widget.listing.pricePerKg.toStringAsFixed(2)} ETB/kg',
                        ),
                        const Divider(height: 16),
                        _SummaryRow(
                          icon: Icons.location_on,
                          label: l10n.location,
                          value: widget.listing.locationName,
                        ),
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 24),

                // ── Quantity input ────────────────────────────────────
                Text(
                  l10n.quantity,
                  style: theme.textTheme.titleMedium
                      ?.copyWith(fontWeight: FontWeight.bold),
                ),
                const SizedBox(height: 8),
                TextField(
                  controller: _quantityController,
                  keyboardType:
                      const TextInputType.numberWithOptions(decimal: true),
                  decoration: InputDecoration(
                    suffixText: 'kg',
                    border: const OutlineInputBorder(),
                    hintText: l10n.quantity,
                  ),
                  onChanged: (_) => setState(() {}),
                ),
                const SizedBox(height: 24),

                // ── Price breakdown ──────────────────────────────────
                Text(
                  l10n.priceBreakdown,
                  style: theme.textTheme.titleMedium
                      ?.copyWith(fontWeight: FontWeight.bold),
                ),
                const SizedBox(height: 12),
                Card(
                  margin: EdgeInsets.zero,
                  child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: Column(
                      children: [
                        _PriceRow(
                          label: l10n.cropCost,
                          amount: _cropCost,
                          l10n: l10n,
                        ),
                        if (widget.selectedTrip != null) ...[
                          const Divider(height: 16),
                          _PriceRow(
                            label: l10n.transportCost,
                            amount: _transportCost,
                            l10n: l10n,
                          ),
                        ],
                        const Divider(height: 16),
                        _PriceRow(
                          label: l10n.platformCommission,
                          amount: _commission,
                          l10n: l10n,
                        ),
                        const Divider(height: 16),
                        _PriceRow(
                          label: l10n.orderTotal,
                          amount: _total,
                          l10n: l10n,
                          isBold: true,
                        ),
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 32),

                // ── Place Order button ───────────────────────────────
                BlocBuilder<OrderBloc, OrderState>(
                  builder: (context, state) {
                    final isLoading = state is OrderLoading;
                    return SizedBox(
                      width: double.infinity,
                      child: FilledButton(
                        onPressed:
                            isLoading || _quantity <= 0 ? null : _placeOrder,
                        style: FilledButton.styleFrom(
                          minimumSize: const Size.fromHeight(48),
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
                    );
                  },
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _SummaryRow extends StatelessWidget {
  final IconData icon;
  final String label;
  final String value;

  const _SummaryRow({
    required this.icon,
    required this.label,
    required this.value,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Row(
      children: [
        Icon(icon, size: 20, color: theme.colorScheme.primary),
        const SizedBox(width: 12),
        Expanded(
          child: Text(label, style: theme.textTheme.bodyMedium),
        ),
        if (value.isNotEmpty)
          Text(
            value,
            style: theme.textTheme.bodyMedium
                ?.copyWith(fontWeight: FontWeight.w500),
          ),
      ],
    );
  }
}

class _PriceRow extends StatelessWidget {
  final String label;
  final double amount;
  final AppLocalizations l10n;
  final bool isBold;

  const _PriceRow({
    required this.label,
    required this.amount,
    required this.l10n,
    this.isBold = false,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final style = isBold
        ? theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold)
        : theme.textTheme.bodyMedium;

    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceBetween,
      children: [
        Text(label, style: style),
        Text(
          l10n.etbCurrency(amount.toStringAsFixed(2)),
          style: style,
        ),
      ],
    );
  }
}
