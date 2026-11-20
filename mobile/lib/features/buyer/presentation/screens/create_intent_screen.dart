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
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

/// Screen for buyers to create a buying intent (design canvas page M:
/// CreateIntentM.dc.html): Back beside the question, the crop as pills with
/// the crop glyph, the quantity in kilograms and the maximum price side by
/// side with the quantity read back kg first, the delivery place, the
/// optional needed-by date, the note about alerts, and the orange Find
/// Matches pinned at the foot. "Find Matches" saves the intent and
/// navigates to results.
///
/// The buyer types kilograms; the intent stores quintals as before
/// (buyer-intent contract), converted here at the form boundary.
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

  /// The kilograms typed, or null when the field holds no number.
  double? get _quantityKg =>
      double.tryParse(_quantityController.text.replaceAll(',', '').trim());

  bool get _isQuantityValid => (_quantityKg ?? 0) > 0;

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
            // Typed in kg, stored in quintals.
            quantityNeeded: _quantityKg! / kgPerQuintal,
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
    final errorStyle = TextStyle(
      fontSize: 12,
      color: Theme.of(context).colorScheme.error,
    );

    return Scaffold(
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
          bottom: false,
          child: SingleChildScrollView(
            child: FwContentWidth(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // CreateIntentM board: Back beside the question, the
                  // line about matches under them.
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
                              l10n.whatDoYouNeed,
                              style: FwText.detailTitle.copyWith(fontSize: 28),
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 10, 20, 24),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(l10n.whatDoYouNeedSubtitle, style: FwText.body),
                        const SizedBox(height: 20),

                        // Crop type chips
                        FwSectionLabel(l10n.selectCropType),
                        const SizedBox(height: 10),
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
                              style: errorStyle,
                            ),
                          ),
                        const SizedBox(height: 18),

                        // Quantity (kg) and max price side by side, as on
                        // the board.
                        LabeledFieldPair(
                          first: LabeledField(
                            label: l10n.quantityNeededKg,
                            field: TextField(
                              key: const Key('create_intent_quantity'),
                              controller: _quantityController,
                              keyboardType:
                                  const TextInputType.numberWithOptions(
                                decimal: true,
                              ),
                              decoration:
                                  labeledFieldDecoration(hintText: '500')
                                      .copyWith(
                                errorText: _showErrors && !_isQuantityValid
                                    ? l10n.enterQuantityError
                                    : null,
                              ),
                              onChanged: (_) => setState(() {}),
                            ),
                          ),
                          second: LabeledField(
                            label: l10n.maxPrice,
                            field: TextField(
                              key: const Key('create_intent_max_price'),
                              controller: _maxPriceController,
                              keyboardType:
                                  const TextInputType.numberWithOptions(
                                decimal: true,
                              ),
                              decoration: labeledFieldDecoration(hintText: '45')
                                  .copyWith(
                                errorText: _showErrors && !_isMaxPriceValid
                                    ? l10n.enterValidPriceError
                                    : null,
                              ),
                              onChanged: (_) => setState(() {}),
                            ),
                          ),
                        ),
                        // "2,000 kg (20 quintals)": the quantity read
                        // back, kilograms first.
                        if (_isQuantityValid &&
                            isRealKgQuantity(_quantityKg!)) ...[
                          const SizedBox(height: 8),
                          Text.rich(
                            key: const Key('create_intent_quantity_echo'),
                            TextSpan(
                              text: kgQuantityText(_quantityKg!, l10n),
                              children: [
                                TextSpan(
                                  text:
                                      ' ${quintalsInParensText(_quantityKg!, l10n)}',
                                  style: const TextStyle(
                                    fontWeight: FontWeight.w500,
                                    color: AppTheme.slate,
                                  ),
                                ),
                              ],
                            ),
                            style: const TextStyle(
                              fontSize: 13,
                              fontWeight: FontWeight.w700,
                              color: AppTheme.ink,
                            ),
                          ),
                        ],
                        const SizedBox(height: 8),
                        Text(
                          l10n.maxPriceOptionalHint,
                          style: LabeledField.helperStyle,
                        ),
                        const SizedBox(height: 18),

                        // Delivery woreda input
                        LabeledField(
                          // The suggestions stay their own nodes (see LabeledField).
                          mergeSemantics: false,
                          label: l10n.deliverTo,
                          field: PlaceSelector(
                            controller: _woredaController,
                            labelAbove: true,
                            showSearchIcon: false,
                            hintText: l10n.wereda,
                            onPlaceSelected: (place) =>
                                setState(() => _selectedDeliveryPlace = place),
                            onChanged: (_) => setState(() {}),
                          ),
                        ),
                        if (_showErrors && !_isWoredaValid)
                          Padding(
                            padding: const EdgeInsets.only(top: 6),
                            child: Text(
                              l10n.enterDeliveryWoredaError,
                              style: errorStyle,
                            ),
                          ),
                        const SizedBox(height: 16),

                        // Needed-by date (optional)
                        LabeledField(
                          label: l10n.neededByDate,
                          field: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              DateFieldButton(
                                key: const Key('create_intent_needed_by'),
                                text: _neededByDate == null
                                    ? l10n.selectDate
                                    : formatBoardDay(_neededByDate!),
                                placeholder: _neededByDate == null,
                                onTap: _pickNeededByDate,
                                onClear: _neededByDate == null
                                    ? null
                                    : () =>
                                        setState(() => _neededByDate = null),
                              ),
                              const SizedBox(height: 6),
                              Text(
                                l10n.neededByDateHint,
                                style: LabeledField.helperStyle,
                              ),
                            ],
                          ),
                        ),
                        const SizedBox(height: 18),
                        // The board moved this note up from under the
                        // button. Future tense: the "Saved!" confirmation
                        // only fires as a snackbar after the intent is
                        // actually saved.
                        Text(
                          l10n.intentSaveHint,
                          style: FwText.body.copyWith(
                            fontWeight: FontWeight.w400,
                          ),
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
      // Find Matches, the orange pill pinned at the foot over a hairline.
      bottomNavigationBar: BlocBuilder<BuyerIntentBloc, BuyerIntentState>(
        builder: (context, state) {
          final isLoading = state is BuyerIntentLoading;
          return DecoratedBox(
            decoration: const BoxDecoration(
              color: AppTheme.ground,
              border: Border(top: BorderSide(color: AppTheme.line)),
            ),
            child: FwBottomActions(
              children: [
                const SizedBox(height: 4),
                FilledButton(
                  onPressed: isLoading ? null : _onFindMatches,
                  child: isLoading
                      ? const SizedBox(
                          height: 18,
                          width: 18,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: AppTheme.ink,
                          ),
                        )
                      : Text(l10n.findMatches),
                ),
              ],
            ),
          );
        },
      ),
    );
  }
}

