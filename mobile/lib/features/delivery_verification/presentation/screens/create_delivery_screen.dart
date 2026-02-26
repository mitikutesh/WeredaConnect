import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/core/database/schemas/crop_listing.dart';
import 'package:wereda_connect/core/database/schemas/trip.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/delivery_error_l10n.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Screen for creating a new Delivery record.
///
/// A trucker uses this screen to link a crop listing to one of their
/// open trips, entering buyer information to complete the match.
/// This is the critical "matching" flow:
///   CropListing (farmer) + Trip (trucker) -> Delivery
///
/// Form patterns follow create_crop_listing_screen.dart:
/// - `GlobalKey<FormState>` for validation
/// - TextFormField with OutlineInputBorder and content padding
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

    // In mock auth mode, convert phone-format buyer ID to mock userId format
    // so delivery queries match the buyer's authenticated userId.
    var buyerId = _buyerIdController.text.trim();
    if (Environment.useMockAuth && buyerId.startsWith('+')) {
      buyerId = 'mock_${buyerId.replaceAll('+', '')}';
    }

    context.read<DeliveryBloc>().add(
          CreateDelivery(
            tripId: trip.tripId,
            listingId: widget.listing.listingId,
            farmerId: widget.listing.farmerId,
            farmerName: widget.listing.farmerId, // farmerId used as fallback
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
    final theme = Theme.of(context);

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
              backgroundColor: Colors.red,
            ),
          );
        }
      },
      child: Scaffold(
        appBar: AppBar(
          title: Text(l10n.createDelivery),
          elevation: 0,
        ),
        body: SafeArea(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24.0),
            child: Form(
              key: _formKey,
              child: BlocBuilder<DeliveryBloc, DeliveryState>(
                builder: (context, state) {
                  final isLoading = state is DeliveryLoading;

                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      // -- Listing summary card --
                      _ListingSummaryCard(listing: widget.listing),
                      const SizedBox(height: 24),

                      // -- Trip selection --
                      Text(
                        l10n.selectTrip,
                        style: theme.textTheme.titleMedium,
                      ),
                      const SizedBox(height: 12),
                      _buildTripSelector(theme),
                      const SizedBox(height: 24),

                      // -- Buyer info section --
                      Text(
                        l10n.buyerInformation,
                        style: theme.textTheme.titleMedium,
                      ),
                      const SizedBox(height: 12),

                      // Buyer name
                      TextFormField(
                        controller: _buyerNameController,
                        decoration: InputDecoration(
                          labelText: l10n.buyerNameLabel,
                          prefixIcon: const Icon(Icons.person),
                          border: const OutlineInputBorder(),
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 16,
                            vertical: 20,
                          ),
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
                      const SizedBox(height: 16),

                      // Buyer ID / phone
                      TextFormField(
                        controller: _buyerIdController,
                        decoration: InputDecoration(
                          labelText: l10n.buyerPhoneIdLabel,
                          prefixIcon: const Icon(Icons.badge),
                          hintText: '+251...',
                          border: const OutlineInputBorder(),
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 16,
                            vertical: 20,
                          ),
                        ),
                        keyboardType: TextInputType.phone,
                        validator: (value) {
                          if (value == null || value.trim().isEmpty) {
                            return l10n.buyerIdRequired;
                          }
                          return null;
                        },
                      ),
                      const SizedBox(height: 32),

                      // -- Submit button --
                      ElevatedButton(
                        onPressed: isLoading ? null : _handleSubmit,
                        style: ElevatedButton.styleFrom(
                          padding: const EdgeInsets.symmetric(vertical: 16),
                          minimumSize: const Size.fromHeight(48),
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(8),
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
                            : Text(
                                l10n.save,
                                style: const TextStyle(fontSize: 16),
                              ),
                      ),
                    ],
                  );
                },
              ),
            ),
          ),
        ),
      ),
    );
  }

  /// Build the trip selection dropdown or loading/empty state.
  Widget _buildTripSelector(ThemeData theme) {
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
      return Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          border: Border.all(color: Colors.orange.shade300),
          borderRadius: BorderRadius.circular(8),
          color: Colors.orange.shade50,
        ),
        child: Row(
          children: [
            Icon(Icons.warning_amber, color: Colors.orange.shade700),
            const SizedBox(width: 12),
            Expanded(
              child: Text(
                AppLocalizations.of(context)!.noOpenTrips,
                style: TextStyle(color: Colors.orange.shade800),
              ),
            ),
          ],
        ),
      );
    }

    final dateFormat = DateFormat.yMMMd();

    return DropdownButtonFormField<Trip>(
      initialValue: _selectedTrip,
      decoration: const InputDecoration(
        prefixIcon: Icon(Icons.local_shipping),
        border: OutlineInputBorder(),
        contentPadding: EdgeInsets.symmetric(
          horizontal: 16,
          vertical: 20,
        ),
      ),
      hint: Text(AppLocalizations.of(context)!.chooseTripHint),
      isExpanded: true,
      items: _openTrips.map((trip) {
        return DropdownMenuItem<Trip>(
          value: trip,
          child: Text(
            '${trip.fromWoreda} -> ${trip.toWoreda} '
            '(${dateFormat.format(trip.departureDate)})',
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

/// Summary card showing listing details at the top of the screen.
///
/// Displays crop type icon, quantity, price, location, and farmer ID
/// so the trucker can confirm they are creating a delivery for the
/// correct listing.
class _ListingSummaryCard extends StatelessWidget {
  final CropListing listing;

  const _ListingSummaryCard({required this.listing});

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final cropType = CropType.fromKey(listing.cropType);

    return Card(
      elevation: 2,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Header with crop icon and type
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(8),
                  decoration: BoxDecoration(
                    color: theme.colorScheme.primary.withValues(alpha: 0.1),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: Icon(
                    cropType.icon,
                    size: 28,
                    color: theme.colorScheme.primary,
                  ),
                ),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    cropType.displayName(l10n),
                    style: theme.textTheme.titleMedium?.copyWith(
                      fontWeight: FontWeight.bold,
                    ),
                  ),
                ),
              ],
            ),
            const SizedBox(height: 12),
            const Divider(),
            const SizedBox(height: 8),

            // Quantity
            _SummaryRow(
              icon: Icons.scale,
              label: l10n.quantityInQuintals,
              value: '${listing.quantity} ${l10n.quintals}',
            ),
            const SizedBox(height: 8),

            // Price
            _SummaryRow(
              icon: Icons.payments,
              label: l10n.priceInBirr,
              value: '${listing.pricePerKg} ${l10n.etbPerKg}',
            ),
            const SizedBox(height: 8),

            // Location
            _SummaryRow(
              icon: Icons.location_on,
              label: l10n.location,
              value: listing.locationName,
            ),
            const SizedBox(height: 8),

            // Farmer ID
            _SummaryRow(
              icon: Icons.person,
              label: l10n.deliveryFarmer,
              value: listing.farmerId,
            ),
          ],
        ),
      ),
    );
  }
}

/// A single info row within the listing summary card.
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
        Icon(icon, size: 18, color: Colors.grey.shade600),
        const SizedBox(width: 8),
        Text(
          '$label: ',
          style: theme.textTheme.bodySmall?.copyWith(
            color: Colors.grey.shade600,
          ),
        ),
        Expanded(
          child: Text(
            value,
            style: theme.textTheme.bodyMedium,
            overflow: TextOverflow.ellipsis,
          ),
        ),
      ],
    );
  }
}
