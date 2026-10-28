import 'package:wereda_connect/shared/services/place_normalizer.dart';

/// A selectable Ethiopian place (woreda / town) used by the place selector.
///
/// Reference data loaded from the bundled `assets/data/ethiopia_woredas.json`
/// asset — this is NOT a synced entity, so it has no Isar schema or web
/// counterpart and is safe to use on both mobile and web.
///
/// `am` (Amharic / Ge'ez) and `om` (Afaan Oromo) may be empty: GeoNames
/// coverage for translated names is sparse, especially for small places.
/// Callers must tolerate empty values.
///
/// Label a place with [displayName] rather than [en]: [en] is the raw
/// GeoNames form ("Mek’ī", "Nazrēt"), kept because legacy free-text fields
/// store it.
class Place {
  const Place({
    required this.id,
    required this.en,
    this.display = '',
    this.am = '',
    this.om = '',
    this.alts = const [],
    this.region = '',
    this.zone = '',
    this.lat,
    this.lng,
    this.population = 0,
  });

  /// Stable identifier (GeoNames id, prefixed `gn-`). Stored on entities to
  /// remember the picked place; never shown to the user.
  final String id;

  /// Canonical English / Latin name. This is the value written to the legacy
  /// free-text fields so server matching and centroid lookup keep working.
  final String en;

  /// Curated English display name from the overrides file, set only where
  /// cleaning [en] is not enough (the current official name: "Adama" for
  /// "Nazrēt", "Batu" for "Ziway"); empty otherwise. Use [displayEn].
  final String display;

  /// Amharic (Ge'ez) name; empty when unknown.
  final String am;

  /// Afaan Oromo (Latin) name; empty when unknown.
  final String om;

  /// Alternate spellings / transliterations used for matching only.
  final List<String> alts;

  /// Region (admin-1) name; used to disambiguate duplicate names in the UI.
  final String region;

  /// Zone (admin-2) name; usually empty (not coded by the upstream source).
  final String zone;

  /// Centroid latitude / longitude, when known.
  final double? lat;
  final double? lng;

  /// Population, when known; 0 otherwise. Used as a ranking tiebreaker.
  final int population;

  /// Parses one entry from `ethiopia_woredas.json`. Empty/default keys are
  /// omitted in the asset to save space, so every field except `id`/`en` is
  /// defaulted defensively.
  factory Place.fromJson(Map<String, dynamic> json) {
    final rawAlts = json['alts'];
    return Place(
      id: json['id'] as String,
      en: json['en'] as String,
      display: (json['display'] as String?) ?? '',
      am: (json['am'] as String?) ?? '',
      om: (json['om'] as String?) ?? '',
      alts: rawAlts is List
          ? rawAlts.map((e) => e.toString()).toList(growable: false)
          : const [],
      region: (json['region'] as String?) ?? '',
      zone: (json['zone'] as String?) ?? '',
      lat: (json['lat'] as num?)?.toDouble(),
      lng: (json['lng'] as num?)?.toDouble(),
      population: (json['population'] as num?)?.toInt() ?? 0,
    );
  }

  /// English (Latin) name for display: [display] when curated, else [en]
  /// without transliteration marks ("Mek’ī" gives "Meki").
  String get displayEn => display.isNotEmpty ? display : cleanPlaceName(en);

  /// Amharic name when it is written in Ge'ez; empty otherwise. GeoNames has
  /// some romanized `am` values ("Ādīs Ābeba") that are not worth showing.
  String get amGeez => _hasEthiopic(am) ? am : '';

  /// The name to show for [languageCode] (`en`, `am` or `om`): the Amharic or
  /// Oromo name when known, else [displayEn]. Unknown codes get [displayEn].
  String displayName(String languageCode) {
    final translated = switch (languageCode) {
      'am' => amGeez,
      'om' => om.trim(),
      _ => '',
    };
    return translated.isNotEmpty ? translated : displayEn;
  }

  static bool _hasEthiopic(String text) => text.runes.any(
        (r) =>
            (r >= 0x1200 && r <= 0x139F) ||
            (r >= 0x2D80 && r <= 0x2DDF) ||
            (r >= 0xAB00 && r <= 0xAB2F),
      );
}
