import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Typeahead place picker that replaces the app's free-text woreda/location
/// fields.
///
/// Behaviour (see `assets/data/ethiopia_woredas.SOURCE.md` for the dataset):
/// - Matches as the user types across en / am / om / alternate names; Latin
///   input is accent-folded, Ge'ez is matched directly (see [PlaceRepository]).
/// - Prefix matches rank above substring; results capped at [maxResults].
/// - Each result shows the place's name in the app language
///   ([Place.displayName]: no transliteration marks, current official names)
///   with its other names beneath.
/// - Selecting a result writes the **canonical English name** ([Place.en]) into
///   [controller] (so server-side substring matching keeps working) and reports
///   the chosen [Place] via [onPlaceSelected]. The place **id** is what callers
///   persist; the display string is kept for matching/back-compat.
/// - It is a **combobox, not a closed list**: free text is always a valid entry
///   (rural places may be off-list). When the user types instead of picking,
///   [onPlaceSelected] is cleared to null and the raw text is kept.
/// - Keyboard navigable (up/down/enter to pick, escape to dismiss) and
///   accessible (labelled field + results region).
///
/// Pure Flutter — works on mobile and web. The caller owns [controller] so
/// existing form validators that read `controller.text` keep working unchanged.
class PlaceSelector extends StatefulWidget {
  const PlaceSelector({
    required this.controller,
    this.labelText,
    this.hintText,
    this.prefixIcon = Icons.location_on_outlined,
    this.validator,
    this.onPlaceSelected,
    this.onChanged,
    this.textCapitalization = TextCapitalization.words,
    this.maxLength,
    this.enabled = true,
    this.repository,
    this.debounce = const Duration(milliseconds: 150),
    this.minQueryLength = 2,
    this.maxResults = 8,
    this.isDense = false,
    this.contentPadding,
    this.labelAbove = false,
    this.prefix,
    this.showSearchIcon = true,
    this.pickerLook = false,
    this.selectAllOnFocus = false,
    super.key,
  });

  /// Selects the whole text when the field gains focus, for a field that
  /// starts prefilled: typing then replaces the name instead of landing in
  /// the middle of it (BUG-18).
  final bool selectAllOnFocus;

  /// Caller-owned controller. Holds the canonical English name after a pick
  /// (with " · region" in [pickerLook]), or the raw free text otherwise.
  final TextEditingController controller;

  final String? labelText;
  final String? hintText;
  final IconData prefixIcon;

  /// Optional form validator (reads the current text), so the field behaves
  /// like the `TextFormField` it replaces inside a `Form`.
  final String? Function(String?)? validator;

  /// Fired with the picked [Place], or null when the user edits to free text.
  final ValueChanged<Place?>? onPlaceSelected;

  /// Fired on every user edit (not on programmatic selection).
  final ValueChanged<String>? onChanged;

  final TextCapitalization textCapitalization;
  final int? maxLength;
  final bool enabled;

  /// Injectable repository (defaults to the shared singleton). Tests pass a
  /// pre-loaded instance.
  final PlaceRepository? repository;

  final Duration debounce;
  final int minQueryLength;
  final int maxResults;

  /// Renders the field in a compact (dense) layout — used for tight filter rows.
  final bool isDense;

  /// Overrides the field's content padding. Defaults to a comfortable
  /// H16/V20 (or a compact H12/V10 when [isDense] is true and this is null).
  final EdgeInsetsGeometry? contentPadding;

  /// The Fieldway boards' field: its label sits above it (the caller wraps
  /// it in a `LabeledField`), so no floating label, a 48 tall box with
  /// radius 14 and the hint inside.
  final bool labelAbove;

  /// Replaces [prefixIcon] when set (the boards' origin ring and
  /// destination dot on Create trip).
  final Widget? prefix;

  /// The search glass at the end of the field; the boards draw none.
  final bool showSearchIcon;

  /// The Register personal board's picker: a picked place reads with its
  /// region ("Meki · Oromia") and the field ends in a chevron instead of the
  /// search glass. The place itself still reaches [onPlaceSelected].
  final bool pickerLook;

  @override
  State<PlaceSelector> createState() => _PlaceSelectorState();
}

