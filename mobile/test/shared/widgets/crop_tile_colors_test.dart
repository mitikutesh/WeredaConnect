import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// The boards' crop tiles: Market (wheat cream, maize green, teff sand) and
/// My Crops (chickpea on the olive tint).
void main() {
  test('each crop gets its board colours', () {
    expect(cropTileColors('wheat').background, const Color(0xFFF4EBCF));
    expect(cropTileColors('wheat').foreground, AppTheme.money);
    expect(cropTileColors('teff').background, const Color(0xFFEFE6D8));
    expect(cropTileColors('teff').foreground, AppTheme.money);
    expect(cropTileColors('chickpea').background, const Color(0xFFE6EFD9));
    expect(cropTileColors('chickpea').foreground, AppTheme.fieldGreen);
    expect(cropTileColors('maize').background, AppTheme.fieldGreenTint);
  });

  test('an unknown or missing crop is on field green tint', () {
    expect(cropTileColors(null).background, AppTheme.fieldGreenTint);
    expect(cropTileColors('quinoa').foreground, AppTheme.fieldGreen);
  });
}
