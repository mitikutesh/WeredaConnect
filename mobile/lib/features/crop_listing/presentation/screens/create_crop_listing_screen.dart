import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Screen for creating a new crop listing.
///
/// Form-based UI following register_screen.dart patterns:
/// - Large touch targets (48dp min)
/// - Localized labels and validation
/// - BlocListener for state transitions
/// - Loading spinner on submit
class CreateCropListingScreen extends StatefulWidget {
  const CreateCropListingScreen({super.key});

  @override
  State<CreateCropListingScreen> createState() =>
      _CreateCropListingScreenState();
}

class _CreateCropListingScreenState extends State<CreateCropListingScreen> {
  final _formKey = GlobalKey<FormState>();
  final _quantityController = TextEditingController();
  final _priceController = TextEditingController();
  final _locationController = TextEditingController();
  final _descriptionController = TextEditingController();

  CropType? _selectedCropType;

  @override
  void dispose() {
    _quantityController.dispose();
    _priceController.dispose();
    _locationController.dispose();
    _descriptionController.dispose();
    super.dispose();
  }

  void _handleSave() {
    if (!_formKey.currentState!.validate()) return;

    final l10n = AppLocalizations.of(context)!;

    if (_selectedCropType == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(l10n.cropTypeRequired)),
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

    context.read<CropListingBloc>().add(
          CreateCropListing(
            farmerId: authState.userId,
            cropType: _selectedCropType!.name,
            quantity: double.parse(_quantityController.text),
            pricePerKg: double.parse(_priceController.text),
            // Default coordinates for Addis Ababa (GPS deferred)
            latitude: 9.0192,
            longitude: 38.7525,
            description: _descriptionController.text.trim(),
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return BlocListener<CropListingBloc, CropListingState>(
      listener: (context, state) {
        if (state is CropListingCreated) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.listingSaved)),
          );
          Navigator.of(context).pop();
        } else if (state is CropListingError) {
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
          title: Text(l10n.createListing),
          elevation: 0,
        ),
        body: SafeArea(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24.0),
            child: Form(
              key: _formKey,
              child: BlocBuilder<CropListingBloc, CropListingState>(
                builder: (context, state) {
                  final isLoading = state is CropListingLoading;

                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      // Crop type selection
                      Text(
                        l10n.selectCropType,
                        style: theme.textTheme.titleMedium,
                      ),
                      const SizedBox(height: 12),
                      _CropTypeGrid(
                        selectedCropType: _selectedCropType,
                        onSelected: (type) {
                          setState(() {
                            _selectedCropType = type;
                          });
                        },
                      ),
                      const SizedBox(height: 24),

                      // Quantity input
                      TextFormField(
                        controller: _quantityController,
                        decoration: InputDecoration(
                          labelText: l10n.quantityInQuintals,
                          prefixIcon: const Icon(Icons.scale),
                          suffixText: l10n.quintals,
                          border: const OutlineInputBorder(),
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 16,
                            vertical: 20,
                          ),
                        ),
                        keyboardType: const TextInputType.numberWithOptions(
                          decimal: true,
                        ),
                        inputFormatters: [
                          FilteringTextInputFormatter.allow(
                            RegExp(r'^\d+\.?\d{0,2}'),
                          ),
                        ],
                        validator: (value) {
                          if (value == null || value.isEmpty) {
                            return l10n.quantityRequired;
                          }
                          final quantity = double.tryParse(value);
                          if (quantity == null || quantity <= 0) {
                            return l10n.quantityRequired;
                          }
                          return null;
                        },
                      ),
                      const SizedBox(height: 16),

                      // Price input
                      TextFormField(
                        controller: _priceController,
                        decoration: InputDecoration(
                          labelText: l10n.priceInBirr,
                          prefixIcon: const Icon(Icons.payments),
                          suffixText: l10n.etbPerKg,
                          border: const OutlineInputBorder(),
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 16,
                            vertical: 20,
                          ),
                        ),
                        keyboardType: const TextInputType.numberWithOptions(
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
                          if (price == null || price <= 0) {
                            return l10n.priceRequired;
                          }
                          return null;
                        },
                      ),
                      const SizedBox(height: 16),

                      // Location input
                      TextFormField(
                        controller: _locationController,
                        decoration: InputDecoration(
                          labelText: l10n.location,
                          prefixIcon: const Icon(Icons.location_on),
                          hintText: l10n.wereda,
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
                            return l10n.locationRequired;
                          }
                          return null;
                        },
                      ),
                      const SizedBox(height: 16),

                      // Description input
                      TextFormField(
                        controller: _descriptionController,
                        decoration: InputDecoration(
                          labelText: l10n.description,
                          prefixIcon: const Icon(Icons.description),
                          border: const OutlineInputBorder(),
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 16,
                            vertical: 20,
                          ),
                        ),
                        maxLines: 3,
                        keyboardType: TextInputType.multiline,
                      ),
                      const SizedBox(height: 16),

                      // Audio note placeholder
                      OutlinedButton.icon(
                        onPressed: () {
                          ScaffoldMessenger.of(context).showSnackBar(
                            SnackBar(content: Text(l10n.comingSoon)),
                          );
                        },
                        icon: const Icon(Icons.mic),
                        label: Text(l10n.recordAudioNote),
                        style: OutlinedButton.styleFrom(
                          padding: const EdgeInsets.symmetric(vertical: 16),
                          minimumSize: const Size.fromHeight(48),
                        ),
                      ),
                      const SizedBox(height: 24),

                      // Save button
                      ElevatedButton(
                        onPressed: isLoading ? null : _handleSave,
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
}

/// Grid of crop type selection cards with icons.
class _CropTypeGrid extends StatelessWidget {
  final CropType? selectedCropType;
  final ValueChanged<CropType> onSelected;

  const _CropTypeGrid({
    required this.selectedCropType,
    required this.onSelected,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return Wrap(
      spacing: 8,
      runSpacing: 8,
      children: CropType.values.map((type) {
        final isSelected = selectedCropType == type;
        return InkWell(
          onTap: () => onSelected(type),
          borderRadius: BorderRadius.circular(12),
          child: Container(
            width: 100,
            padding: const EdgeInsets.symmetric(vertical: 12, horizontal: 8),
            decoration: BoxDecoration(
              border: Border.all(
                color: isSelected
                    ? theme.colorScheme.primary
                    : Colors.grey.shade300,
                width: isSelected ? 2 : 1,
              ),
              borderRadius: BorderRadius.circular(12),
              color: isSelected
                  ? theme.colorScheme.primary.withValues(alpha: 0.1)
                  : Colors.transparent,
            ),
            child: Column(
              children: [
                Icon(
                  type.icon,
                  size: 32,
                  color: isSelected
                      ? theme.colorScheme.primary
                      : Colors.grey.shade600,
                ),
                const SizedBox(height: 4),
                Text(
                  type.displayName(l10n),
                  style: TextStyle(
                    fontSize: 12,
                    color: isSelected
                        ? theme.colorScheme.primary
                        : Colors.grey.shade700,
                    fontWeight:
                        isSelected ? FontWeight.bold : FontWeight.normal,
                  ),
                  textAlign: TextAlign.center,
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          ),
        );
      }).toList(),
    );
  }
}
