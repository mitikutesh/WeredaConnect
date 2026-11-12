import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/crop_listing.dart';
import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/utils/contact_in_text.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

/// SIM-56: the farmer edits their own active listing: how much is left
/// (quintals), the price (ETB per kg) and the description. The crop, the
/// photos and the place stay as listed (delete and list again to change
/// them). Laid out like Create listing, with Save pinned at the foot.
///
/// The fields allow what the server accepts (crop-listing-api.md): up to
/// 9,999.99 quintals, up to 999.99 ETB per kg, 500 characters of
/// description and no phone number in it. Pops with the saved listing.
class EditCropListingScreen extends StatefulWidget {
  final CropListing listing;
  final String farmerId;

  const EditCropListingScreen({
    required this.listing,
    required this.farmerId,
    super.key,
  });

  @override
  State<EditCropListingScreen> createState() => _EditCropListingScreenState();
}

class _EditCropListingScreenState extends State<EditCropListingScreen> {
  final _formKey = GlobalKey<FormState>();
  late final TextEditingController _quantityController =
      TextEditingController(text: _plain(widget.listing.quantity));
  late final TextEditingController _priceController =
      TextEditingController(text: _plain(widget.listing.pricePerKg));
  late final TextEditingController _descriptionController =
      TextEditingController(text: widget.listing.description);

  /// This screen's own submission (the shared bloc's loading state also
  /// follows the list's reload, which must not gray this button).
  bool _isSubmitting = false;

  /// The listing's numbers as the field shows them: no trailing zeros, at
  /// most two decimals (what the field accepts).
  static String _plain(double value) {
    final fixed = value.toStringAsFixed(2);
    return fixed.contains('.')
        ? fixed.replaceFirst(RegExp(r'\.?0+$'), '')
        : fixed;
  }

  @override
  void initState() {
    super.initState();
    _quantityController.addListener(_onAmountTyped);
    _priceController.addListener(_onAmountTyped);
  }

  void _onAmountTyped() {
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    _quantityController.removeListener(_onAmountTyped);
    _priceController.removeListener(_onAmountTyped);
    _quantityController.dispose();
    _priceController.dispose();
    _descriptionController.dispose();
    super.dispose();
  }

  /// What the whole listing sells for now, as on Create listing.
  String? _totalLine(AppLocalizations l10n) {
    final quintals = double.tryParse(_quantityController.text);
    final pricePerKg = double.tryParse(_priceController.text);
    if (quintals == null || quintals <= 0) return null;
    if (pricePerKg == null || pricePerKg <= 0) return null;
    final perQuintal = pricePerKg * kgPerQuintal;
    return l10n.cropFormTotalLine(
      formatEtbAmount(quintals * perQuintal),
      formatEtbAmount(perQuintal),
    );
  }

  /// The typed number, or the listing's own when the field still shows it:
  /// the free stock can carry more decimals than the field (an order's kilos
  /// taken off), and an untouched field must not round it.
  static double _valueOf(TextEditingController controller, double original) =>
      controller.text == _plain(original)
          ? original
          : double.parse(controller.text);

