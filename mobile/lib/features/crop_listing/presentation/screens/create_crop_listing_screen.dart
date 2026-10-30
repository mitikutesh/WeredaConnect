import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:uuid/uuid.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_bloc.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_event.dart';
import 'package:wereda_connect/features/crop_listing/presentation/bloc/crop_listing_state.dart';
import 'package:wereda_connect/features/delivery_verification/data/services/gps_capture_service.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/utils/contact_in_text.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/route_preview_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';
import 'package:wereda_connect/shared/widgets/voice_note_widget.dart';

/// Screen for creating a new crop listing (the Farmer · Create crop listing
/// board): the crop as three text tiles per row, quantity and price side by
/// side, the location with the farm drawn from the GPS fix, the description
/// and the voice note, and Save pinned at the foot.
///
/// - Large touch targets (48dp min)
/// - Localized labels and validation
/// - BlocListener for state transitions
/// - Loading spinner on submit
class CreateCropListingScreen extends StatefulWidget {
  /// Injectable GPS capture service (for testing).
  final GpsCaptureService? gpsCaptureService;

  const CreateCropListingScreen({this.gpsCaptureService, super.key});

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

  /// Pre-generated listing ID used for audio file naming.
  final String _listingId = const Uuid().v4();

  CropType? _selectedCropType;
  String? _audioNoteRef;

  /// The place chosen from the location selector, if any. Null for free text.
  Place? _selectedLocationPlace;

  late final GpsCaptureService _gpsCaptureService;
  GpsCaptureResult? _gpsResult;
  bool _gpsCapturing = false;

  /// Guards against accidental double-tap submission.
  bool _isSubmitting = false;

  @override
  void initState() {
    super.initState();
    _gpsCaptureService = widget.gpsCaptureService ?? GpsCaptureService();
    _captureGps();
    // The location card draws the towns around the farm from the place list.
    PlaceRepository.instance.ensureLoaded().then((_) {
      if (mounted) setState(() {});
    }).ignore();
    _locationController.addListener(_onLocationTyped);
  }

  void _onLocationTyped() {
    if (mounted) setState(() {});
  }

  Future<void> _captureGps() async {
    setState(() => _gpsCapturing = true);
    final result = await _gpsCaptureService.captureCurrentPosition();
    if (!mounted) return;
    setState(() {
      _gpsResult = result;
      _gpsCapturing = false;
    });
  }

  @override
  void dispose() {
    _locationController.removeListener(_onLocationTyped);
    _quantityController.dispose();
    _priceController.dispose();
    _locationController.dispose();
    _descriptionController.dispose();
    super.dispose();
  }

