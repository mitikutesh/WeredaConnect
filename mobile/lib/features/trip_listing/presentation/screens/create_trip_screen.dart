import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';
import 'package:isar/isar.dart';

import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Screen for creating a new trip listing.
///
/// Features:
/// - Auto-fills trucker name/phone from user profile
/// - Recent route chips for quick-fill
/// - Stepper buttons for capacity and price
/// - Repeat-last-trip factory constructor
/// - 56dp touch targets for rural trucker usability
class CreateTripScreen extends StatefulWidget {
  final String? truckerName;
  final String? truckerPhone;
  final String? fromWoreda;
  final String? toWoreda;
  final double? capacity;
  final double? pricePerQuintal;
  final String? vehiclePlate;
  final String? vehicleDescription;

  const CreateTripScreen({
    this.truckerName,
    this.truckerPhone,
    this.fromWoreda,
    this.toWoreda,
    this.capacity,
    this.pricePerQuintal,
    this.vehiclePlate,
    this.vehicleDescription,
    super.key,
  });

  /// Create a trip screen pre-filled with data from an existing trip.
  factory CreateTripScreen.fromTrip(Trip trip) {
    return CreateTripScreen(
      truckerName: trip.truckerName,
      truckerPhone: trip.truckerPhone,
      fromWoreda: trip.fromWoreda,
      toWoreda: trip.toWoreda,
      capacity: trip.capacity,
      pricePerQuintal: trip.pricePerQuintal,
      vehiclePlate: trip.vehiclePlate,
      vehicleDescription: trip.vehicleDescription,
    );
  }

  @override
  State<CreateTripScreen> createState() => _CreateTripScreenState();
}

class _CreateTripScreenState extends State<CreateTripScreen> {
  final _formKey = GlobalKey<FormState>();
  final _truckerNameController = TextEditingController();
  final _truckerPhoneController = TextEditingController();
  final _vehiclePlateController = TextEditingController();
  final _vehicleDescriptionController = TextEditingController();
  final _fromWoredaController = TextEditingController();
  final _toWoredaController = TextEditingController();
  final _capacityController = TextEditingController();
  final _priceController = TextEditingController();

  DateTime? _selectedDepartureDate;
  List<({String from, String to})> _recentRoutes = [];

  @override
  void initState() {
    super.initState();
    // Pre-fill from constructor args
    if (widget.truckerName != null) {
      _truckerNameController.text = widget.truckerName!;
    }
    if (widget.truckerPhone != null) {
      _truckerPhoneController.text = widget.truckerPhone!;
    }
    if (widget.fromWoreda != null) {
      _fromWoredaController.text = widget.fromWoreda!;
    }
    if (widget.toWoreda != null) {
      _toWoredaController.text = widget.toWoreda!;
    }
    if (widget.capacity != null) {
      _capacityController.text = widget.capacity!.toStringAsFixed(0);
    }
    if (widget.pricePerQuintal != null) {
      _priceController.text = widget.pricePerQuintal!.toStringAsFixed(0);
    }
    if (widget.vehiclePlate != null) {
      _vehiclePlateController.text = widget.vehiclePlate!;
    }
    if (widget.vehicleDescription != null) {
      _vehicleDescriptionController.text = widget.vehicleDescription!;
    }

    _autoFillTruckerInfo();
    _loadRecentRoutes();
  }

  /// Auto-fill trucker name/phone from Isar user profile.
  Future<void> _autoFillTruckerInfo() async {
    if (_truckerNameController.text.isNotEmpty &&
        _truckerPhoneController.text.isNotEmpty) {
      return;
    }

    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;

    try {
      final isar = IsarService.instance.db;
      final user =
          await isar.users.filter().userIdEqualTo(authState.userId).findFirst();
      if (user != null && mounted) {
        setState(() {
          if (_truckerNameController.text.isEmpty) {
            _truckerNameController.text = user.effectiveDisplayName;
          }
          if (_truckerPhoneController.text.isEmpty) {
            _truckerPhoneController.text = user.phoneNumber;
          }
        });
      }
    } on Object {
      // Silently ignore — fields stay empty for manual entry
    }
  }

  /// Load recent routes for quick-fill chips.
  Future<void> _loadRecentRoutes() async {
    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;

    try {
      final routes = await context
          .read<TripListingBloc>()
          .repository
          .getRecentRoutes(authState.userId);
      if (mounted) {
        setState(() => _recentRoutes = routes);
      }
    } on Object {
      // Silently ignore
    }
  }