  void _handleSave() {
    if (_isSubmitting) return;
    if (!_formKey.currentState!.validate()) return;
    setState(() => _isSubmitting = true);
    context.read<CropListingBloc>().add(
          UpdateCropListing(
            listingId: widget.listing.listingId,
            farmerId: widget.farmerId,
            quantity: _valueOf(_quantityController, widget.listing.quantity),
            pricePerKg: _valueOf(_priceController, widget.listing.pricePerKg),
            description: _descriptionController.text.trim(),
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final crop = CropType.labelFor(widget.listing.cropType, l10n);

    return BlocListener<CropListingBloc, CropListingState>(
      // Only this screen's save: the list below reloads through the same
      // bloc and may fail on its own.
      listenWhen: (previous, current) =>
          _isSubmitting &&
          (current is CropListingUpdated || current is CropListingError),
      listener: (context, state) {
        if (state is CropListingUpdated) {
          _isSubmitting = false;
          // Saved and sent, or saved on the phone to send later (E-5).
          showSavedOrSent(context, sent: l10n.listingSaved).ignore();
          Navigator.of(context).pop(state.listing);
        } else if (state is CropListingError) {
          setState(() => _isSubmitting = false);
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(
                localizeErrorCode(state.code, l10n, message: state.message),
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
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  FwDetailHeader(title: l10n.editListing, subtitle: crop),
                  const OfflineBanner(),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                    child: Form(
                      key: _formKey,
                      child: _buildForm(l10n),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
        bottomNavigationBar: FwBottomActions(
          children: [
            FilledButton(
              key: const Key('edit_listing_save'),
              onPressed: _isSubmitting ? null : _handleSave,
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(56),
                textStyle: const TextStyle(
                  fontFamily: AppTheme.fontFamily,
                  fontFamilyFallback: AppTheme.ethiopicFontFallback,
                  fontSize: 16,
                  fontWeight: FontWeight.w800,
                ),
              ),
              child: _isSubmitting
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
            // Web saves straight to the server, nothing waits in a browser.
            if (!kIsWeb) FwActionNote(l10n.savedOnPhoneFirstNote),
          ],
        ),
      ),
    );
  }

  Widget _buildForm(AppLocalizations l10n) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Text(l10n.editListingNote, style: LabeledField.helperStyle),
        const SizedBox(height: 18),
        LabeledFieldPair(
          first: LabeledField(
            label: l10n.quantityInQuintals,
            field: TextFormField(
              key: const Key('edit_listing_quantity'),
              controller: _quantityController,
              decoration: labeledFieldDecoration(suffixText: l10n.quintals),
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              // At most 9,999.99 quintals: the server's limit is 10,000.
              inputFormatters: [
                FilteringTextInputFormatter.allow(
                  RegExp(r'^\d{0,4}\.?\d{0,2}'),
                ),
              ],
              validator: (value) {
                final quantity = double.tryParse(value ?? '');
                return quantity == null || quantity <= 0
                    ? l10n.quantityRequired
                    : null;
              },
            ),
          ),
          second: LabeledField(
            label: l10n.priceInBirr,
            field: TextFormField(
              key: const Key('edit_listing_price'),
              controller: _priceController,
              decoration: labeledFieldDecoration(suffixText: l10n.etbPerKg),
              keyboardType: const TextInputType.numberWithOptions(
                decimal: true,
              ),
              // At most 999.99 ETB per kg: the server's limit is 1,000.
              inputFormatters: [
                FilteringTextInputFormatter.allow(
                  RegExp(r'^\d{0,3}\.?\d{0,2}'),
                ),
              ],
              validator: (value) {
                final price = double.tryParse(value ?? '');
                return price == null || price <= 0 ? l10n.priceRequired : null;
              },
            ),
          ),
        ),
        if (_totalLine(l10n) case final total?) ...[
          const SizedBox(height: 6),
          Text(
            total,
            key: const Key('edit_listing_total'),
            style: LabeledField.helperStyle,
          ),
        ],
        const SizedBox(height: 18),
        LabeledField(
          label: l10n.description,
          field: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              TextFormField(
                key: const Key('edit_listing_description'),
                controller: _descriptionController,
                decoration: labeledFieldDecoration(),
                maxLines: 3,
                maxLength: 500,
                keyboardType: TextInputType.multiline,
                // FUP-49: buyers read it before any order.
                validator: (value) =>
                    containsPhoneNumber(value) ? l10n.errorContactInText : null,
              ),
              const SizedBox(height: 6),
              Text(
                l10n.detailNoPhoneNumbersNote,
                style: LabeledField.helperStyle,
              ),
            ],
          ),
        ),
      ],
    );
  }
}
