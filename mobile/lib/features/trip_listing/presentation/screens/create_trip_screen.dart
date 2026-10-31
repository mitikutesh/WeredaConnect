import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:wereda_connect/core/database/isar_service.dart'
    if (dart.library.html) 'package:wereda_connect/core/database/isar_service_web.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/core/models/trip.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_bloc.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_event.dart';
import 'package:wereda_connect/features/trip_listing/presentation/bloc/trip_listing_state.dart';
import 'package:wereda_connect/features/trip_listing/presentation/widgets/route_preview_card.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/core/utils/contact_in_text.dart';
import 'package:wereda_connect/core/utils/phone_utils.dart';
import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/utils/board_day.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

/// Screen for creating a new trip listing (the Trucker · Create trip board).
///
/// One page: Repeat Last Trip, From and To with a swap button, a route
/// preview drawn on the phone, the departure date, capacity, price and the
/// vehicle. The driver's name and phone come from the profile and only show
/// as fields when the profile could not fill them, so the page asks for
/// seven things instead of the nine the old wizard split over three steps.
///
/// Save is pinned in a bottom action bar (thumb zone, always visible)
/// instead of buried at the end of the scroll.
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

  /// Set once the profile prefill finished: true when it left the driver's
  /// name or phone empty, so the page shows those two fields. Latched, so
  /// the fields stay while the trucker types into them.
  bool _showDriverFields = false;

  /// Guards against double-tap submission and drives the Save button's spinner
  /// from THIS screen's own submit — not the shared bloc's TripListingLoading,
  /// which the still-mounted "My Trips" list re-triggers on TripCreated.
  bool _isSubmitting = false;

  final _truckerNameController = TextEditingController();
  final _truckerPhoneController = TextEditingController();
  final _vehiclePlateController = TextEditingController();
  final _vehicleDescriptionController = TextEditingController();
  final _fromWoredaController = TextEditingController();
  final _toWoredaController = TextEditingController();
  final _capacityController = TextEditingController();
  final _priceController = TextEditingController();

  /// Places chosen from the origin/destination selectors. Null for free text
  /// (including quick-fill chips, which set the text directly).
  Place? _fromPlace;
  Place? _toPlace;

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

    _autoFillTruckerInfo().whenComplete(() {
      if (mounted) {
        setState(() => _showDriverFields |= _driverInfoMissing);
      }
    });
    _loadRecentRoutes();
    // The route preview draws towns from the bundled place list.
    PlaceRepository.instance.ensureLoaded().then((_) {
      if (mounted) setState(() {});
    }).ignore();
  }

  bool get _driverInfoMissing =>
      _truckerNameController.text.trim().isEmpty ||
      !isValidEthiopianPhone(_truckerPhoneController.text.trim());

  /// Auto-fill trucker name/phone from Isar user profile (mobile) or
  /// AuthAuthenticated state (web, where Isar is unavailable).
  Future<void> _autoFillTruckerInfo() async {
    if (_truckerNameController.text.isNotEmpty &&
        _truckerPhoneController.text.isNotEmpty) {
      return;
    }

    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) return;

    // On web, Isar is unavailable — fall back to AuthBloc state.
    if (kIsWeb) {
      if (mounted) {
        setState(() {
          if (_truckerNameController.text.isEmpty &&
              authState.userName.isNotEmpty) {
            _truckerNameController.text = authState.userName;
          }
          if (_truckerPhoneController.text.isEmpty &&
              authState.phoneNumber != null &&
              authState.phoneNumber!.isNotEmpty) {
            _truckerPhoneController.text = authState.phoneNumber!;
          }
        });
      }
      return;
    }

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
      // The repository rejects same-day departures (must be strictly future),
      // so the earliest selectable date is tomorrow.
      firstDate: DateUtils.dateOnly(now).add(const Duration(days: 1)),
      lastDate: now.add(const Duration(days: 30)),
    );
    if (picked != null) {
      setState(() {
        _selectedDepartureDate = picked;
      });
    }
  }

  /// Validates the page, then saves. A missing departure date or driver
  /// detail says so instead of saving.
  void _handleSaveTapped() {
    final l10n = AppLocalizations.of(context)!;
    if (_driverInfoMissing && !_showDriverFields) {
      // The driver fields are not on the page yet, so this validation pass
      // cannot see them: show them and stop here, never save without them.
      setState(() => _showDriverFields = true);
      _formKey.currentState?.validate();
      return;
    }
    if (!(_formKey.currentState?.validate() ?? false)) return;
    if (_selectedDepartureDate == null) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(l10n.departureDateRequired)),
      );
      return;
    }
    _handleSave();
  }

  /// Swaps From and To, with any place picked for them.
  void _swapRoute() {
    setState(() {
      final fromText = _fromWoredaController.text;
      _fromWoredaController.text = _toWoredaController.text;
      _toWoredaController.text = fromText;
      final fromPlace = _fromPlace;
      _fromPlace = _toPlace;
      _toPlace = fromPlace;
    });
  }

  void _handleSave() {
    if (_isSubmitting) return;
    final l10n = AppLocalizations.of(context)!;

    final authState = context.read<AuthBloc>().state;
    if (authState is! AuthAuthenticated) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text(l10n.notAuthenticated)),
      );
      return;
    }

    final vehiclePlate = _vehiclePlateController.text.trim();
    final vehicleDescription = _vehicleDescriptionController.text.trim();

    final normalizedPhone =
        normalizeEthiopianPhone(_truckerPhoneController.text.trim()) ??
            _truckerPhoneController.text.trim();
    setState(() => _isSubmitting = true);
    context.read<TripListingBloc>().add(
          CreateTrip(
            truckerId: authState.userId,
            truckerName: _truckerNameController.text.trim(),
            truckerPhone: normalizedPhone,
            vehiclePlate: vehiclePlate.isNotEmpty ? vehiclePlate : null,
            vehicleDescription:
                vehicleDescription.isNotEmpty ? vehicleDescription : null,
            fromWoreda: _fromWoredaController.text.trim(),
            toWoreda: _toWoredaController.text.trim(),
            departureDate: _selectedDepartureDate!,
            capacity: double.parse(_capacityController.text),
            pricePerQuintal: double.parse(_priceController.text),
            fromPlaceId: _fromPlace?.id,
            toPlaceId: _toPlace?.id,
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return BlocListener<TripListingBloc, TripListingState>(
      listener: (context, state) {
        if (state is TripCreated) {
          _isSubmitting = false;
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.tripCreatedSuccess)),
          );
          Navigator.of(context).pop();
        } else if (state is TripListingError) {
          setState(() => _isSubmitting = false);
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text(localizeErrorCode(state.code, l10n)),
              backgroundColor: AppTheme.statusError,
            ),
          );
        }
      },
      child: Scaffold(
        // The board's header: the square Back beside the title, on the page.
        body: SafeArea(
          bottom: false,
          child: Column(
            children: [
              const OfflineBanner(),
              Expanded(
                child: SingleChildScrollView(
                  child: FwContentWidth(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        FwDetailHeader(title: l10n.createTrip),
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
            ],
          ),
        ),
        // Pinned action bar: Save is always visible and in the thumb zone,
        // never buried at the end of the scroll.
        bottomNavigationBar: BlocBuilder<TripListingBloc, TripListingState>(
          builder: (context, state) {
            final isLoading = state is TripListingLoading;
            return FwBottomActions(
              children: [
                FilledButton(
                  onPressed: isLoading ? null : _handleSaveTapped,
                  style: FilledButton.styleFrom(
                    backgroundColor: AppTheme.soilDark,
                    minimumSize: const Size.fromHeight(56),
                    textStyle: const TextStyle(
                      fontFamily: AppTheme.fontFamily,
                      fontFamilyFallback: AppTheme.ethiopicFontFallback,
                      fontSize: 16,
                      fontWeight: FontWeight.w800,
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
                      : Text(l10n.save),
                ),
                FwActionNote(l10n.savedOnPhoneFirstNote),
              ],
            );
          },
        ),
      ),
    );
  }

  // ── The form ────────────────────────────────────────────────────────
  Widget _buildForm(AppLocalizations l10n) {
    final lastRoute = _recentRoutes.isNotEmpty ? _recentRoutes.first : null;
    final from = _fromPlace;
    final to = _toPlace;
    final showPreview = routeDistanceKm(from, to) != null;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      // Same layout, but the fields paint bottom-up (list reversed, laid out
      // upward). Flutter 3.38 puts a PlaceSelector's suggestion list at the
      // field's own place in the semantics tree, so on web a field painted
      // after it (the date) sat above the list and took the click (SIM-59).
      verticalDirection: VerticalDirection.up,
      children: <Widget>[
        if (lastRoute != null) ...[
          Align(
            alignment: AlignmentDirectional.centerStart,
            child: _RepeatLastTripButton(
              label: '${l10n.repeatLastTrip} · '
                  '${lastRoute.from} → ${lastRoute.to}',
              onPressed: () {
                setState(() {
                  _fromWoredaController.text = lastRoute.from;
                  _toWoredaController.text = lastRoute.to;
                  // Quick fill sets raw text, not a picked place: drop any
                  // resolved place ids.
                  _fromPlace = null;
                  _toPlace = null;
                });
              },
            ),
          ),
          const SizedBox(height: 16),
        ],
        Stack(
          children: [
            Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              verticalDirection: VerticalDirection.up,
              children: <Widget>[
                LabeledField(
                  label: l10n.fromWoreda,
                  // Full width: the swap button sits over the right end of
                  // both fields, as on the board.
                  field: PlaceSelector(
                    key: const Key('create_trip_from'),
                    controller: _fromWoredaController,
                    labelAbove: true,
                    showSearchIcon: false,
                    prefix: const _RouteEndDot(origin: true),
                    onPlaceSelected: (place) =>
                        setState(() => _fromPlace = place),
                    validator: (value) {
                      if (value == null || value.trim().isEmpty) {
                        return l10n.fromWoredaRequired;
                      }
                      // FUP-49: buyers read the route before any order.
                      if (containsPhoneNumber(value)) {
                        return l10n.errorContactInText;
                      }
                      return null;
                    },
                  ),
                ),
                const SizedBox(height: 12),
                LabeledField(
                  label: l10n.toWoreda,
                  field: PlaceSelector(
                    key: const Key('create_trip_to'),
                    controller: _toWoredaController,
                    labelAbove: true,
                    showSearchIcon: false,
                    prefix: const _RouteEndDot(origin: false),
                    onPlaceSelected: (place) =>
                        setState(() => _toPlace = place),
                    validator: (value) {
                      if (value == null || value.trim().isEmpty) {
                        return l10n.toWoredaRequired;
                      }
                      if (value.trim() == _fromWoredaController.text.trim()) {
                        return l10n.sameOriginDestination;
                      }
                      if (containsPhoneNumber(value)) {
                        return l10n.errorContactInText;
                      }
                      return null;
                    },
                  ),
                ),
              ].reversed.toList(),
            ),
            // Centred on the gap between From and To, as on the board.
            PositionedDirectional(
              end: 10,
              top: 62,
              child: Tooltip(
                message: l10n.createTripSwapRoute,
                child: OutlinedButton(
                  key: const Key('create_trip_swap'),
                  onPressed: _swapRoute,
                  style: OutlinedButton.styleFrom(
                    minimumSize: const Size(44, 44),
                    fixedSize: const Size(44, 44),
                    padding: EdgeInsets.zero,
                    backgroundColor: Colors.white,
                    foregroundColor: AppTheme.soilDark,
                    side: const BorderSide(color: Color(0xFFD6D3C8)),
                    shape: RoundedRectangleBorder(
                      borderRadius: BorderRadius.circular(14),
                    ),
                  ),
                  child: const Icon(Icons.swap_vert, size: 20),
                ),
              ),
            ),
          ],
        ),
        if (showPreview) ...[
          const SizedBox(height: 16),
          RoutePreviewCard(
            from: from!,
            to: to!,
            places: PlaceRepository.instance.isLoaded
                ? PlaceRepository.instance.places
                : const [],
          ),
        ],
        const SizedBox(height: 16),
        LabeledField(
          label: l10n.departureDate,
          field: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              DateFieldButton(
                key: const Key('create_trip_departure_date'),
                text: _selectedDepartureDate != null
                    ? formatBoardDate(_selectedDepartureDate!)
                    : l10n.selectDepartureDate,
                placeholder: _selectedDepartureDate == null,
                onTap: _pickDepartureDate,
              ),
              const SizedBox(height: 6),
              Text(
                l10n.createTripBookingCutoffNote,
                style: LabeledField.helperStyle,
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),
        // Two per row with the labels above, as on the board; a label
        // wraps instead of being cut (Afaan Oromoo labels run long).
        LabeledFieldPair(
          first: LabeledField(
            label: l10n.capacity,
            field: TextFormField(
              key: const Key('create_trip_capacity'),
              controller: _capacityController,
              decoration: labeledFieldDecoration(suffixText: l10n.quintals),
              keyboardType:
                  const TextInputType.numberWithOptions(decimal: true),
              inputFormatters: [
                FilteringTextInputFormatter.allow(RegExp(r'^\d+\.?\d{0,2}')),
              ],
              validator: (value) {
                if (value == null || value.isEmpty) {
                  return l10n.capacityRequired;
                }
                final capacity = double.tryParse(value);
                if (capacity == null || capacity < 0.1 || capacity > 1000) {
                  return l10n.invalidCapacity;
                }
                return null;
              },
            ),
          ),
          second: LabeledField(
            label: l10n.pricePerQuintal,
            field: TextFormField(
              key: const Key('create_trip_price'),
              controller: _priceController,
              decoration: labeledFieldDecoration(suffixText: 'ETB'),
              keyboardType:
                  const TextInputType.numberWithOptions(decimal: true),
              inputFormatters: [
                FilteringTextInputFormatter.allow(RegExp(r'^\d+\.?\d{0,2}')),
              ],
              validator: (value) {
                if (value == null || value.isEmpty) {
                  return l10n.priceRequired;
                }
                final price = double.tryParse(value);
                if (price == null || price < 0.01 || price > 10000) {
                  return l10n.invalidPrice;
                }
                return null;
              },
            ),
          ),
        ),
        const SizedBox(height: 16),
        LabeledFieldPair(
          first: LabeledField(
            label: l10n.vehiclePlate,
            field: TextFormField(
              key: const Key('create_trip_plate'),
              controller: _vehiclePlateController,
              decoration: labeledFieldDecoration(hintText: '3-AA-12345'),
              keyboardType: TextInputType.text,
              textCapitalization: TextCapitalization.characters,
              // FUP-49: the vehicle fields reach every buyer with the trip.
              validator: (value) =>
                  containsPhoneNumber(value) ? l10n.errorContactInText : null,
            ),
          ),
          second: LabeledField(
            label: l10n.vehicleDescription,
            field: TextFormField(
              key: const Key('create_trip_vehicle'),
              controller: _vehicleDescriptionController,
              decoration: labeledFieldDecoration(hintText: 'Isuzu FSR 5 t'),
              keyboardType: TextInputType.text,
              textCapitalization: TextCapitalization.words,
              validator: (value) =>
                  containsPhoneNumber(value) ? l10n.errorContactInText : null,
            ),
          ),
        ),
        if (_showDriverFields) ...[
          const SizedBox(height: 20),
          Text(
            l10n.createTripDriverHint,
            style: const TextStyle(fontSize: 13, color: AppTheme.mutedSteel),
          ),
          const SizedBox(height: 12),
          LabeledField(
            label: l10n.truckerName,
            field: TextFormField(
              controller: _truckerNameController,
              decoration: labeledFieldDecoration(),
              keyboardType: TextInputType.name,
              textCapitalization: TextCapitalization.words,
              validator: (value) {
                if (value == null || value.trim().isEmpty) {
                  return l10n.truckerNameRequired;
                }
                return null;
              },
            ),
          ),
          const SizedBox(height: 16),
          LabeledField(
            label: l10n.truckerPhone,
            field: TextFormField(
              controller: _truckerPhoneController,
              decoration: labeledFieldDecoration(hintText: '09XXXXXXXX'),
              keyboardType: TextInputType.phone,
              validator: (value) {
                if (value == null || value.trim().isEmpty) {
                  return l10n.invalidPhoneFormat;
                }
                if (!isValidEthiopianPhone(value.trim())) {
                  return l10n.invalidPhoneFormat;
                }
                return null;
              },
            ),
          ),
        ],
      ].reversed.toList(),
    );
  }
}

/// The board's dashed amber "Repeat Last Trip" button.
class _RepeatLastTripButton extends StatelessWidget {
  const _RepeatLastTripButton({required this.label, required this.onPressed});

  final String label;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) {
    return Material(
      color: const Color(0xFFFAEED8),
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(12),
        side: const BorderSide(color: Color(0xFFD9922B)),
      ),
      child: InkWell(
        onTap: onPressed,
        borderRadius: BorderRadius.circular(12),
        child: ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 48),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                const Icon(Icons.replay, size: 18, color: Color(0xFF6B400B)),
                const SizedBox(width: 8),
                Flexible(
                  child: Text(
                    label,
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w700,
                      color: Color(0xFF6B400B),
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

/// The board's marker inside From and To: a green ring for the origin, a
/// blue dot for the destination (the colours the route preview uses).
class _RouteEndDot extends StatelessWidget {
  const _RouteEndDot({required this.origin});

  final bool origin;

  @override
  Widget build(BuildContext context) {
    // A fixed slot: the prefix only gets a minimum width, so an unsized
    // Center would stretch over the whole field and hide the input.
    return SizedBox(
      width: 40,
      height: 40,
      child: Center(
        child: Container(
          width: 12,
          height: 12,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: origin ? Colors.white : AppTheme.dropoffBlue,
            border: origin
                ? Border.all(color: AppTheme.fieldGreen, width: 3)
                : null,
          ),
        ),
      ),
    );
  }
}