class _PlaceSelectorState extends State<PlaceSelector>
    with WidgetsBindingObserver {
  late final PlaceRepository _repository;
  final FocusNode _focusNode = FocusNode();

  Timer? _debounceTimer;
  Place? _selectedPlace;

  /// Whether the most recent debounced query returned zero matches (drives the
  /// non-blocking "use what you typed" hint).
  bool _showNoResultsHint = false;

  @override
  void initState() {
    super.initState();
    _repository = widget.repository ?? PlaceRepository.instance;
    // Lazy-load the dataset on first focus rather than at app boot.
    _focusNode.addListener(_handleFocusChange);
    WidgetsBinding.instance.addObserver(this);
  }

  /// The window changed, for one when the on-screen keyboard comes up.
  @override
  void didChangeMetrics() => _liftAboveKeyboard();

  /// After the frame, so the tap that gave the focus has placed its caret.
  void _selectAllAfterFocus() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_focusNode.hasFocus) return;
      final text = widget.controller.text;
      if (text.isEmpty) return;
      widget.controller.selection =
          TextSelection(baseOffset: 0, extentOffset: text.length);
    });
  }

  /// BUG-38: the suggestions open under the field. On a phone the keyboard
  /// covers the lower half of the page, so a field that only just clears it
  /// shows no suggestion. While the keyboard is up and the field has the
  /// focus, the field is scrolled to the top of its scroll view so the list
  /// has room. Nothing moves without an on-screen keyboard (desktop web) or
  /// outside a scroll view (the sheets).
  void _liftAboveKeyboard() {
    if (!mounted || !_focusNode.hasFocus) return;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted || !_focusNode.hasFocus) return;
      if (View.of(context).viewInsets.bottom <= 0) return;
      if (Scrollable.maybeOf(context) == null) return;
      Scrollable.ensureVisible(context, alignment: 0.05).ignore();
    });
  }

  void _handleFocusChange() {
    if (_focusNode.hasFocus && !_repository.isLoaded) {
      _repository.ensureLoaded().then((_) {
        if (mounted) setState(() {});
      });
    }
    if (!_focusNode.hasFocus && _showNoResultsHint) {
      setState(() => _showNoResultsHint = false);
    }
    if (_focusNode.hasFocus) {
      if (widget.selectAllOnFocus) _selectAllAfterFocus();
      // The keyboard may already be up (focus moved from another field).
      _liftAboveKeyboard();
    }
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _debounceTimer?.cancel();
    _focusNode.removeListener(_handleFocusChange);
    _focusNode.dispose();
    super.dispose();
  }

  /// Debounced async options. Returns the previous frame's matches only after
  /// [PlaceSelector.debounce] of quiet typing, ignoring stale queries.
  Future<Iterable<Place>> _optionsBuilder(TextEditingValue value) {
    _debounceTimer?.cancel();
    final query = value.text.trim();
    if (query.length < widget.minQueryLength) {
      if (_showNoResultsHint) {
        scheduleMicrotask(() {
          if (mounted) setState(() => _showNoResultsHint = false);
        });
      }
      return Future.value(const Iterable<Place>.empty());
    }

    final completer = Completer<Iterable<Place>>();
    _debounceTimer = Timer(widget.debounce, () {
      // Drop stale results if the user kept typing.
      if (widget.controller.text.trim() != query) {
        completer.complete(const Iterable<Place>.empty());
        return;
      }
      final results = _repository.isLoaded
          ? _repository.search(query, limit: widget.maxResults)
          : const <Place>[];
      final empty = results.isEmpty;
      if (empty != _showNoResultsHint && mounted) {
        setState(() => _showNoResultsHint = empty);
      }
      completer.complete(results);
    });
    return completer.future;
  }

  void _handleSelected(Place place) {
    // RawAutocomplete sets controller.text to displayStringForOption.
    _selectedPlace = place;
    _showNoResultsHint = false;
    widget.onPlaceSelected?.call(place);
    _focusNode.unfocus();
  }

  /// What the field shows for a picked [place].
  String _display(Place place) => widget.pickerLook && place.region.isNotEmpty
      ? '${place.en} · ${place.region}'
      : place.en;

  void _handleFieldChanged(String text) {
    // User typed: if it diverges from the picked place, drop the selection.
    if (_selectedPlace != null && text.trim() != _display(_selectedPlace!)) {
      _selectedPlace = null;
      widget.onPlaceSelected?.call(null);
    }
    widget.onChanged?.call(text);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return RawAutocomplete<Place>(
      textEditingController: widget.controller,
      focusNode: _focusNode,
      displayStringForOption: _display,
      optionsBuilder: _optionsBuilder,
      onSelected: _handleSelected,
      fieldViewBuilder: (context, controller, focusNode, onFieldSubmitted) {
        return CallbackShortcuts(
          bindings: {
            const SingleActivator(LogicalKeyboardKey.escape): focusNode.unfocus,
          },
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              TextFormField(
                controller: controller,
                focusNode: focusNode,
                enabled: widget.enabled,
                validator: widget.validator,
                onChanged: _handleFieldChanged,
                onFieldSubmitted: (_) => onFieldSubmitted(),
                textCapitalization: widget.textCapitalization,
                maxLength: widget.maxLength,
                autovalidateMode: AutovalidateMode.onUserInteraction,
                decoration: InputDecoration(
                  labelText: widget.labelAbove ? null : widget.labelText,
                  hintText: widget.hintText ?? l10n.placeSearchHint,
                  prefixIcon: widget.prefix ?? Icon(widget.prefixIcon),
                  prefixIconConstraints: widget.prefix == null
                      ? null
                      : const BoxConstraints(minWidth: 40, minHeight: 40),
                  suffixIcon: widget.pickerLook
                      ? const Icon(
                          Icons.keyboard_arrow_down,
                          color: AppTheme.slate,
                        )
                      : widget.showSearchIcon
                          ? const Icon(Icons.search, color: AppTheme.slate)
                          : null,
                  isDense: widget.isDense,
                  // Long validator texts (FUP-49 phone refusal) wrap, not clip.
                  errorMaxLines: 3,
                  contentPadding: widget.contentPadding ??
                      (widget.isDense
                          ? const EdgeInsets.symmetric(
                              horizontal: 12,
                              vertical: 10,
                            )
                          : widget.labelAbove
                              ? const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 15,
                                )
                              : const EdgeInsets.symmetric(
                                  horizontal: 16,
                                  vertical: 20,
                                )),
                ),
              ),
              if (_showNoResultsHint && focusNode.hasFocus)
                Padding(
                  padding: const EdgeInsets.only(top: 4, left: 12),
                  child: Text(
                    l10n.placeNoResults,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AppTheme.slate,
                        ),
                  ),
                ),
            ],
          ),
        );
      },
      optionsViewBuilder: (context, onSelected, options) {
        return Align(
          alignment: Alignment.topLeft,
          child: Material(
            // The suggestion list: white, radius 16, the quiet card edge.
            elevation: 6,
            color: AppTheme.ground,
            shadowColor: Theme.of(context).colorScheme.shadow,
            clipBehavior: Clip.antiAlias,
            shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(16),
              side: const BorderSide(color: AppTheme.line),
            ),
            child: Semantics(
              container: true,
              label: l10n.placeResultsLabel,
              child: ConstrainedBox(
                constraints:
                    const BoxConstraints(maxHeight: 280, maxWidth: 520),
                child: ListView.separated(
                  padding: EdgeInsets.zero,
                  shrinkWrap: true,
                  itemCount: options.length,
                  separatorBuilder: (_, __) =>
                      const Divider(height: 1, color: AppTheme.line),
                  itemBuilder: (context, index) {
                    final place = options.elementAt(index);
                    final highlighted =
                        AutocompleteHighlightedOption.of(context) == index;
                    return _PlaceOptionTile(
                      place: place,
                      highlighted: highlighted,
                      onTap: () => onSelected(place),
                    );
                  },
                ),
              ),
            ),
          ),
        );
      },
    );
  }
}