  @override
  void dispose() {
    _truckerNameController.dispose();
    _truckerPhoneController.dispose();
    _vehiclePlateController.dispose();
    _vehicleDescriptionController.dispose();
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

  /// Adjust a numeric text field by [delta], clamped to [min]..[max].
  void _adjustValue(
    TextEditingController controller,
    double delta,
    double min,
    double max,
  ) {
    final current = double.tryParse(controller.text) ?? 0;
    final newValue = (current + delta).clamp(min, max);
    controller.text = newValue == newValue.roundToDouble()
        ? newValue.toStringAsFixed(0)
        : newValue.toStringAsFixed(2);
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

    final vehiclePlate = _vehiclePlateController.text.trim();
    final vehicleDescription = _vehicleDescriptionController.text.trim();

    context.read<TripListingBloc>().add(
          CreateTrip(
            truckerId: authState.userId,
            truckerName: _truckerNameController.text.trim(),
            truckerPhone: _truckerPhoneController.text.trim(),
            vehiclePlate: vehiclePlate.isNotEmpty ? vehiclePlate : null,
            vehicleDescription:
                vehicleDescription.isNotEmpty ? vehicleDescription : null,
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
                            // Recent route chips
                            if (_recentRoutes.isNotEmpty) ...[
                              Text(
                                l10n.quickFill,
                                style: Theme.of(context)
                                    .textTheme
                                    .labelMedium
                                    ?.copyWith(fontWeight: FontWeight.w600),
                              ),
                              const SizedBox(height: 8),
                              Wrap(
                                spacing: 8,
                                runSpacing: 8,
                                children: _recentRoutes.map((route) {
                                  return ActionChip(
                                    label: Text('${route.from} → ${route.to}'),
                                    materialTapTargetSize:
                                        MaterialTapTargetSize.padded,
                                    onPressed: () {
                                      setState(() {
                                        _fromWoredaController.text = route.from;
                                        _toWoredaController.text = route.to;
                                      });
                                    },
                                  );
                                }).toList(),
                              ),
                              const SizedBox(height: 16),
                            ],

                            // Trucker name input
                            TextFormField(
                              controller: _truckerNameController,
                              decoration: InputDecoration(
                                labelText: l10n.truckerName,
                                prefixIcon: const Icon(Icons.person),
                                border: const OutlineInputBorder(),
                                contentPadding: const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 20,
                                ),
                              ),
                              keyboardType: TextInputType.name,
                              textCapitalization: TextCapitalization.words,
                              validator: (value) {
                                if (value == null || value.trim().isEmpty) {
                                  return l10n.truckerNameRequired;
                                }
                                return null;
                              },
                            ),
                            const SizedBox(height: 16),

                            // Trucker phone input
                            TextFormField(
                              controller: _truckerPhoneController,
                              decoration: InputDecoration(
                                labelText: l10n.truckerPhone,
                                prefixIcon: const Icon(Icons.phone),
                                hintText: '+251XXXXXXXXX',
                                border: const OutlineInputBorder(),
                                contentPadding: const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 20,
                                ),
                              ),
                              keyboardType: TextInputType.phone,
                              validator: (value) {
                                if (value == null || value.trim().isEmpty) {
                                  return l10n.invalidPhoneFormat;
                                }
                                final phoneRegex = RegExp(r'^\+251\d{9}$');
                                if (!phoneRegex.hasMatch(value.trim())) {
                                  return l10n.invalidPhoneFormat;
                                }
                                return null;
                              },
                            ),
                            const SizedBox(height: 16),

                            // Vehicle plate input
                            TextFormField(
                              controller: _vehiclePlateController,
                              decoration: InputDecoration(
                                labelText: l10n.vehiclePlate,
                                prefixIcon:
                                    const Icon(Icons.confirmation_number),
                                hintText: '3-AA-12345',
                                border: const OutlineInputBorder(),
                                contentPadding: const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 20,
                                ),
                              ),
                              keyboardType: TextInputType.text,
                              textCapitalization: TextCapitalization.characters,
                            ),
                            const SizedBox(height: 16),

                            // Vehicle description input
                            TextFormField(
                              controller: _vehicleDescriptionController,
                              decoration: InputDecoration(
                                labelText: l10n.vehicleDescription,
                                prefixIcon: const Icon(Icons.directions_car),
                                hintText: 'Isuzu FTR 10-ton',
                                border: const OutlineInputBorder(),
                                contentPadding: const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 20,
                                ),
                              ),
                              keyboardType: TextInputType.text,
                              textCapitalization: TextCapitalization.words,
                            ),
                            const SizedBox(height: 16),

