import 'dart:async';

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
import 'package:wereda_connect/features/crop_listing/domain/repositories/base_crop_listing_repository.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
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
/// description and no phone number in it. Only the changed fields are sent,
/// at the version of [listing] (the copy the form shows). Pops with an
/// [EditListingResult]: the saved listing, or that the listing changed
/// since the form opened (nothing saved), or null when nothing changed.
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

  /// The typed number when the farmer changed it, null while the field
  /// still shows the listing's own: an untouched field is not sent (the free
  /// stock can carry more decimals than the field, and an order may have
  /// taken some meanwhile).
  static double? _changedNumber(
    TextEditingController controller,
    double original,
  ) =>
      controller.text == _plain(original)
          ? null
          : double.parse(controller.text);

  Future<void> _handleSave() async {
    if (_isSubmitting) return;
    if (!_formKey.currentState!.validate()) return;
    final l10n = AppLocalizations.of(context)!;
    final navigator = Navigator.of(context);
    final quantity =
        _changedNumber(_quantityController, widget.listing.quantity);
    final pricePerKg =
        _changedNumber(_priceController, widget.listing.pricePerKg);
    final typed = _descriptionController.text.trim();
    final description =
        typed == widget.listing.description.trim() ? null : typed;
    if (quantity == null && pricePerKg == null && description == null) {
      navigator.pop();
      return;
    }

    setState(() => _isSubmitting = true);
    // This edit's own answer: the bloc is app-wide, and the list under this
    // screen loads through it too.
    final done = Completer<CropListing>();
    context.read<CropListingBloc>().add(
          UpdateCropListing(
            listing: widget.listing,
            farmerId: widget.farmerId,
            quantity: quantity,
            pricePerKg: pricePerKg,
            description: description,
            done: done,
          ),
        );
    try {
      final saved = await done.future;
      if (!mounted) return;
      // Saved and sent, or saved on the phone to send later (E-5).
      showSavedOrSent(context, sent: l10n.listingSaved).ignore();
      navigator.pop(EditListingResult.saved(saved));
    } on ListingChangeWaitingException {
      // An earlier change of this listing has not gone out yet (the phone's
      // queue): saving on top of it could undo an order's stock.
      if (!mounted) return;
      setState(() => _isSubmitting = false);
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(l10n.editListingWaiting)),
      );
    } on Object catch (e) {
      if (!mounted) return;
      final code = mapErrorToCode(e);
      if (code == AppErrorCode.conflict) {
        // The listing changed since the form read it (an order took stock):
        // nothing was written; the detail reads it again and says so.
        navigator.pop(const EditListingResult.changedElsewhere());
        return;
      }
      setState(() => _isSubmitting = false);
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(localizeErrorCode(code, l10n, message: '$e')),
          backgroundColor: AppTheme.statusError,
        ),
      );
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final crop = CropType.labelFor(widget.listing.cropType, l10n);

    return Scaffold(
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

/// How [EditCropListingScreen] ended, for the detail under it.
class EditListingResult {
  /// The listing as saved.
  final CropListing? saved;

  /// The listing changed since the form read it (an order took stock, or a
  /// newer copy arrived): nothing was saved and the detail must read it
  /// again before the farmer edits.
  final bool changedElsewhere;

  const EditListingResult.saved(CropListing this.saved)
      : changedElsewhere = false;

  const EditListingResult.changedElsewhere()
      : saved = null,
        changedElsewhere = true;
}
