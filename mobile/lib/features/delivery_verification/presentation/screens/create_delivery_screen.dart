import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/utils/ethiopia_time.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/delivery_error_l10n.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// Screen for creating a new Delivery record (a sibling of the Place order
/// board: the listing summary card, the trip and the buyer under section
/// labels, Save pinned at the foot).
///
/// A trucker uses this screen to link a crop listing to one of their
/// open trips, entering buyer information to complete the match.
/// This is the critical "matching" flow:
///   CropListing (farmer) + Trip (trucker) -> Delivery
///
/// Form patterns follow create_crop_listing_screen.dart:
/// - `GlobalKey<FormState>` for validation
/// - Inputs styled by the theme (radius 14)
/// - Large touch targets (48dp)
/// - BlocListener for state changes (success -> pop, error -> SnackBar)
/// - Loading spinner on submit button while processing
class CreateDeliveryScreen extends StatefulWidget {
  /// The crop listing this delivery is for.
  final CropListing listing;

  const CreateDeliveryScreen({required this.listing, super.key});

  @override
  State<CreateDeliveryScreen> createState() => _CreateDeliveryScreenState();
}

class _CreateDeliveryScreenState extends State<CreateDeliveryScreen> {
  final _formKey = GlobalKey<FormState>();
  final _buyerNameController = TextEditingController();
  final _buyerIdController = TextEditingController();

  List<Trip> _openTrips = [];
  Trip? _selectedTrip;
  bool _loadingTrips = true;

  @override
  void initState() {
    super.initState();
    _loadOpenTrips();
  }

  /// Load the current trucker's open trips from the repository.
  Future<void> _loadOpenTrips() async {
    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;

    try {
      final repository = context.read<TripListingBloc>().repository;
      final allTrips = await repository.getTripsByTrucker(authState.userId);
      final openTrips =
          allTrips.where((t) => t.status == TripStatus.open).toList();

      if (!mounted) return;
      setState(() {
        _openTrips = openTrips;
        _loadingTrips = false;
      });
    } on Exception {
      if (!mounted) return;
      setState(() {
        _loadingTrips = false;
      });
    }
  }

  @override
  void dispose() {
    _buyerNameController.dispose();
    _buyerIdController.dispose();
    super.dispose();
  }