                            // From Woreda input
                            TextFormField(
                              controller: _fromWoredaController,
                              decoration: InputDecoration(
                                labelText: l10n.fromWoreda,
                                prefixIcon: const Icon(Icons.trip_origin),
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
                                contentPadding: const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 20,
                                ),
                              ),
                              keyboardType: TextInputType.text,
                              textCapitalization: TextCapitalization.words,
                              validator: (value) {
                                if (value == null || value.trim().isEmpty) {
                                  return l10n.toWoredaRequired;
                                }
                                if (value.trim() ==
                                    _fromWoredaController.text.trim()) {
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
                                    Icons.calendar_today,
                                  ),
                                  border: const OutlineInputBorder(),
                                  contentPadding: const EdgeInsets.symmetric(
                                    horizontal: 16,
                                    vertical: 20,
                                  ),
                                ),
                                child: Text(
                                  _selectedDepartureDate != null
                                      ? DateFormat.yMMMd().format(
                                          _selectedDepartureDate!,
                                        )
                                      : l10n.selectDepartureDate,
                                  style: TextStyle(
                                    color: _selectedDepartureDate != null
                                        ? null
                                        : Theme.of(context).hintColor,
                                  ),
                                ),
                              ),
                            ),
                            const SizedBox(height: 16),

                            // Capacity input with stepper buttons
                            Row(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                IconButton(
                                  onPressed: () => _adjustValue(
                                    _capacityController,
                                    -10,
                                    0.1,
                                    1000,
                                  ),
                                  icon: const Icon(Icons.remove_circle_outline),
                                  iconSize: 28,
                                  constraints: const BoxConstraints(
                                    minWidth: 56,
                                    minHeight: 56,
                                  ),
                                ),
                                Expanded(
                                  child: TextFormField(
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
                                      if (value == null || value.isEmpty) {
                                        return l10n.capacityRequired;
                                      }
                                      final capacity = double.tryParse(value);
                                      if (capacity == null ||
                                          capacity < 0.1 ||
                                          capacity > 1000) {
                                        return l10n.invalidCapacity;
                                      }
                                      return null;
                                    },
                                  ),
                                ),
                                IconButton(
                                  onPressed: () => _adjustValue(
                                    _capacityController,
                                    10,
                                    0.1,
                                    1000,
                                  ),
                                  icon: const Icon(Icons.add_circle_outline),
                                  iconSize: 28,
                                  constraints: const BoxConstraints(
                                    minWidth: 56,
                                    minHeight: 56,
                                  ),
                                ),
                              ],
                            ),
                            const SizedBox(height: 16),

                            // Price per quintal input with stepper buttons
                            Row(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                IconButton(
                                  onPressed: () => _adjustValue(
                                    _priceController,
                                    -10,
                                    0.01,
                                    10000,
                                  ),
                                  icon: const Icon(Icons.remove_circle_outline),
                                  iconSize: 28,
                                  constraints: const BoxConstraints(
                                    minWidth: 56,
                                    minHeight: 56,
                                  ),
                                ),
                                Expanded(
                                  child: TextFormField(
                                    controller: _priceController,
                                    decoration: InputDecoration(
                                      labelText: l10n.pricePerQuintal,
                                      prefixIcon: const Icon(Icons.payments),
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
                                      if (value == null || value.isEmpty) {
                                        return l10n.priceRequired;
                                      }
                                      final price = double.tryParse(value);
                                      if (price == null ||
                                          price < 0.01 ||
                                          price > 10000) {
                                        return l10n.invalidPrice;
                                      }
                                      return null;
                                    },
                                  ),
                                ),
                                IconButton(
                                  onPressed: () => _adjustValue(
                                    _priceController,
                                    10,
                                    0.01,
                                    10000,
                                  ),
                                  icon: const Icon(Icons.add_circle_outline),
                                  iconSize: 28,
                                  constraints: const BoxConstraints(
                                    minWidth: 56,
                                    minHeight: 56,
                                  ),
                                ),
                              ],
                            ),
                            const SizedBox(height: 24),

                            // Save button
                            ElevatedButton(
                              onPressed: isLoading ? null : _handleSave,
                              style: ElevatedButton.styleFrom(
                                padding: const EdgeInsets.symmetric(
                                  vertical: 16,
                                ),
                                minimumSize: const Size.fromHeight(56),
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
                                        valueColor:
                                            AlwaysStoppedAnimation<Color>(
                                          Colors.white,
                                        ),
                                      ),
                                    )
                                  : Text(
                                      l10n.save,
                                      style: const TextStyle(
                                        fontSize: 16,
                                      ),
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
