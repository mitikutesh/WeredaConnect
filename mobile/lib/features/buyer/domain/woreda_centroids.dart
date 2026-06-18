import 'package:wereda_connect/core/models/crop_listing.dart' show LatLng;

/// Approximate centroids (decimal degrees) for major Ethiopian woredas, towns,
/// and market cities.
///
/// Used to estimate the distance from a crop's real GPS location to a buyer's
/// delivery destination — which is stored only as a free-text woreda name.
/// Common transliteration variants and Oromo names map to the same point.
///
/// NOTE: these coordinates are hand-curated approximate centroids and should be
/// verified against an authoritative source before being relied on for anything
/// beyond relevance ranking. Names entered in Ethiopic script, or woredas not
/// listed here, normalise to an empty/unknown key and fall back to the
/// woreda-string proximity proxy in `MatchScorer.geoScore`.
class WoredaCentroids {
  WoredaCentroids._();

  static LatLng _ll(double lat, double lon) => LatLng()
    ..latitude = lat
    ..longitude = lon;

  /// Keys MUST be pre-normalised (lowercase, letters only) — see [normalize].
  static final Map<String, LatLng> _byName = {
    'addisababa': _ll(9.03, 38.74),
    'addisabeba': _ll(9.03, 38.74),
    'finfinne': _ll(9.03, 38.74),
    'adama': _ll(8.54, 39.27),
    'nazret': _ll(8.54, 39.27),
    'nazreth': _ll(8.54, 39.27),
    'bishoftu': _ll(8.75, 38.98),
    'debrezeit': _ll(8.75, 38.98),
    'hawassa': _ll(7.05, 38.47),
    'awasa': _ll(7.05, 38.47),
    'diredawa': _ll(9.59, 41.86),
    'mekelle': _ll(13.49, 39.47),
    'mekele': _ll(13.49, 39.47),
    'bahirdar': _ll(11.59, 37.39),
    'gondar': _ll(12.60, 37.47),
    'gonder': _ll(12.60, 37.47),
    'jimma': _ll(7.67, 36.83),
    'jima': _ll(7.67, 36.83),
    'dessie': _ll(11.13, 39.63),
    'shashemene': _ll(7.20, 38.60),
    'assosa': _ll(10.07, 34.53),
    'asosa': _ll(10.07, 34.53),
    'debremarkos': _ll(10.33, 37.72),
    'nekemte': _ll(9.09, 36.55),
    'arbaminch': _ll(6.03, 37.55),
    'hosaena': _ll(7.55, 37.85),
    'hossana': _ll(7.55, 37.85),
    'wolaitasodo': _ll(6.90, 37.75),
    'sodo': _ll(6.90, 37.75),
    'harar': _ll(9.31, 42.12),
    'debrebirhan': _ll(9.68, 39.53),
    'ambo': _ll(8.98, 37.86),
    'woliso': _ll(8.53, 37.97),
    'balerobe': _ll(7.13, 40.00),
    'robe': _ll(7.13, 40.00),
    'jijiga': _ll(9.35, 42.80),
    'adigrat': _ll(14.27, 39.46),
    'axum': _ll(14.13, 38.72),
    'aksum': _ll(14.13, 38.72),
    'gambela': _ll(8.25, 34.58),
  };

  /// Normalise a free-text woreda/town name to a lookup key: lowercase and
  /// strip everything except `a`–`z` (spaces, hyphens, digits, punctuation,
  /// and non-Latin scripts collapse away).
  static String normalize(String name) =>
      name.toLowerCase().replaceAll(RegExp('[^a-z]'), '');

  /// Centroid for [woreda], or `null` if it is not in the curated table.
  static LatLng? lookup(String woreda) => _byName[normalize(woreda)];
}
