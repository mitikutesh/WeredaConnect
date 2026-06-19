import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/match_results_screen.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Screen for buyers to create a buying intent.
///
/// Form with crop type chips, quantity, max price, and delivery woreda.
/// "Find Matches" saves the intent and navigates to results.
class CreateIntentScreen extends StatefulWidget {
  const CreateIntentScreen({super.key});

  @override
  State<CreateIntentScreen> createState() => _CreateIntentScreenState();
}

class _CreateIntentScreenState extends State<CreateIntentScreen> {
  String? _selectedCropType;
  final _quantityController = TextEditingController();
  final _maxPriceController = TextEditingController();
  final _woredaController = TextEditingController();

  @override
  void initState() {
    super.initState();
    // Pre-fill delivery woreda from auth state
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      _woredaController.text = authState.woreda ?? '';
    }
  }

  @override
  void dispose() {
    _quantityController.dispose();
    _maxPriceController.dispose();
    _woredaController.dispose();
    super.dispose();
  }

  bool get _isFormValid {
    return _selectedCropType != null &&
        (double.tryParse(_quantityController.text) ?? 0) > 0 &&
        (double.tryParse(_maxPriceController.text) ?? 0) > 0 &&
        _woredaController.text.trim().isNotEmpty;
  }

  void _onFindMatches() {
    if (!_isFormValid) return;

    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;

    context.read<BuyerIntentBloc>().add(
          CreateBuyerIntent(
            buyerId: authState.userId,
            cropType: _selectedCropType!,
            quantityNeeded: double.parse(_quantityController.text),
            maxPricePerKg: double.parse(_maxPriceController.text),
            deliveryWoreda: _woredaController.text.trim(),
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Scaffold(
      appBar: AppBar(title: Text(l10n.findCrops), elevation: 0),
      body: BlocListener<BuyerIntentBloc, BuyerIntentState>(
        listener: (context, state) {
          if (state is BuyerIntentCreated) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(l10n.intentSaved)),
            );
            Navigator.pushReplacement(
              context,
              MaterialPageRoute<void>(
                builder: (_) => BlocProvider.value(
                  value: context.read<BuyerIntentBloc>(),
                  child: MatchResultsScreen(
                    intent: state.intent,
                    initialMatches: state.matches,
                  ),
                ),
              ),
            );
          } else if (state is BuyerIntentError) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(content: Text(localizeErrorCode(state.code, l10n))),
            );
          }
        },
        child: SafeArea(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                // Title
                Text(
                  l10n.whatDoYouNeed,
                  style: Theme.of(context)
                      .textTheme
                      .headlineSmall
                      ?.copyWith(fontWeight: FontWeight.bold),
                ),
                const SizedBox(height: 4),
                Text(
                  l10n.whatDoYouNeedSubtitle,
                  style: const TextStyle(
                    fontSize: 14,
                    color: AppTheme.textMuted,
                  ),
                ),
                const SizedBox(height: 24),

                // Crop type label
                Text(
                  l10n.selectCropType,
                  style: Theme.of(context)
                      .textTheme
                      .titleMedium
                      ?.copyWith(fontWeight: FontWeight.bold),
                ),
                const SizedBox(height: 8),

                // Crop type chips
                _CropTypeChips(
                  selectedCropType: _selectedCropType,
                  onCropTypeChanged: (type) {
                    setState(() => _selectedCropType = type);
                  },
                ),
                const SizedBox(height: 24),

                // Quantity input
                Text(
                  l10n.quantityNeeded,
                  style: Theme.of(context)
                      .textTheme
                      .titleMedium
                      ?.copyWith(fontWeight: FontWeight.bold),
                ),
                const SizedBox(height: 8),
                TextField(
                  controller: _quantityController,
                  keyboardType:
                      const TextInputType.numberWithOptions(decimal: true),
                  decoration: InputDecoration(
                    suffixText: l10n.quintals,
                    border: const OutlineInputBorder(),
                    hintText: '5',
                  ),
                  onChanged: (_) => setState(() {}),
                ),
                const SizedBox(height: 24),

                // Max price input
                Text(
                  l10n.maxPrice,
                  style: Theme.of(context)
                      .textTheme
                      .titleMedium
                      ?.copyWith(fontWeight: FontWeight.bold),
                ),
                const SizedBox(height: 8),
                TextField(
                  controller: _maxPriceController,
                  keyboardType:
                      const TextInputType.numberWithOptions(decimal: true),
                  decoration: InputDecoration(
                    suffixText: l10n.etbPerKg,
                    border: const OutlineInputBorder(),
                    hintText: '45',
                  ),
                  onChanged: (_) => setState(() {}),
                ),
                const SizedBox(height: 24),

                // Delivery woreda input
                Text(
                  l10n.deliverTo,
                  style: Theme.of(context)
                      .textTheme
                      .titleMedium
                      ?.copyWith(fontWeight: FontWeight.bold),
                ),
                const SizedBox(height: 8),
                TextField(
                  controller: _woredaController,
                  decoration: InputDecoration(
                    border: const OutlineInputBorder(),
                    hintText: l10n.wereda,
                    prefixIcon: const Icon(Icons.location_on_outlined),
                  ),
                  onChanged: (_) => setState(() {}),
                ),
                const SizedBox(height: 32),

                // Find Matches button
                BlocBuilder<BuyerIntentBloc, BuyerIntentState>(
                  builder: (context, state) {
                    final isLoading = state is BuyerIntentLoading;
                    return SizedBox(
                      width: double.infinity,
                      child: FilledButton.icon(
                        onPressed:
                            isLoading || !_isFormValid ? null : _onFindMatches,
                        icon: isLoading
                            ? const SizedBox(
                                height: 18,
                                width: 18,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                  color: Colors.white,
                                ),
                              )
                            : const Icon(Icons.search),
                        label: Text(l10n.findMatches),
                        style: FilledButton.styleFrom(
                          minimumSize: const Size.fromHeight(48),
                        ),
                      ),
                    );
                  },
                ),

                const SizedBox(height: 12),
                Center(
                  child: Text(
                    l10n.intentSaved,
                    style: const TextStyle(
                      fontSize: 12,
                      color: AppTheme.textMuted,
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// Horizontal scrolling crop type filter chips.
class _CropTypeChips extends StatelessWidget {
  final String? selectedCropType;
  final ValueChanged<String?> onCropTypeChanged;

  const _CropTypeChips({
    required this.selectedCropType,
    required this.onCropTypeChanged,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return SizedBox(
      height: 50,
      child: ListView(
        scrollDirection: Axis.horizontal,
        children: CropType.values.map((type) {
          final isSelected = selectedCropType == type.name;
          return Padding(
            padding: const EdgeInsets.only(right: 8),
            child: FilterChip(
              avatar: Icon(type.icon, size: 18),
              label: Text(type.displayName(l10n)),
              selected: isSelected,
              labelStyle: TextStyle(
                color: isSelected ? Colors.white : AppTheme.textDark,
              ),
              onSelected: (_) => onCropTypeChanged(type.name),
            ),
          );
        }).toList(),
      ),
    );
  }
}
