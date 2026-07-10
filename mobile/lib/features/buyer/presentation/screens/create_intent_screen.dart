import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:intl/intl.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_bloc.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_event.dart';
import 'package:wereda_connect/features/buyer/presentation/bloc/buyer_intent_state.dart';
import 'package:wereda_connect/features/buyer/presentation/screens/match_results_screen.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

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

  /// The place chosen from the selector, if any. Null for free-text entries.
  Place? _selectedDeliveryPlace;

  /// Optional delivery deadline; null means no deadline.
  DateTime? _neededByDate;

  @override
  void initState() {
    super.initState();
    // Pre-fill delivery woreda from auth state, then try to resolve it to a
    // known place so the saved intent carries a placeId when possible.
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      final woreda = authState.woreda ?? '';
      _woredaController.text = woreda;
      if (woreda.trim().isNotEmpty) {
        PlaceRepository.instance.ensureLoaded().then((_) {
          if (!mounted) return;
          final match = PlaceRepository.instance.findByDisplayName(woreda);
          if (match != null) setState(() => _selectedDeliveryPlace = match);
        });
      }
    }
  }

  @override
  void dispose() {
    _quantityController.dispose();
    _maxPriceController.dispose();
    _woredaController.dispose();
    super.dispose();
  }

  /// Whether the user has attempted to submit — inline field errors only show
  /// after the first attempt so a fresh form isn't covered in red.
  bool _showErrors = false;

  bool get _isQuantityValid =>
      (double.tryParse(_quantityController.text) ?? 0) > 0;

  /// Max price is OPTIONAL: empty means "no budget" (stored as 0, which both
  /// scorers treat as unconstrained). Non-empty input must be a positive number.
  bool get _isMaxPriceValid {
    final text = _maxPriceController.text.trim();
    if (text.isEmpty) return true;
    return (double.tryParse(text) ?? 0) > 0;
  }

  bool get _isWoredaValid => _woredaController.text.trim().isNotEmpty;

  bool get _isFormValid =>
      _selectedCropType != null &&
      _isQuantityValid &&
      _isMaxPriceValid &&
      _isWoredaValid;

  void _onFindMatches() {
    // Instead of a silently-disabled button, validate on tap and show inline
    // errors under the offending fields so the user knows what's missing.
    if (!_isFormValid) {
      setState(() => _showErrors = true);
      return;
    }

    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;

    final maxPriceText = _maxPriceController.text.trim();
    context.read<BuyerIntentBloc>().add(
          CreateBuyerIntent(
            buyerId: authState.userId,
            cropType: _selectedCropType!,
            quantityNeeded: double.parse(_quantityController.text),
            // 0 = no budget (optional field left empty).
            maxPricePerKg:
                maxPriceText.isEmpty ? 0 : double.parse(maxPriceText),
            deliveryWoreda: _woredaController.text.trim(),
            placeId: _selectedDeliveryPlace?.id,
            neededByDate: _neededByDate,
          ),
        );
  }

  Future<void> _pickNeededByDate() async {
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: _neededByDate ?? now.add(const Duration(days: 7)),
      firstDate: now,
      lastDate: now.add(const Duration(days: 365)),
    );
    if (picked != null) setState(() => _neededByDate = picked);
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
                    initialIsStale: state.isStale,
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
                if (_showErrors && _selectedCropType == null)
                  Padding(
                    padding: const EdgeInsets.only(top: 6),
                    child: Text(
                      l10n.selectCropTypeError,
                      style: TextStyle(
                        fontSize: 12,
                        color: Theme.of(context).colorScheme.error,
                      ),
                    ),
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
                    errorText: _showErrors && !_isQuantityValid
                        ? l10n.enterQuantityError
                        : null,
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
                    helperText: l10n.maxPriceOptionalHint,
                    errorText: _showErrors && !_isMaxPriceValid
                        ? l10n.enterValidPriceError
                        : null,
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
                PlaceSelector(
                  controller: _woredaController,
                  hintText: l10n.wereda,
                  onPlaceSelected: (place) =>
                      setState(() => _selectedDeliveryPlace = place),
                  onChanged: (_) => setState(() {}),
                ),
                if (_showErrors && !_isWoredaValid)
                  Padding(
                    padding: const EdgeInsets.only(top: 6),
                    child: Text(
                      l10n.enterDeliveryWoredaError,
                      style: TextStyle(
                        fontSize: 12,
                        color: Theme.of(context).colorScheme.error,
                      ),
                    ),
                  ),
                const SizedBox(height: 24),

                // Needed-by date (optional)
                Text(
                  l10n.neededByDate,
                  style: Theme.of(context)
                      .textTheme
                      .titleMedium
                      ?.copyWith(fontWeight: FontWeight.bold),
                ),
                const SizedBox(height: 8),
                OutlinedButton.icon(
                  onPressed: _pickNeededByDate,
                  icon: const Icon(Icons.calendar_today, size: 18),
                  label: Text(
                    _neededByDate == null
                        ? l10n.selectDate
                        : DateFormat.yMMMd().format(_neededByDate!),
                  ),
                  style: OutlinedButton.styleFrom(
                    minimumSize: const Size.fromHeight(48),
                    alignment: Alignment.centerLeft,
                  ),
                ),
                if (_neededByDate != null)
                  Align(
                    alignment: Alignment.centerRight,
                    child: TextButton(
                      onPressed: () => setState(() => _neededByDate = null),
                      child: Text(l10n.clearDate),
                    ),
                  ),
                Padding(
                  padding: const EdgeInsets.only(top: 4),
                  child: Text(
                    l10n.neededByDateHint,
                    style: const TextStyle(
                      fontSize: 12,
                      color: AppTheme.textMuted,
                    ),
                  ),
                ),
                const SizedBox(height: 32),

                // Find Matches button
                BlocBuilder<BuyerIntentBloc, BuyerIntentState>(
                  builder: (context, state) {
                    final isLoading = state is BuyerIntentLoading;
                    return SizedBox(
                      width: double.infinity,
                      child: FilledButton.icon(
                        // Kept enabled when the form is invalid so tapping
                        // surfaces inline errors instead of doing nothing.
                        onPressed: isLoading ? null : _onFindMatches,
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
                    // Future-tense hint — the "Saved!" confirmation only fires
                    // as a snackbar after the intent is actually saved.
                    l10n.intentSaveHint,
                    textAlign: TextAlign.center,
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