/// One result row: the name in the app language, the place's other names
/// beneath (only when known), and a muted region/zone suffix to disambiguate
/// repeated names.
class _PlaceOptionTile extends StatelessWidget {
  const _PlaceOptionTile({
    required this.place,
    required this.highlighted,
    required this.onTap,
  });

  final Place place;
  final bool highlighted;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final title =
        place.displayName(Localizations.localeOf(context).languageCode);
    // The other names, each once. The cleaned canonical name comes last so a
    // renamed place ("Adama") also shows the name a pick writes ("Nazret").
    final shownKeys = {placeSearchKey(title)};
    final otherNames = <String>[];
    for (final name in [
      place.displayEn,
      place.amGeez,
      place.om,
      cleanPlaceName(place.en),
    ]) {
      if (name.isNotEmpty && shownKeys.add(placeSearchKey(name))) {
        otherNames.add(name);
      }
    }
    final subtitle = otherNames.join('  ·  ');
    final locality = [
      if (place.zone.isNotEmpty) place.zone,
      if (place.region.isNotEmpty) place.region,
    ].join(', ');

    return InkWell(
      onTap: onTap,
      // On web with semantics on, a click focuses a focusable tile first,
      // the field loses focus, and RawAutocomplete closes the list before
      // the tap arrives (SIM-59). Keyboard picking stays on the field
      // (up/down + enter).
      canRequestFocus: false,
      child: Container(
        color: highlighted ? AppTheme.paper : Colors.transparent,
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Icon(
              Icons.location_on_outlined,
              size: 18,
              color: AppTheme.slate,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    title,
                    style: theme.textTheme.bodyMedium
                        ?.copyWith(fontWeight: FontWeight.w700),
                  ),
                  if (subtitle.isNotEmpty)
                    Text(
                      subtitle,
                      style: theme.textTheme.bodySmall
                          ?.copyWith(color: AppTheme.slate),
                    ),
                ],
              ),
            ),
            if (locality.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(left: 8),
                child: Text(
                  locality,
                  style: theme.textTheme.labelSmall
                      ?.copyWith(color: AppTheme.slate),
                ),
              ),
          ],
        ),
      ),
    );
  }
}
