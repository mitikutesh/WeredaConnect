import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_commitment.dart';
import 'package:wereda_connect/core/models/order_delivery_location.dart';
import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

// LT-14 (live-trip.md section 10): the order's own drop-off place. The picker
// on Place order and in the change sheet, and the card on the order detail.
// Same widgets on mobile and web (the place list is bundled, no network).

/// The drop-off place picker: the bundled place search, who sees the place
/// under it, and, with [showPickHint], that a typed name needs a pick from
/// the list (only a picked place has a point to send).
class DeliveryPlaceField extends StatelessWidget {
  const DeliveryPlaceField({
    required this.controller,
    required this.onPlaceSelected,
    this.onChanged,
    this.showPickHint = false,
    this.repository,
    super.key,
  });

  final TextEditingController controller;
  final ValueChanged<Place?> onPlaceSelected;
  final ValueChanged<String>? onChanged;
  final bool showPickHint;

  /// Injectable place list (defaults to the shared one); tests pass theirs.
  final PlaceRepository? repository;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return LabeledField(
      label: l10n.deliveryPlaceLabel,
      // A field with a suggestion list: see LabeledField.mergeSemantics.
      mergeSemantics: false,
      field: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          PlaceSelector(
            key: const Key('delivery_place_field'),
            controller: controller,
            labelAbove: true,
            showSearchIcon: false,
            repository: repository,
            onPlaceSelected: onPlaceSelected,
            onChanged: onChanged,
          ),
          const SizedBox(height: 6),
          Text(
            showPickHint
                ? l10n.deliveryPlacePickFromList
                : l10n.deliveryPlaceHelper,
            key: const Key('delivery_place_helper'),
            style: showPickHint
                ? LabeledField.helperStyle.copyWith(color: AppTheme.statusError)
                : LabeledField.helperStyle,
          ),
        ],
      ),
    );
  }
}

/// Asks the buyer for another drop-off place, starting from [initial].
/// Returns the picked place (one with a point in the box), or null when the
/// sheet was dismissed. Save waits for a pick from the list.
Future<Place?> showDeliveryPlaceSheet(
  BuildContext context, {
  Place? initial,
  PlaceRepository? repository,
}) =>
    showModalBottomSheet<Place>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (_) =>
          _DeliveryPlaceSheet(initial: initial, repository: repository),
    );

class _DeliveryPlaceSheet extends StatefulWidget {
  const _DeliveryPlaceSheet({this.initial, this.repository});

  final Place? initial;
  final PlaceRepository? repository;

  @override
  State<_DeliveryPlaceSheet> createState() => _DeliveryPlaceSheetState();
}

class _DeliveryPlaceSheetState extends State<_DeliveryPlaceSheet> {
  late final TextEditingController _controller = TextEditingController(
    text: widget.initial?.en ?? '',
  );
  late Place? _picked = widget.initial;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _typed(String text) {
    final picked = _picked;
    if (picked != null && text.trim() != picked.en) {
      setState(() => _picked = null);
    } else {
      setState(() {});
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final place = _picked;
    final canSave =
        deliveryPointOfPlace(place) != null && place?.id != widget.initial?.id;
    return SafeArea(
      child: SingleChildScrollView(
        padding: EdgeInsets.fromLTRB(
          24,
          20,
          24,
          16 + MediaQuery.viewInsetsOf(context).bottom,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Semantics(
              header: true,
              child: Text(
                l10n.deliveryPlaceLabel,
                style: Theme.of(
                  context,
                ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.bold),
              ),
            ),
            const SizedBox(height: 16),
            DeliveryPlaceField(
              controller: _controller,
              repository: widget.repository,
              showPickHint: place == null && _controller.text.trim().isNotEmpty,
              onPlaceSelected: (picked) => setState(() => _picked = picked),
              onChanged: _typed,
            ),
            const SizedBox(height: 16),
            FilledButton(
              key: const Key('delivery_place_save'),
              onPressed:
                  canSave ? () => Navigator.of(context).pop(place) : null,
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
              child: Text(l10n.save),
            ),
            const SizedBox(height: 4),
            TextButton(
              onPressed: () => Navigator.of(context).pop(),
              child: Text(l10n.cancel),
            ),
          ],
        ),
      ),
    );
  }
}

/// The drop-off place on the order detail of a trucked order: the place the
/// order's point names for every party the server sent it to (the buyer,
/// the farmer, the trucker while the money is held), "The truck's
/// destination" when the order has none, and for the buyer before pickup
/// Change ([onEdit]).
/// Nothing when there is no point and the viewer cannot set one.
class DeliveryPlaceCard extends StatefulWidget {
  const DeliveryPlaceCard({
    required this.order,
    required this.userId,
    this.onEdit,
    this.repository,
    super.key,
  });

  final Order order;
  final String userId;

  /// Opens the change sheet; null hides the button.
  final VoidCallback? onEdit;

  /// Injectable place list (defaults to the shared one); tests pass theirs.
  final PlaceRepository? repository;

  @override
  State<DeliveryPlaceCard> createState() => _DeliveryPlaceCardState();
}

class _DeliveryPlaceCardState extends State<DeliveryPlaceCard> {
  PlaceRepository get _places => widget.repository ?? PlaceRepository.instance;

  @override
  void initState() {
    super.initState();
    // The bundled list names the point; it loads once, on first need.
    if (!_places.isLoaded &&
        orderDeliveryPoint(widget.order) != null &&
        widget.repository == null) {
      _places.ensureLoaded().then((_) {
        if (mounted) setState(() {});
      }).ignore();
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final order = widget.order;
    final point = orderDeliveryPoint(order);
    final edit =
        canEditDeliveryLocation(order, widget.userId) ? widget.onEdit : null;
    if (!orderHasTrip(order) || (point == null && edit == null)) {
      return const SizedBox.shrink();
    }

    final String text;
    if (point == null) {
      text = l10n.deliveryPlaceNone;
    } else {
      final place = deliveryPlaceOf(order, _places);
      text = place?.displayName(Localizations.localeOf(context).languageCode) ??
          '${point.latitude.toStringAsFixed(3)}, '
              '${point.longitude.toStringAsFixed(3)}';
    }

    return FwCard(
      key: const Key('order_delivery_place'),
      padding: const EdgeInsets.fromLTRB(16, 14, 8, 14),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Padding(
            padding: EdgeInsets.only(top: 2),
            child: Icon(
              Icons.flag_outlined,
              size: 22,
              color: AppTheme.dropoffBlue,
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(l10n.deliveryPlaceLabel, style: LabeledField.labelStyle),
                const SizedBox(height: 2),
                Text(
                  text,
                  key: const Key('order_delivery_place_name'),
                  style: point == null
                      ? const TextStyle(
                          fontSize: 14,
                          height: 1.4,
                          color: AppTheme.mutedSteel,
                        )
                      : const TextStyle(
                          fontSize: 15,
                          height: 1.4,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.soilDark,
                        ),
                ),
              ],
            ),
          ),
          if (edit != null) ...[
            const SizedBox(width: 4),
            TextButton(
              key: const Key('order_delivery_place_edit'),
              onPressed: edit,
              child: Text(l10n.deliveryPlaceChange),
            ),
          ],
        ],
      ),
    );
  }
}