  void _handleSubmit() {
    if (!_formKey.currentState!.validate()) return;

    if (_selectedTrip == null) {
      final l10n = AppLocalizations.of(context)!;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(l10n.tripSelectionRequired)),
      );
      return;
    }

    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) {
      final l10n = AppLocalizations.of(context)!;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(l10n.notAuthenticated)),
      );
      return;
    }

    final trip = _selectedTrip!;
    final qrToken = const Uuid().v4();

    final buyerId = _buyerIdController.text.trim();

    context.read<DeliveryBloc>().add(
          CreateDelivery(
            tripId: trip.tripId,
            // Legacy screen (no live callers) predating order-linked deliveries:
            // it has no order context, and the server now rejects order-less
            // creates (DELIVERY_ORDER_REQUIRED). Live creates come from the
            // order detail screen, which passes the real orderId.
            orderId: '',
            listingId: widget.listing.listingId,
            farmerId: widget.listing.farmerId,
            farmerName: widget.listing.farmerName.isNotEmpty
                ? widget.listing.farmerName
                : widget.listing.farmerId, // fall back to id only if unnamed
            truckerId: authState.userId,
            truckerName: trip.truckerName,
            buyerId: buyerId,
            buyerName: _buyerNameController.text.trim(),
            qrToken: qrToken,
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<DeliveryBloc, DeliveryState>(
      listener: (context, state) {
        if (state is DeliveryCreated) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content:
                  Text(AppLocalizations.of(context)!.deliveryCreatedSuccess),
            ),
          );
          Navigator.of(context).pop();
        } else if (state is DeliveryError) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                localizeDeliveryError(
                  AppLocalizations.of(context)!,
                  state.code,
                ),
              ),
              backgroundColor: AppTheme.statusError,
            ),
          );
        }
      },
      child: Scaffold(
        body: SafeArea(
          bottom: false,
          child: SingleChildScrollView(
            child: FwContentWidth(
              child: Form(
                key: _formKey,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    FwDetailHeader(title: l10n.createDelivery),
                    Padding(
                      padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: [
                          // -- Listing summary card --
                          _ListingSummaryCard(listing: widget.listing),
                          const SizedBox(height: 20),

                          // -- Trip selection --
                          FwSectionLabel(
                            l10n.selectTrip,
                            padding: const EdgeInsets.only(bottom: 8),
                          ),
                          _buildTripSelector(),
                          const SizedBox(height: 20),

                          // -- Buyer info section --
                          FwSectionLabel(
                            l10n.buyerInformation,
                            padding: const EdgeInsets.only(bottom: 8),
                          ),

                          // Buyer name
                          TextFormField(
                            controller: _buyerNameController,
                            decoration: InputDecoration(
                              labelText: l10n.buyerNameLabel,
                              prefixIcon: const Icon(Icons.person),
                            ),
                            keyboardType: TextInputType.text,
                            textCapitalization: TextCapitalization.words,
                            validator: (value) {
                              if (value == null || value.trim().isEmpty) {
                                return l10n.buyerNameRequired;
                              }
                              return null;
                            },
                          ),
                          const SizedBox(height: 12),

                          // Buyer ID / phone
                          TextFormField(
                            controller: _buyerIdController,
                            decoration: InputDecoration(
                              labelText: l10n.buyerPhoneIdLabel,
                              prefixIcon: const Icon(Icons.badge),
                              hintText: '09XXXXXXXX',
                            ),
                            keyboardType: TextInputType.phone,
                            validator: (value) {
                              if (value == null || value.trim().isEmpty) {
                                return l10n.buyerIdRequired;
                              }
                              return null;
                            },
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
        // -- Save, pinned at the foot --
        bottomNavigationBar: BlocBuilder<DeliveryBloc, DeliveryState>(
          builder: (context, state) {
            final isLoading = state is DeliveryLoading;
            return FwBottomActions(
              children: [
                FilledButton(
                  onPressed: isLoading ? null : _handleSubmit,
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
                            valueColor: AlwaysStoppedAnimation<Color>(
                              Colors.white,
                            ),
                          ),
                        )
                      : Text(l10n.save),
                ),
              ],
            );
          },
        ),
      ),
    );
  }

  /// Build the trip selection dropdown or loading/empty state.
  Widget _buildTripSelector() {
    if (_loadingTrips) {
      return const Padding(
        padding: EdgeInsets.symmetric(vertical: 16),
        child: Center(
          child: SizedBox(
            height: 24,
            width: 24,
            child: CircularProgressIndicator(strokeWidth: 2),
          ),
        ),
      );
    }

    if (_openTrips.isEmpty) {
      return FwNoteBox.cream(
        icon: Icons.warning_amber,
        child: Text(AppLocalizations.of(context)!.noOpenTrips),
      );
    }

    final dateFormat = DateFormat.yMMMd();

    return DropdownButtonFormField<Trip>(
      initialValue: _selectedTrip,
      decoration: const InputDecoration(
        prefixIcon: Icon(Icons.local_shipping),
      ),
      hint: Text(AppLocalizations.of(context)!.chooseTripHint),
      isExpanded: true,
      items: _openTrips.map((trip) {
        return DropdownMenuItem<Trip>(
          value: trip,
          child: Text(
            '${trip.fromWoreda} -> ${trip.toWoreda} '
            '(${dateFormat.format(dayInEthiopia(trip.departureDate))})',
            overflow: TextOverflow.ellipsis,
          ),
        );
      }).toList(),
      onChanged: (trip) {
        setState(() {
          _selectedTrip = trip;
        });
      },
      validator: (value) {
        if (value == null) {
          return AppLocalizations.of(context)!.tripSelectionRequired;
        }
        return null;
      },
    );
  }
}

/// Summary card showing listing details at the top of the screen (the
/// Place order board's listing summary): the crop tile and name with the
/// place, then quantity, price and farmer as fact rows.
///
/// Lets the trucker confirm they are creating a delivery for the correct
/// listing.
class _ListingSummaryCard extends StatelessWidget {
  final CropListing listing;

  const _ListingSummaryCard({required this.listing});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final cropType = CropType.fromKey(listing.cropType);

    return FwCard(
      padding: const EdgeInsets.all(14),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              DeliveryCropTile(
                cropKey: listing.cropType,
                status: DeliveryStatus.pending,
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      cropType.displayName(l10n),
                      style: const TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w700,
                        color: AppTheme.soilDark,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      listing.locationName,
                      style: const TextStyle(
                        fontSize: 12,
                        color: AppTheme.mutedSteel,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
          const SizedBox(height: 12),
          const Divider(height: 1, color: AppTheme.segmentTrack),
          const SizedBox(height: 12),
          _SummaryRow(
            label: l10n.quantityInQuintals,
            value: '${listing.quantity} ${l10n.quintals}',
          ),
          const SizedBox(height: 8),
          _SummaryRow(
            label: l10n.priceInBirr,
            value: '${listing.pricePerKg} ${l10n.etbPerKg}',
          ),
          const SizedBox(height: 8),
          _SummaryRow(
            label: l10n.deliveryFarmer,
            value: listing.farmerId,
          ),
        ],
      ),
    );
  }
}

/// A fact of the listing: the label left, the value bold right.
class _SummaryRow extends StatelessWidget {
  final String label;
  final String value;

  const _SummaryRow({
    required this.label,
    required this.value,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          label,
          style: const TextStyle(fontSize: 13, color: AppTheme.mutedSteel),
        ),
        const SizedBox(width: 12),
        Expanded(
          child: Text(
            value,
            textAlign: TextAlign.end,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(
              fontSize: 14,
              fontWeight: FontWeight.w700,
              color: AppTheme.soilDark,
            ),
          ),
        ),
      ],
    );
  }
}
