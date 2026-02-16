import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Screen for creating a new trip listing.
///
/// Form-based UI following CreateCropListingScreen patterns:
/// - Large touch targets (48dp min)
/// - Localized labels and validation
/// - BlocListener for state transitions
/// - Loading spinner on submit
class CreateTripScreen extends StatefulWidget {
  final String truckerName;
  final String truckerPhone;

  const CreateTripScreen({
    required this.truckerName,
    required this.truckerPhone,
    super.key,
  });

  @override
  State<CreateTripScreen> createState() => _CreateTripScreenState();
}

class _CreateTripScreenState extends State<CreateTripScreen> {
  final _formKey = GlobalKey<FormState>();
  final _fromWoredaController = TextEditingController();
  final _toWoredaController = TextEditingController();
  final _capacityController = TextEditingController();
  final _priceController = TextEditingController();

  DateTime? _selectedDepartureDate;

  @override
  void dispose() {
    _fromWoredaController.dispose();
    _toWoredaController.dispose();
    _capacityController.dispose();
    _priceController.dispose();
    super.dispose();
  }

  Future<void> _pickDepartureDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _selectedDepartureDate ?? now.add(const Duration(days: 1)),
      firstDate: now,
      lastDate: now.add(const Duration(days: 30)),
    );
    if (picked != null) {
      setState(() {
        _selectedDepartureDate = picked;
      });
    }
  }

  void _handleSave() {
    if (!_formKey.currentState!.validate()) return;

    final l10n = AppLocalizations.of(context)!;

    if (_selectedDepartureDate == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(l10n.departureDateRequired)),
      );
      return;
    }

    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(l10n.notAuthenticated)),
      );
      return;
    }

    context.read<TripListingBloc>().add(
          CreateTrip(
            truckerId: authState.userId,
            truckerName: widget.truckerName,
            truckerPhone: widget.truckerPhone,
            fromWoreda: _fromWoredaController.text.trim(),
            toWoreda: _toWoredaController.text.trim(),
            departureDate: _selectedDepartureDate!,
            capacity: double.parse(_capacityController.text),
            pricePerQuintal: double.parse(_priceController.text),
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<TripListingBloc, TripListingState>(
      listener: (context, state) {
        if (state is TripCreated) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.tripCreatedSuccess)),
          );
          Navigator.of(context).pop();
        } else if (state is TripListingError) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(state.message),
              backgroundColor: Colors.red,
            ),
          );
        }
      },
      child: Scaffold(
        appBar: AppBar(
          title: Text(l10n.createTrip),
          elevation: 0,
        ),
        body: SafeArea(
          child: Column(
            children: [
              const TripOfflineBanner(),
              Expanded(
                child: SingleChildScrollView(
                  padding: const EdgeInsets.all(24.0),
                  child: Form(
                    key: _formKey,
                    child: BlocBuilder<TripListingBloc, TripListingState>(
                      builder: (context, state) {
                        final isLoading = state is TripListingLoading;

                        return Column(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            // From Woreda input
                            TextFormField(
                              controller: _fromWoredaController,
                              decoration: InputDecoration(
                                labelText: l10n.fromWoreda,
                                prefixIcon:
                                    const Icon(Icons.trip_origin),
                                border: const OutlineInputBorder(),
                                contentPadding:
                                    const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 20,
                                ),
                              ),
                              keyboardType: TextInputType.text,
                              textCapitalization:
                                  TextCapitalization.words,
                              validator: (value) {
                                if (value == null ||
                                    value.trim().isEmpty) {
                                  return l10n.fromWoredaRequired;
                                }
                                return null;
                              },
                            ),
                            const SizedBox(height: 16),

                            // To Woreda input
                            TextFormField(
                              controller: _toWoredaController,
                              decoration: InputDecoration(
                                labelText: l10n.toWoreda,
                                prefixIcon: const Icon(Icons.place),
                                border: const OutlineInputBorder(),
                                contentPadding:
                                    const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 20,
                                ),
                              ),
                              keyboardType: TextInputType.text,
                              textCapitalization:
                                  TextCapitalization.words,
                              validator: (value) {
                                if (value == null ||
                                    value.trim().isEmpty) {
                                  return l10n.toWoredaRequired;
                                }
                                if (value.trim() ==
                                    _fromWoredaController.text
                                        .trim()) {
                                  return l10n.sameOriginDestination;
                                }
                                return null;
                              },
                            ),
                            const SizedBox(height: 16),

                            // Departure date picker
                            InkWell(
                              onTap: _pickDepartureDate,
                              child: InputDecorator(
                                decoration: InputDecoration(
                                  labelText: l10n.departureDate,
                                  prefixIcon: const Icon(
                                      Icons.calendar_today),
                                  border: const OutlineInputBorder(),
                                  contentPadding:
                                      const EdgeInsets.symmetric(
                                    horizontal: 16,
                                    vertical: 20,
                                  ),
                                ),
                                child: Text(
                                  _selectedDepartureDate != null
                                      ? DateFormat.yMMMd().format(
                                          _selectedDepartureDate!)
                                      : l10n.selectDepartureDate,
                                  style: TextStyle(
                                    color:
                                        _selectedDepartureDate != null
                                            ? null
                                            : Theme.of(context)
                                                .hintColor,
                                  ),
                                ),
                              ),
                            ),
                            const SizedBox(height: 16),

                            // Capacity input
                            TextFormField(
                              controller: _capacityController,
                              decoration: InputDecoration(
                                labelText: l10n.capacity,
                                prefixIcon:
                                    const Icon(Icons.local_shipping),
                                suffixText: l10n.quintals,
                                border: const OutlineInputBorder(),
                                contentPadding:
                                    const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 20,
                                ),
                              ),
                              keyboardType:
                                  const TextInputType.numberWithOptions(
                                decimal: true,
                              ),
                              inputFormatters: [
                                FilteringTextInputFormatter.allow(
                                  RegExp(r'^\d+\.?\d{0,2}'),
                                ),
                              ],
                              validator: (value) {
                                if (value == null ||
                                    value.isEmpty) {
                                  return l10n.capacityRequired;
                                }
                                final capacity =
                                    double.tryParse(value);
                                if (capacity == null ||
                                    capacity < 0.1 ||
                                    capacity > 1000) {
                                  return l10n.invalidCapacity;
                                }
                                return null;
                              },
                            ),
                            const SizedBox(height: 16),

                            // Price per quintal input
                            TextFormField(
                              controller: _priceController,
                              decoration: InputDecoration(
                                labelText: l10n.pricePerQuintal,
                                prefixIcon:
                                    const Icon(Icons.payments),
                                suffixText: 'ETB',
                                border: const OutlineInputBorder(),
                                contentPadding:
                                    const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 20,
                                ),
                              ),
                              keyboardType:
                                  const TextInputType.numberWithOptions(
                                decimal: true,
                              ),
                              inputFormatters: [
                                FilteringTextInputFormatter.allow(
                                  RegExp(r'^\d+\.?\d{0,2}'),
                                ),
                              ],
                              validator: (value) {
                                if (value == null ||
                                    value.isEmpty) {
                                  return l10n.priceRequired;
                                }
                                final price =
                                    double.tryParse(value);
                                if (price == null ||
                                    price < 0.01 ||
                                    price > 10000) {
                                  return l10n.invalidPrice;
                                }
                                return null;
                              },
                            ),
                            const SizedBox(height: 24),

                            // Save button
                            ElevatedButton(
                              onPressed:
                                  isLoading ? null : _handleSave,
                              style: ElevatedButton.styleFrom(
                                padding:
                                    const EdgeInsets.symmetric(
                                        vertical: 16),
                                minimumSize:
                                    const Size.fromHeight(48),
                                shape: RoundedRectangleBorder(
                                  borderRadius:
                                      BorderRadius.circular(8),
                                ),
                              ),
                              child: isLoading
                                  ? const SizedBox(
                                      height: 20,
                                      width: 20,
                                      child:
                                          CircularProgressIndicator(
                                        strokeWidth: 2,
                                        valueColor:
                                            AlwaysStoppedAnimation<
                                                Color>(
                                          Colors.white,
                                        ),
                                      ),
                                    )
                                  : Text(
                                      l10n.save,
                                      style: const TextStyle(
                                          fontSize: 16),
                                    ),
                            ),
                          ],
                        );
                      },
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