  void _handleSave() {
    // Prevent accidental double-tap submission
    if (_isSubmitting) return;
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

    // Prefer the device GPS fix; fall back to the picked place's centroid.
    // Never send 0,0: the server rejects it as CROP_VALIDATION.
    final latitude = _gpsResult?.latitude ?? _selectedLocationPlace?.lat;
    final longitude = _gpsResult?.longitude ?? _selectedLocationPlace?.lng;
    if (latitude == null || longitude == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(l10n.cropLocationCoordinatesRequired)),
      );
      return;
    }

    setState(() => _isSubmitting = true);

    context.read<CropListingBloc>().add(
          CreateCropListing(
            farmerId: authState.userId,
            farmerName: authState.userName,
            farmerPhone: authState.phoneNumber,
            cropType: _selectedCropType!.name,
            quantity: double.parse(_quantityController.text),
            pricePerKg: double.parse(_priceController.text),
            latitude: latitude,
            longitude: longitude,
            locationName: _locationController.text.trim(),
            description: _descriptionController.text.trim(),
            audioNoteRef: _audioNoteRef,
            woreda: authState.woreda,
            placeId: _selectedLocationPlace?.id,
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<CropListingBloc, CropListingState>(
      listener: (context, state) {
        if (state is CropListingCreated) {
          _isSubmitting = false;
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.listingSaved)),
          );
          Navigator.of(context).pop();
        } else if (state is CropListingError) {
          setState(() => _isSubmitting = false);
          if (state.message.contains('FRAUD_DUPLICATE_LISTING')) {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(
                content: Text(l10n.fraudDuplicateListing),
                backgroundColor: AppTheme.statusWarning,
              ),
            );
          } else if (state.message.contains('FRAUD_VELOCITY_EXCEEDED')) {
            // Server buckets listing velocity by fixed UTC clock-hour, so the
            // limit resets at the next UTC hour boundary.
            final minutesToReset = 60 - DateTime.now().toUtc().minute;
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(
                content: Text(l10n.fraudVelocityExceeded(minutesToReset)),
                backgroundColor: AppTheme.statusWarning,
              ),
            );
          } else {
            ScaffoldMessenger.of(context).showSnackBar(
              SnackBar(
                content: Text(localizeErrorCode(state.code, l10n)),
                backgroundColor: AppTheme.statusError,
              ),
            );
          }
        }
      },
      child: Scaffold(
        // The board's header: the square Back beside the title, on the page.
        body: SafeArea(
          bottom: false,
          child: SingleChildScrollView(
            child: FwContentWidth(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  FwDetailHeader(title: l10n.createListing),
                  Padding(
                    padding: const EdgeInsets.fromLTRB(20, 8, 20, 24),
                    child: Form(
                      key: _formKey,
                      child: _buildForm(context, l10n),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
        // Save pinned at the foot, as on the board. Driven by THIS screen's
        // own submission, not the shared bloc's CropListingLoading: the
        // still-mounted "My Crops" list re-dispatches LoadCropListings on
        // CropListingCreated, which would otherwise re-gray this button
        // around the pop.
        bottomNavigationBar: FwBottomActions(
          children: [
            FilledButton(
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
            FwActionNote(l10n.savedOnPhoneFirstNote),
          ],
        ),
      ),
    );
  }

  Widget _buildForm(BuildContext context, AppLocalizations l10n) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // Crop type selection
        Text(l10n.selectCropType, style: LabeledField.labelStyle),
        const SizedBox(height: 10),
        _CropTypeGrid(
          selectedCropType: _selectedCropType,
          onSelected: (type) {
            setState(() {
              _selectedCropType = type;
            });
          },
        ),
        const SizedBox(height: 18),

        // Quantity and price side by side, labels above
        // (Farmer · Create crop listing board).
        LabeledFieldPair(
          first: LabeledField(
            label: l10n.quantityInQuintals,
            field: TextFormField(
              key: const Key('create_listing_quantity'),
              controller: _quantityController,
              decoration: labeledFieldDecoration(
                suffixText: l10n.quintals,
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
          ),
          second: LabeledField(
            label: l10n.priceInBirr,
            field: TextFormField(
              key: const Key('create_listing_price'),
              controller: _priceController,
              decoration: labeledFieldDecoration(
                suffixText: l10n.etbPerKg,
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
          ),
        ),
        const SizedBox(height: 18),

        // Location: the place, then the board's card with the farm drawn
        // from the GPS fix (else the picked place) and the GPS state.
        LabeledField(
          label: l10n.location,
          field: PlaceSelector(
            key: const Key('create_listing_location'),
            controller: _locationController,
            labelAbove: true,
            showSearchIcon: false,
            hintText: l10n.wereda,
            onPlaceSelected: (place) =>
                setState(() => _selectedLocationPlace = place),
            validator: (value) {
              if (value == null || value.trim().isEmpty) {
                return l10n.locationRequired;
              }
              // FUP-49: buyers read it before any order.
              if (containsPhoneNumber(value)) {
                return l10n.errorContactInText;
              }
              return null;
            },
          ),
        ),
        const SizedBox(height: 8),
        _LocationCard(
          gpsResult: _gpsResult,
          isCapturing: _gpsCapturing,
          place: _selectedLocationPlace,
          locationName: _locationController.text.trim(),
          onRetry: _captureGps,
        ),
        const SizedBox(height: 18),

        // Description input
        LabeledField(
          label: l10n.description,
          field: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              TextFormField(
                key: const Key('create_listing_description'),
                controller: _descriptionController,
                decoration: labeledFieldDecoration(),
                maxLines: 3,
                keyboardType: TextInputType.multiline,
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
        const SizedBox(height: 16),

        // Audio voice note
        Builder(
          builder: (context) {
            final authState = context.read<AuthBloc>().state;
            final userId =
                authState is AuthAuthenticated ? authState.userId : '';
            return VoiceNoteWidget(
              audioFilePath: _audioNoteRef,
              userId: userId,
              entityId: _listingId,
              onRecorded: (path) {
                setState(() => _audioNoteRef = path);
              },
              onDeleted: () {
                setState(() => _audioNoteRef = null);
              },
            );
          },
        ),
      ],
    );
  }
}

/// The board's location card: the farm on a small drawing with the towns
/// around it, and the GPS state in the foot (captured as a green chip,
/// capturing, or unavailable with a retry). Without any position yet it is
/// the GPS state alone, with the hint to pick the wereda from the list.
class _LocationCard extends StatelessWidget {
  final GpsCaptureResult? gpsResult;
  final bool isCapturing;
  final Place? place;
  final String locationName;
  final VoidCallback onRetry;

  const _LocationCard({
    required this.gpsResult,
    required this.isCapturing,
    required this.place,
    required this.locationName,
    required this.onRetry,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final gps = gpsResult != null && gpsResult!.isAvailable ? gpsResult : null;
    final lat = gps?.latitude ?? place?.lat;
    final lng = gps?.longitude ?? place?.lng;
    final status = _GpsStatusIndicator(
      gpsResult: gpsResult,
      isCapturing: isCapturing,
      onRetry: onRetry,
      placeName: locationName.isNotEmpty
          ? locationName
          : place?.displayName(Localizations.localeOf(context).languageCode),
    );
    final hint = gps == null && !isCapturing
        ? Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Text(
              l10n.detailGpsPickFromList,
              style: LabeledField.helperStyle,
            ),
          )
        : null;
    if (lat == null || lng == null) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [status, if (hint != null) hint],
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        PlacePreviewCard(
          lat: lat,
          lng: lng,
          label: l10n.detailYourFarm,
          places: PlaceRepository.instance.isLoaded
              ? PlaceRepository.instance.places
              : const [],
          excludeId: gps == null ? place?.id : null,
          footer: status,
        ),
        if (hint != null) hint,
      ],
    );
  }
}

/// GPS status: capturing, the board's green "GPS Captured · place" chip, or
/// "GPS unavailable" in cream with a retry.
class _GpsStatusIndicator extends StatelessWidget {
  final GpsCaptureResult? gpsResult;
  final bool isCapturing;
  final VoidCallback onRetry;
  final String? placeName;

  const _GpsStatusIndicator({
    required this.gpsResult,
    required this.isCapturing,
    required this.onRetry,
    this.placeName,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    if (isCapturing) {
      return Row(
        children: [
          const SizedBox(
            width: 16,
            height: 16,
            child: CircularProgressIndicator(strokeWidth: 2),
          ),
          const SizedBox(width: 8),
          Text(l10n.deliveryGpsCapturing, style: LabeledField.helperStyle),
        ],
      );
    }

    if (gpsResult != null && gpsResult!.isAvailable) {
      final name = placeName;
      return Align(
        alignment: AlignmentDirectional.centerStart,
        child: FwPill(
          icon: Icons.check,
          label: name == null || name.isEmpty
              ? l10n.deliveryGpsCaptured
              : '${l10n.deliveryGpsCaptured} · $name',
          background: AppTheme.fieldGreenTint,
          foreground: AppTheme.fieldGreen,
        ),
      );
    }

    return Row(
      children: [
        FwPill(
          icon: Icons.warning_amber_rounded,
          label: l10n.deliveryGpsUnavailable,
          foreground: AppTheme.actionInk,
        ),
        const SizedBox(width: 4),
        IconButton(
          onPressed: onRetry,
          tooltip: l10n.retry,
          visualDensity: VisualDensity.compact,
          icon: const Icon(
            Icons.refresh,
            size: 18,
            color: AppTheme.actionInk,
          ),
        ),
      ],
    );
  }
}

/// The board's crop picker: three text tiles per row (46 tall, radius 14),
/// the chosen one filled green with a check.
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
    return LayoutBuilder(
      builder: (context, constraints) {
        const gap = 8.0;
        final width = (constraints.maxWidth - 2 * gap) / 3;
        return Wrap(
          spacing: gap,
          runSpacing: gap,
          children: CropType.values.map((type) {
            final isSelected = selectedCropType == type;
            return Semantics(
              key: Key('create_listing_crop_${type.name}'),
              button: true,
              selected: isSelected,
              child: Material(
                color: isSelected ? AppTheme.fieldGreen : AppTheme.surfaceWhite,
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(14),
                  side: isSelected
                      ? BorderSide.none
                      : const BorderSide(color: AppTheme.whisperBorder),
                ),
                clipBehavior: Clip.antiAlias,
                child: InkWell(
                  onTap: () => onSelected(type),
                  child: SizedBox(
                    width: width,
                    height: 46,
                    child: Row(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        if (isSelected) ...[
                          const Icon(
                            Icons.check,
                            size: 14,
                            color: Colors.white,
                          ),
                          const SizedBox(width: 6),
                        ],
                        Flexible(
                          child: Text(
                            type.displayName(l10n),
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                            style: TextStyle(
                              fontSize: 14,
                              fontWeight: isSelected
                                  ? FontWeight.w800
                                  : FontWeight.w600,
                              color:
                                  isSelected ? Colors.white : AppTheme.soilDark,
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            );
          }).toList(),
        );
      },
    );
  }
}
