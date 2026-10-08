import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Ethiopian crop types supported in crop listings.
///
/// Each crop has a localized display name (Amharic, Afan Oromo, English)
/// and an icon for visual identification (important for low-literacy users).
enum CropType {
  teff,
  wheat,
  barley,
  maize,
  chickpea,
  lentils,
  sorghum,
  coffee,
  sesame;

  /// Get the localized display name for this crop type.
  String displayName(AppLocalizations l10n) {
    switch (this) {
      case CropType.teff:
        return l10n.cropTypeTeff;
      case CropType.wheat:
        return l10n.cropTypeWheat;
      case CropType.barley:
        return l10n.cropTypeBarley;
      case CropType.maize:
        return l10n.cropTypeMaize;
      case CropType.chickpea:
        return l10n.cropTypeChickpea;
      case CropType.lentils:
        return l10n.cropTypeLentils;
      case CropType.sorghum:
        return l10n.cropTypeSorghum;
      case CropType.coffee:
        return l10n.cropTypeCoffee;
      case CropType.sesame:
        return l10n.cropTypeSesame;
    }
  }

  /// Icon for visual identification (low-literacy friendly).
  IconData get icon {
    switch (this) {
      case CropType.teff:
        return Icons.grass;
      case CropType.wheat:
        return Icons.grain;
      case CropType.barley:
        return Icons.eco;
      case CropType.maize:
        return Icons.energy_savings_leaf;
      case CropType.chickpea:
        return Icons.circle;
      case CropType.lentils:
        return Icons.lens;
      case CropType.sorghum:
        return Icons.spa;
      case CropType.coffee:
        return Icons.coffee;
      case CropType.sesame:
        return Icons.scatter_plot;
    }
  }

  /// Parse a crop type from its Isar-stored string key.
  static CropType fromKey(String key) {
    return CropType.values.firstWhere(
      (type) => type.name == key,
      orElse: () => CropType.teff,
    );
  }

  /// Parse a stored key, or null when it is not one of the nine crops. Unlike
  /// [fromKey] this never turns an unknown value into teff, so user-facing
  /// text can fall back to the raw key instead of a wrong translation.
  static CropType? tryFromKey(String key) {
    final normalized = key.trim().toLowerCase();
    for (final type in CropType.values) {
      if (type.name == normalized) return type;
    }
    return null;
  }

  /// Localized name for a stored crop key: "teff" reads as Teff, ጤፍ or
  /// Xaafii depending on the locale; an unknown key is shown as-is. Every
  /// card, summary row and activity line that prints a crop must use this
  /// rather than the raw key (SIM-22).
  static String labelFor(String key, AppLocalizations l10n) =>
      tryFromKey(key)?.displayName(l10n) ?? key;

  /// Lowercased names a buyer may type for each crop: the stored key plus
  /// the display name in every supported locale. Built once, on first use,
  /// so a search keystroke does not rebuild the labels for every listing.
  static final Map<CropType, List<String>> _searchLabels = {
    for (final type in CropType.values)
      type: [
        type.name,
        for (final locale in AppLocalizations.supportedLocales)
          type.displayName(lookupAppLocalizations(locale)).toLowerCase(),
      ],
  };

  /// Keys of every crop whose stored key, or name in any supported language
  /// (English, Amharic, Afan Oromo), contains [term] case-insensitively.
  /// All locales are searched, not only the active one: a buyer on an
  /// Amharic UI may type "teff" and one on an English UI may type "ጤፍ"
  /// (SIM-51). A blank term matches nothing.
  static Set<String> keysMatching(String term) {
    final needle = term.trim().toLowerCase();
    if (needle.isEmpty) return const <String>{};
    return {
      for (final entry in _searchLabels.entries)
        if (entry.value.any((label) => label.contains(needle))) entry.key.name,
    };
  }
}