/// Crop type pills, wrapping onto more lines as on the board: outlined with
/// the crop glyph, the chosen one black with white ink and a check.
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

    return Wrap(
      spacing: 8,
      runSpacing: 8,
      children: CropType.values.map((type) {
        final isSelected = selectedCropType == type.name;
        final ink = isSelected ? Colors.white : AppTheme.inkSoft;
        return Semantics(
          key: Key('create_intent_crop_${type.name}'),
          button: true,
          selected: isSelected,
          child: FwTapTarget(
            onTap: () => onCropTypeChanged(type.name),
            child: Material(
              color: isSelected ? AppTheme.ink : Colors.transparent,
              shape: StadiumBorder(
                side: BorderSide(
                  color: isSelected ? AppTheme.ink : AppTheme.fieldBorder,
                ),
              ),
              clipBehavior: Clip.antiAlias,
              child: InkWell(
                onTap: () => onCropTypeChanged(type.name),
                child: ConstrainedBox(
                  constraints: const BoxConstraints(minHeight: 44),
                  child: Padding(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 14,
                      vertical: 4,
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (isSelected) ...[
                          const Icon(
                            Icons.check,
                            size: 13,
                            color: Colors.white,
                          ),
                          const SizedBox(width: 6),
                        ],
                        ExcludeSemantics(
                          child: FwSprout(color: ink, size: 20),
                        ),
                        const SizedBox(width: 6),
                        Flexible(
                          child: Text(
                            type.displayName(l10n),
                            style: TextStyle(
                              fontSize: 13,
                              fontWeight: isSelected
                                  ? FontWeight.w700
                                  : FontWeight.w500,
                              color: ink,
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
        );
      }).toList(),
    );
  }
}
