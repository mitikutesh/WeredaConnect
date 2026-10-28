import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart' show AssetBundle, rootBundle;

import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/shared/services/place_normalizer.dart';

/// In-memory index over the bundled Ethiopian places dataset.
///
/// Loads `assets/data/ethiopia_woredas.json` once (lazily, on first use) and
/// answers typeahead queries. Pure Dart + `rootBundle`, so it works identically
/// on mobile and web (no Isar, no `dart:io`). The dataset is small (~3.9k rows),
/// so a linear scan per query is well under a frame budget; no isolate needed.
class PlaceRepository {
  PlaceRepository();

  /// Shared instance used by the UI. Tests construct their own with
  /// [loadFromStringForTest] to avoid coupling to the bundled asset.
  static final PlaceRepository instance = PlaceRepository();

  static const String assetPath = 'assets/data/ethiopia_woredas.json';

  /// The credit the GeoNames licence (CC BY 4.0) asks for, shown in the About
  /// dialog. A proper name and a licence, so it is never translated.
  static const String attribution = '\u00A9 GeoNames (CC BY 4.0)';

  List<_IndexedPlace>? _index;
  Map<String, Place> _byId = const {};
  Map<String, Place> _byNormalizedName = const {};

  bool get isLoaded => _index != null;

  /// Every place, in the dataset's order; empty until loaded. The live trip
  /// map labels its towns from it (LT-10).
  List<Place> get places => _places;
  List<Place> _places = const [];

  /// Loads and indexes the dataset if not already loaded. Safe to call repeatedly.
  Future<void> ensureLoaded({AssetBundle? bundle}) async {
    if (_index != null) return;
    final raw = await (bundle ?? rootBundle).loadString(assetPath);
    _loadFromString(raw);
  }

  /// Test seam: load from a raw JSON string instead of the bundled asset.
  @visibleForTesting
  void loadFromStringForTest(String rawJson) => _loadFromString(rawJson);

  void _loadFromString(String rawJson) {
    final decoded = (jsonDecode(rawJson) as List).cast<Map<String, dynamic>>();
    final places = decoded.map(Place.fromJson).toList(growable: false);

    final index = <_IndexedPlace>[];
    final byId = <String, Place>{};
    for (final place in places) {
      byId[place.id] = place;
      // Keys drop transliteration marks, so "Meki" finds "Mek’ī"; the
      // curated display name ("Adama" for "Nazrēt") is a term too.
      final terms = <String>{
        placeSearchKey(place.en),
        if (place.display.isNotEmpty) placeSearchKey(place.display),
        if (place.am.isNotEmpty) placeSearchKey(place.am),
        if (place.om.isNotEmpty) placeSearchKey(place.om),
        for (final alt in place.alts)
          if (alt.isNotEmpty) placeSearchKey(alt),
      }.toList()
        ..removeWhere((t) => t.isEmpty);
      index.add(_IndexedPlace(place, terms));
    }

    // Build the display-name lookup from most- to least-populous so that a
    // shared name (e.g. "Addis Ababa") resolves to the largest place.
    final byName = <String, Place>{};
    final sortedByPop = [...index]
      ..sort((a, b) => b.place.population.compareTo(a.place.population));
    for (final item in sortedByPop) {
      for (final term in item.terms) {
        byName.putIfAbsent(term, () => item.place);
      }
    }

    _index = index;
    _places = List.unmodifiable(places);
    _byId = byId;
    _byNormalizedName = byName;
  }

  /// Returns the place with [id], or null.
  Place? byId(String id) => _byId[id];

  /// Resolves an existing free-text value (a legacy stored woreda, or
  /// `authState.woreda`) to a [Place] by exact normalized name match across
  /// en/display/am/om/alts. Returns null for off-list values.
  Place? findByDisplayName(String name) {
    final normalized = placeSearchKey(name);
    if (normalized.isEmpty) return null;
    return _byNormalizedName[normalized];
  }

  /// Up to [limit] places matching [query]. Prefix matches rank above substring
  /// matches; within each group, more-populous places rank first.
  List<Place> search(String query, {int limit = 8}) {
    final index = _index;
    if (index == null) return const [];
    final normalized = placeSearchKey(query);
    if (normalized.isEmpty) return const [];

    final prefix = <_IndexedPlace>[];
    final substring = <_IndexedPlace>[];
    for (final item in index) {
      switch (item.matchKind(normalized)) {
        case _MatchKind.prefix:
          prefix.add(item);
        case _MatchKind.substring:
          substring.add(item);
        case _MatchKind.none:
          break;
      }
    }

    int byPopulationDesc(_IndexedPlace a, _IndexedPlace b) =>
        b.place.population.compareTo(a.place.population);
    prefix.sort(byPopulationDesc);
    substring.sort(byPopulationDesc);

    final results = <Place>[];
    for (final item in prefix) {
      results.add(item.place);
      if (results.length >= limit) return results;
    }
    for (final item in substring) {
      results.add(item.place);
      if (results.length >= limit) return results;
    }
    return results;
  }
}

enum _MatchKind { none, substring, prefix }

class _IndexedPlace {
  _IndexedPlace(this.place, this.terms);

  final Place place;
  final List<String> terms; // placeSearchKey of en/display/am/om/alts

  _MatchKind matchKind(String normalizedQuery) {
    var substring = false;
    for (final term in terms) {
      if (term.startsWith(normalizedQuery)) return _MatchKind.prefix;
      if (!substring && term.contains(normalizedQuery)) substring = true;
    }
    return substring ? _MatchKind.substring : _MatchKind.none;
  }
}
