/// A selectable Ethiopian place (woreda / town) used by the place selector.
///
/// Reference data loaded from the bundled `assets/data/ethiopia_woredas.json`
/// asset — this is NOT a synced entity, so it has no Isar schema or web
/// counterpart and is safe to use on both mobile and web.
///
/// `am` (Amharic / Ge'ez) and `om` (Afaan Oromo) may be empty: GeoNames
/// coverage for translated names is sparse, especially for small places.
/// Callers must tolerate empty values.
class Place {
  const Place({
    required this.id,
    required this.en,
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
}
